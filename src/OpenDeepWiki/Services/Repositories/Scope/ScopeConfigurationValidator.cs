using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenDeepWiki.Services.Repositories.Scope;

public interface IScopeConfigurationValidator
{
    ScopeValidationResult Validate(ScopeConfigurationDocument document);
}

public sealed class ScopeConfigurationValidator : IScopeConfigurationValidator
{
    private static readonly Regex ScopeIdPattern = new(
        @"^[a-zA-Z][a-zA-Z0-9._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnsafeGlob = new(
        @"(\.\.|\\\\|[A-Za-z]:)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ScopeValidationResult Validate(ScopeConfigurationDocument document)
    {
        var result = new ScopeValidationResult();
        if (document.SchemaVersion != 1)
        {
            result.Errors.Add($"Unsupported schemaVersion: {document.SchemaVersion}. Only version 1 is supported.");
        }

        if (document.DocumentScopes is null || document.DocumentScopes.Count == 0)
        {
            result.Errors.Add("At least one documentScopes entry is required.");
            return result;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<(string Id, string Root, int Specificity)>();

        foreach (var scope in document.DocumentScopes)
        {
            if (string.IsNullOrWhiteSpace(scope.Id))
            {
                result.Errors.Add("documentScopes.id is required.");
                continue;
            }

            var id = scope.Id.Trim();
            if (!ScopeIdPattern.IsMatch(id))
            {
                result.Errors.Add($"documentScopes.id '{id}' is invalid. Use letters, digits, '.', '_' or '-'.");
            }

            if (!ids.Add(id))
            {
                result.Errors.Add($"Duplicate documentScopes.id '{id}'.");
            }

            // root 允许空字符串表示仓库根；null 才是缺失。
            if (scope.Root is null)
            {
                result.Errors.Add($"documentScopes '{id}' root is required (use \"\" for repository root).");
                continue;
            }

            if (scope.Root.Length > 0)
            {
                ValidateRelativePath(scope.Root, $"documentScopes '{id}' root", result);
            }

            ValidateGlobs(scope.IncludedPathGlobs, $"documentScopes '{id}' includedPathGlobs", result, allowNull: true);
            ValidateGlobs(scope.ExcludedPathGlobs, $"documentScopes '{id}' excludedPathGlobs", result, allowNull: true);
            ValidateSuffixes(scope.IncludedSuffixes, $"documentScopes '{id}' includedSuffixes", result);

            if (scope.AcceptAllTextFiles && scope.IncludedSuffixes is { Count: > 0 })
            {
                result.Warnings.Add(
                    $"documentScopes '{id}' sets acceptAllTextFiles with includedSuffixes; suffixes will be ignored.");
            }

            var root = ScopePathUtility.NormalizeRoot(scope.Root);
            roots.Add((id, root, ScopePathUtility.RootSpecificity(root)));
        }

        DetectAmbiguousOverlaps(roots, result);

        if (document.ContextScope is not null)
        {
            if (document.ContextScope.Roots is null)
            {
                result.Errors.Add("contextScope.roots must not be null (use [] for empty).");
            }
            else
            {
                foreach (var root in document.ContextScope.Roots)
                {
                    if (root is null)
                    {
                        result.Errors.Add("contextScope.roots contains null.");
                        continue;
                    }

                    if (root.Length > 0)
                    {
                        ValidateRelativePath(root, "contextScope.roots", result);
                    }
                }
            }

            ValidateGlobs(document.ContextScope.IncludedPathGlobs, "contextScope.includedPathGlobs", result, allowNull: true);
            ValidateGlobs(document.ContextScope.ExcludedPathGlobs, "contextScope.excludedPathGlobs", result, allowNull: true);

            if (document.ContextScope.MaxFileBytes <= 0)
            {
                result.Errors.Add("contextScope.maxFileBytes must be positive.");
            }
        }

        if (document.ChangeTriggerScope is not null)
        {
            if (document.ChangeTriggerScope.AdditionalRoots is null)
            {
                result.Errors.Add("changeTriggerScope.additionalRoots must not be null (use [] for empty).");
            }
            else
            {
                foreach (var root in document.ChangeTriggerScope.AdditionalRoots)
                {
                    if (root is null)
                    {
                        result.Errors.Add("changeTriggerScope.additionalRoots contains null.");
                        continue;
                    }

                    if (root.Length > 0)
                    {
                        ValidateRelativePath(root, "changeTriggerScope.additionalRoots", result);
                    }
                }
            }

            ValidateGlobs(document.ChangeTriggerScope.IncludedPathGlobs, "changeTriggerScope.includedPathGlobs", result, allowNull: true);
            ValidateGlobs(document.ChangeTriggerScope.ExcludedPathGlobs, "changeTriggerScope.excludedPathGlobs", result, allowNull: true);
        }

        return result;
    }

    private static void DetectAmbiguousOverlaps(
        List<(string Id, string Root, int Specificity)> roots,
        ScopeValidationResult result)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                var left = roots[i];
                var right = roots[j];
                if (left.Root.Equals(right.Root, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add(
                        $"Overlapping documentScopes roots cannot be disambiguated: '{left.Id}' and '{right.Id}' share root '{left.Root}'.");
                    continue;
                }

                // Nested roots are fine: most specific root wins.
                var nested = ScopePathUtility.IsUnderRoot(left.Root, right.Root)
                             || ScopePathUtility.IsUnderRoot(right.Root, left.Root);
                if (nested)
                {
                    continue;
                }

                // Sibling roots with equal prefix length that cross via globs are not automatically rejected here;
                // root identity is the disambiguation key.
            }
        }
    }

