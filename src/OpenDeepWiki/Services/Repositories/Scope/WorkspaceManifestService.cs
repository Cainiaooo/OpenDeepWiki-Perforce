using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenDeepWiki.Services.Repositories.Scope;

/// <summary>
/// Document/Context 的 tracked/#have 清单。不从磁盘任意枚举文本文件。
/// </summary>
public interface IWorkspaceManifestService
{
    WorkspaceManifest BuildManifest(
        string repositoryId,
        string branchId,
        int? scopeConfigurationVersion,
        string? targetChangelist,
        IEnumerable<WorkspaceManifestEntry> trackedEntries,
        WorkspaceContentPolicy contentPolicy);

    WorkspaceManifestVerificationResult VerifyReadFiles(
        WorkspaceManifest manifest,
        IEnumerable<WorkspaceReadObservation> observations);
}

public sealed record WorkspaceManifestEntry
{
    public required string RelativePath { get; init; }

    public string? DepotPath { get; init; }

    public string? HaveRevision { get; init; }

    public string? FileType { get; init; }

    public bool IsOpened { get; init; }

    public string? OpenedAction { get; init; }

    public string? LocalDigest { get; init; }

    public bool MatchesHaveContent { get; init; } = true;
}

public sealed class WorkspaceManifest
{
    public required string RepositoryId { get; init; }

    public required string BranchId { get; init; }

    public int? ScopeConfigurationVersion { get; init; }

    public string? TargetChangelist { get; init; }

    public required WorkspaceContentPolicy ContentPolicy { get; init; }

    public required string ManifestHash { get; init; }

    public required IReadOnlyList<WorkspaceManifestEntry> Entries { get; init; }

    public required string ManifestJson { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class WorkspaceReadObservation
{
    public required string RelativePath { get; init; }

    public string? LocalDigest { get; init; }

    public bool? StillMatchesHave { get; init; }

    public bool? IsMissing { get; init; }
}

public sealed class WorkspaceManifestVerificationResult
{
    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class WorkspaceManifestService : IWorkspaceManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public WorkspaceManifest BuildManifest(
        string repositoryId,
        string branchId,
        int? scopeConfigurationVersion,
        string? targetChangelist,
        IEnumerable<WorkspaceManifestEntry> trackedEntries,
        WorkspaceContentPolicy contentPolicy)
    {
        var warnings = new List<string>();
        var entries = trackedEntries
            .Select(entry => entry with
            {
                RelativePath = ScopePathUtility.NormalizeRelativePath(entry.RelativePath)
            })
            .Where(entry => !string.IsNullOrEmpty(entry.RelativePath))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (contentPolicy == WorkspaceContentPolicy.SubmittedHaveOnly)
        {
            var rejectedOpened = entries.Where(entry => entry.IsOpened || !entry.MatchesHaveContent).ToList();
            foreach (var item in rejectedOpened)
            {
                warnings.Add(
                    item.IsOpened
                        ? $"Opened file excluded under SubmittedHaveOnly: {item.RelativePath} ({item.OpenedAction})"
                        : $"Have-content mismatch excluded: {item.RelativePath}");
            }

            entries = entries
                .Where(entry => !entry.IsOpened && entry.MatchesHaveContent)
                .ToList();
        }
        else
        {
            foreach (var opened in entries.Where(entry => entry.IsOpened))
            {
                warnings.Add(
                    $"AllowOpenedFiles: non-reproducible snapshot includes opened {opened.RelativePath} ({opened.OpenedAction}) digest={opened.LocalDigest}");
            }
        }

        var payload = new
        {
            repositoryId,
            branchId,
            scopeConfigurationVersion,
            targetChangelist,
            contentPolicy = contentPolicy.ToString(),
            files = entries.Select(entry => new
            {
                path = entry.RelativePath,
                depot = entry.DepotPath,
                have = entry.HaveRevision,
                type = entry.FileType,
                opened = entry.IsOpened,
                action = entry.OpenedAction,
                digest = entry.LocalDigest
            })
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        return new WorkspaceManifest
        {
            RepositoryId = repositoryId,
            BranchId = branchId,
            ScopeConfigurationVersion = scopeConfigurationVersion,
            TargetChangelist = targetChangelist,
            ContentPolicy = contentPolicy,
            ManifestHash = hash,
            Entries = entries,
            ManifestJson = json,
            Warnings = warnings
        };
    }

    public WorkspaceManifestVerificationResult VerifyReadFiles(
        WorkspaceManifest manifest,
        IEnumerable<WorkspaceReadObservation> observations)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var byPath = manifest.Entries.ToDictionary(
            entry => entry.RelativePath,
            entry => entry,
            StringComparer.OrdinalIgnoreCase);

        foreach (var observation in observations)
        {
            var path = ScopePathUtility.NormalizeRelativePath(observation.RelativePath);
            if (!byPath.TryGetValue(path, out var entry))
            {
                errors.Add($"Workspace drift: read untracked/out-of-manifest file '{path}'.");
                continue;
            }

            if (observation.IsMissing == true)
            {
                errors.Add($"Workspace drift: expected file missing '{path}'.");
                continue;
            }

            if (observation.StillMatchesHave == false)
            {
                errors.Add($"Workspace drift: content no longer matches have for '{path}'.");
                continue;
            }

            if (!string.IsNullOrEmpty(observation.LocalDigest)
                && !string.IsNullOrEmpty(entry.LocalDigest)
                && !string.Equals(observation.LocalDigest, entry.LocalDigest, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Workspace drift: digest changed for '{path}'.");
            }
        }

        return new WorkspaceManifestVerificationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }
}
