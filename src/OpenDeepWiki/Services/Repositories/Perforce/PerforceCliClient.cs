using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OpenDeepWiki.Services.Repositories.Perforce;

public sealed class PerforceCliClient(
    IPerforceCommandRunner commandRunner,
    IOptionsMonitor<PerforceOptions> optionsMonitor,
    ILogger<PerforceCliClient> logger) : IPerforceClient
{
    private static readonly Regex IndexedFileField = new(
        "^(depotFile|action|type)([0-9]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<long?> GetLatestChangelistAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var result = await commandRunner.RunTaggedAsync(
            workspaceRoot,
            ["changes", "-m", "1", "-s", "submitted", "...#have"],
            cancellationToken);

        EnsureSuccess(result, "query latest changelist");
        return ParseChangelists(result.StandardOutput).FirstOrDefault()?.Number;
    }

    public async Task<IReadOnlyList<PerforceChangelist>> GetChangelistsAsync(
        string workspaceRoot,
        long afterChangelist,
        long throughChangelist,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (afterChangelist < 0 || throughChangelist <= 0 || throughChangelist <= afterChangelist)
        {
            return [];
        }

        var result = await commandRunner.RunTaggedAsync(
            workspaceRoot,
            [
                "changes", "-s", "submitted", "-L", "-m", Math.Max(1, maxResults).ToString(CultureInfo.InvariantCulture),
                $"...@{checked(afterChangelist + 1).ToString(CultureInfo.InvariantCulture)},@{throughChangelist.ToString(CultureInfo.InvariantCulture)}"
            ],
            cancellationToken);

        EnsureSuccess(result, $"query changelist range ({afterChangelist}, {throughChangelist}]");
        return ParseChangelists(result.StandardOutput)
            .Where(change => change.Number > afterChangelist && change.Number <= throughChangelist)
            .OrderBy(change => change.Number)
            .ToArray();
    }

    public async Task<IReadOnlyList<PerforceFileChange>> GetFileChangesAsync(
        string workspaceRoot,
        long changelist,
        CancellationToken cancellationToken = default)
    {
        if (changelist <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(changelist));
        }

        var describeResult = await commandRunner.RunTaggedAsync(
            workspaceRoot,
            ["describe", "-s", changelist.ToString(CultureInfo.InvariantCulture)],
            cancellationToken);
        EnsureSuccess(describeResult, $"describe changelist {changelist}");

        var describedFiles = ParseDescribedFiles(describeResult.StandardOutput);
        if (describedFiles.Count == 0)
        {
            return [];
        }

        var mappedPaths = await MapWorkspacePathsAsync(
            workspaceRoot,
            describedFiles.Select(file => file.DepotPath).ToArray(),
            cancellationToken);

        var changes = new List<PerforceFileChange>(describedFiles.Count);
        foreach (var file in describedFiles)
        {
            if (!mappedPaths.TryGetValue(file.DepotPath, out var relativePath))
            {
                logger.LogWarning(
                    "Skipping Perforce file outside or unmapped from workspace. Changelist: {Changelist}, DepotPath: {DepotPath}",
                    changelist, file.DepotPath);
                continue;
            }

            changes.Add(new PerforceFileChange(
                changelist,
                file.DepotPath,
                relativePath,
                file.Action,
                file.FileType));
        }

        return changes;
    }

    internal static IReadOnlyList<PerforceChangelist> ParseChangelists(string output)
    {
        var changes = new List<PerforceChangelist>();
        long? number = null;
        var user = string.Empty;
        var description = string.Empty;

        void Flush()
        {
            if (number.HasValue)
            {
                changes.Add(new PerforceChangelist(number.Value, user, description));
            }
        }

        foreach (var (key, value) in ParseTaggedFields(output))
        {
            if (key.Equals("change", StringComparison.Ordinal))
            {
                Flush();
                number = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
                user = string.Empty;
                description = string.Empty;
            }
            else if (key.Equals("user", StringComparison.Ordinal))
            {
                user = value;
            }
            else if (key.Equals("desc", StringComparison.Ordinal))
            {
                description = value;
            }
        }

        Flush();
        return changes;
    }

    internal static IReadOnlyList<DescribedFile> ParseDescribedFiles(string output)
    {
        var indexed = new SortedDictionary<int, DescribedFileBuilder>();
        var currentUnindexed = -1;

        foreach (var (rawKey, value) in ParseTaggedFields(output))
        {
            var match = IndexedFileField.Match(rawKey);
            var key = match.Success ? match.Groups[1].Value : rawKey;
            int index;

            if (match.Success)
            {
                index = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else if (key.Equals("depotFile", StringComparison.Ordinal))
            {
                index = ++currentUnindexed;
            }
            else
            {
                index = currentUnindexed;
            }

            if (index < 0 || key is not ("depotFile" or "action" or "type"))
            {
                continue;
            }

            if (!indexed.TryGetValue(index, out var builder))
            {
                builder = new DescribedFileBuilder();
                indexed[index] = builder;
            }

            switch (key)
            {
                case "depotFile":
                    builder.DepotPath = value;
                    break;
                case "action":
                    builder.Action = value;
                    break;
                case "type":
                    builder.FileType = value;
                    break;
            }
        }

        return indexed.Values
            .Where(file => !string.IsNullOrWhiteSpace(file.DepotPath))
            .Select(file => new DescribedFile(file.DepotPath, file.Action, file.FileType))
            .ToArray();
    }

    internal static IReadOnlyList<(string Key, string Value)> ParseTaggedFields(string output)
    {
        var fields = new List<(string Key, string Value)>();
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("... ", StringComparison.Ordinal))
            {
                var content = line[4..];
                var separator = content.IndexOf(' ');
                if (separator < 0)
                {
                    fields.Add((content, string.Empty));
                }
                else
                {
                    fields.Add((content[..separator], content[(separator + 1)..]));
                }

                continue;
            }

            // p4 tagged multi-line values (especially desc with -L) continue without a "... " prefix.
            if (fields.Count > 0)
            {
                var last = fields[^1];
                fields[^1] = (last.Key, string.IsNullOrEmpty(last.Value) ? line : last.Value + "\n" + line);
            }
        }

        return fields;
    }

    private async Task<Dictionary<string, string>> MapWorkspacePathsAsync(
        string workspaceRoot,
        IReadOnlyList<string> depotPaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var batchSize = Math.Clamp(optionsMonitor.CurrentValue.WhereBatchSize, 1, 256);

        foreach (var batch in depotPaths.Distinct(StringComparer.Ordinal).Chunk(batchSize))
        {
            var arguments = new List<string>(batch.Length + 1) { "where" };
            arguments.AddRange(batch);
            var whereResult = await commandRunner.RunTaggedAsync(workspaceRoot, arguments, cancellationToken);

            // p4 where can return mapped records and an error for another unmapped filespec in the same batch.
            // Preserve the valid mappings and let the caller log individual omissions.
            var mappings = ParseWhereMappings(whereResult.StandardOutput);
            if (!whereResult.IsSuccess && mappings.Count == 0)
            {
                EnsureSuccess(whereResult, "map depot files to workspace");
            }

            foreach (var mapping in mappings)
            {
                if (TryMakeRelativePath(workspaceRoot, mapping.LocalPath, out var relativePath))
                {
                    result[mapping.DepotPath] = relativePath;
                }
            }
        }

        return result;
    }

    internal static IReadOnlyList<WhereMapping> ParseWhereMappings(string output)
    {
        var mappings = new List<WhereMapping>();
        string? depotPath = null;
        string? localPath = null;
        var unmap = false;

        void Flush()
        {
            if (!unmap && !string.IsNullOrWhiteSpace(depotPath) && !string.IsNullOrWhiteSpace(localPath))
            {
                mappings.Add(new WhereMapping(depotPath, localPath));
            }
        }

        foreach (var (key, value) in ParseTaggedFields(output))
        {
            if (key.Equals("depotFile", StringComparison.Ordinal))
            {
                Flush();
                depotPath = value;
                localPath = null;
                unmap = false;
            }
            else if (key.Equals("path", StringComparison.Ordinal))
            {
                localPath = value;
            }
            else if (key.Equals("unmap", StringComparison.Ordinal))
            {
                unmap = true;
            }
        }

        Flush();
        return mappings;
    }

    private static bool TryMakeRelativePath(string workspaceRoot, string localPath, out string relativePath)
    {
        var fullRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.GetFullPath(localPath);
        var candidate = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
        if (candidate.Equals("..", StringComparison.Ordinal)
            || candidate.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(candidate))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = candidate;
        return true;
    }

    private static void EnsureSuccess(PerforceCommandResult result, string operation)
    {
        // Prefer exit code as the primary success signal. Some p4 builds surface informational
        // messages as tagged "... data" fields even when the command succeeds.
        if (result.IsSuccess)
        {
            return;
        }

        var taggedError = ParseTaggedFields(result.StandardOutput)
            .FirstOrDefault(field => field.Key.Equals("data", StringComparison.Ordinal)).Value;
        var detail = !string.IsNullOrWhiteSpace(taggedError)
            ? taggedError
            : result.StandardError.Trim();
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = $"p4 exited with code {result.ExitCode}";
        }

        throw new PerforceCommandException($"Failed to {operation}: {detail}");
    }

    internal sealed record DescribedFile(string DepotPath, string Action, string FileType);
    internal sealed record WhereMapping(string DepotPath, string LocalPath);

    private sealed class DescribedFileBuilder
    {
        public string DepotPath { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
    }
}