    private static void ValidateRelativePath(string path, string field, ScopeValidationResult result)
    {
        var safety = ScopePathUtility.EvaluateSafety(path);
        if (safety is not null)
        {
            result.Errors.Add($"{field} rejected: {safety.ReasonCode} ({safety.Detail}).");
            return;
        }

        var normalized = ScopePathUtility.NormalizeRelativePath(path);
        if (string.IsNullOrEmpty(normalized) && field.Contains("root", StringComparison.OrdinalIgnoreCase))
        {
            // empty root means repository root — allowed only as explicit ""
            return;
        }
    }

    private static void ValidateGlobs(
        IEnumerable<string>? globs,
        string field,
        ScopeValidationResult result,
        bool allowNull = false)
    {
        if (globs is null)
        {
            if (!allowNull)
            {
                result.Errors.Add($"{field} must not be null (use [] for empty).");
            }

            return;
        }

        foreach (var glob in globs)
        {
            if (glob is null || string.IsNullOrWhiteSpace(glob))
            {
                result.Errors.Add($"{field} contains an empty glob.");
                continue;
            }

            if (UnsafeGlob.IsMatch(glob) || glob.Contains("..", StringComparison.Ordinal))
            {
                result.Errors.Add($"{field} glob '{glob}' contains unsafe path elements.");
            }
        }
    }

    private static void ValidateSuffixes(IEnumerable<string>? suffixes, string field, ScopeValidationResult result)
    {
        if (suffixes is null)
        {
            return;
        }

        foreach (var suffix in suffixes)
        {
            if (suffix is null || string.IsNullOrWhiteSpace(suffix))
            {
                result.Errors.Add($"{field} contains an empty suffix.");
                continue;
            }

            if (suffix.Contains('/') || suffix.Contains('\\') || suffix.Contains("..", StringComparison.Ordinal))
            {
                result.Errors.Add($"{field} suffix '{suffix}' is invalid.");
            }
        }
    }
}

public interface IScopeConfigurationNormalizer
{
    ResolvedScopeConfiguration Normalize(ScopeConfigurationDocument document);
}

public sealed class ScopeConfigurationNormalizer : IScopeConfigurationNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ResolvedScopeConfiguration Normalize(ScopeConfigurationDocument document)
    {
        var documentScopes = document.DocumentScopes
            .Select(scope =>
            {
                var suffixes = scope.AcceptAllTextFiles
                    ? Array.Empty<string>()
                    : (scope.IncludedSuffixes is { Count: > 0 }
                        ? NormalizeSuffixes(scope.IncludedSuffixes)
                        : ScopeDefaults.DefaultDocumentSuffixes.ToArray());

                return new ResolvedDocumentScope
                {
                    Id = scope.Id.Trim(),
                    Root = ScopePathUtility.NormalizeRoot(scope.Root),
                    DisplayName = string.IsNullOrWhiteSpace(scope.DisplayName) ? null : scope.DisplayName.Trim(),
                    IncludedPathGlobs = NormalizeGlobs(scope.IncludedPathGlobs, defaultGlob: "**"),
                    ExcludedPathGlobs = NormalizeGlobs(scope.ExcludedPathGlobs, defaultGlob: null),
                    IncludedSuffixes = suffixes,
                    AcceptAllTextFiles = scope.AcceptAllTextFiles
                };
            })
            .OrderBy(scope => scope.Id, StringComparer.Ordinal)
            .ToArray();

        ResolvedContextScope? context = null;
        if (document.ContextScope is not null)
        {
            context = new ResolvedContextScope
            {
                Roots = document.ContextScope.Roots
                    .Select(ScopePathUtility.NormalizeRoot)
                    .Where(root => !string.IsNullOrEmpty(root) || document.ContextScope.Roots.Any(string.IsNullOrWhiteSpace))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ReadOnly = document.ContextScope.ReadOnly,
                PreferTrackedFiles = document.ContextScope.PreferTrackedFiles,
                MaxFileBytes = document.ContextScope.MaxFileBytes <= 0
                    ? 2 * 1024 * 1024
                    : document.ContextScope.MaxFileBytes,
                IncludedPathGlobs = NormalizeGlobs(document.ContextScope.IncludedPathGlobs, "**"),
                ExcludedPathGlobs = NormalizeGlobs(document.ContextScope.ExcludedPathGlobs, null)
            };
        }

        var triggerDto = document.ChangeTriggerScope;
        var trigger = new ResolvedChangeTriggerScope
        {
            // 缺省语义：未配置 changeTriggerScope 时默认继承全部 documentScopes
            InheritsDocumentScopes = triggerDto?.InheritsDocumentScopes ?? true,
            AdditionalRoots = (triggerDto?.AdditionalRoots ?? [])
                .Select(ScopePathUtility.NormalizeRoot)
                .Where(root => root.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IncludedPathGlobs = NormalizeGlobs(triggerDto?.IncludedPathGlobs, "**"),
            ExcludedPathGlobs = NormalizeGlobs(triggerDto?.ExcludedPathGlobs, null)
        };

        var normalizedDocument = new ScopeConfigurationDocument
        {
            SchemaVersion = 1,
            WorkspaceContentPolicy = document.WorkspaceContentPolicy,
            DocumentScopes = documentScopes.Select(scope => new DocumentScopeRuleDto
            {
                Id = scope.Id,
                Root = scope.Root,
                DisplayName = scope.DisplayName,
                IncludedPathGlobs = scope.IncludedPathGlobs.ToList(),
                ExcludedPathGlobs = scope.ExcludedPathGlobs.ToList(),
                IncludedSuffixes = scope.AcceptAllTextFiles ? null : scope.IncludedSuffixes.ToList(),
                AcceptAllTextFiles = scope.AcceptAllTextFiles
            }).ToList(),
            ContextScope = context is null
                ? null
                : new ContextScopeRuleDto
                {
                    Roots = context.Roots.ToList(),
                    ReadOnly = context.ReadOnly,
                    PreferTrackedFiles = context.PreferTrackedFiles,
                    MaxFileBytes = context.MaxFileBytes,
                    IncludedPathGlobs = context.IncludedPathGlobs.ToList(),
                    ExcludedPathGlobs = context.ExcludedPathGlobs.ToList()
                },
            ChangeTriggerScope = new ChangeTriggerScopeRuleDto
            {
                InheritsDocumentScopes = trigger.InheritsDocumentScopes,
                AdditionalRoots = trigger.AdditionalRoots.ToList(),
                IncludedPathGlobs = trigger.IncludedPathGlobs.ToList(),
                ExcludedPathGlobs = trigger.ExcludedPathGlobs.ToList()
            }
        };

        var json = JsonSerializer.Serialize(normalizedDocument, JsonOptions);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        return new ResolvedScopeConfiguration
        {
            SchemaVersion = 1,
            WorkspaceContentPolicy = document.WorkspaceContentPolicy,
            DocumentScopes = documentScopes,
            ContextScope = context,
            ChangeTriggerScope = trigger,
            ContentHash = hash,
            NormalizedJson = json,
            IsLegacyFallback = false
        };
    }

    private static IReadOnlyList<string> NormalizeGlobs(IEnumerable<string>? globs, string? defaultGlob)
    {
        var list = (globs ?? [])
            .Where(glob => !string.IsNullOrWhiteSpace(glob))
            .Select(glob => glob.Replace('\\', '/').Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(glob => glob, StringComparer.Ordinal)
            .ToList();

        if (list.Count == 0 && defaultGlob is not null)
        {
            list.Add(defaultGlob);
        }

        return list;
    }

    private static string[] NormalizeSuffixes(IEnumerable<string> suffixes)
        => suffixes
            .Where(suffix => !string.IsNullOrWhiteSpace(suffix))
            .Select(suffix =>
            {
                var value = suffix.Trim();
                return value.StartsWith('.') ? value : "." + value;
            })
            // longest first for stable serialization; matching uses longest-match anyway
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(suffix => suffix.Length)
            .ThenBy(suffix => suffix, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
