using System.Text;
using System.Text.RegularExpressions;

namespace OpenDeepWiki.Services.Repositories.Perforce;

public sealed record PerforceFilterDecision(bool Included, string Reason)
{
    public static PerforceFilterDecision Include() => new(true, "included");
    public static PerforceFilterDecision Exclude(string reason) => new(false, reason);
}

public interface IChangelistFilter
{
    PerforceFilterDecision Evaluate(PerforceChangelist changelist, PerforceFilterOptions options);
}

public interface IFileChangeFilter
{
    PerforceFilterDecision Evaluate(PerforceFileChange file, PerforceFilterOptions options);
}

public interface IChangelistFilterPipeline
{
    PerforceFilterDecision EvaluateChangelist(PerforceChangelist changelist, PerforceFilterOptions options);
    PerforceFilterDecision EvaluateFile(PerforceFileChange file, PerforceFilterOptions options);
}

/// <summary>
/// Composable AND pipeline. Every registered rule is a veto; adding another rule does not change the skeleton.
/// </summary>
public sealed class ChangelistFilterPipeline(
    IEnumerable<IChangelistFilter> changelistFilters,
    IEnumerable<IFileChangeFilter> fileFilters) : IChangelistFilterPipeline
{
    private readonly IChangelistFilter[] _changelistFilters = changelistFilters.ToArray();
    private readonly IFileChangeFilter[] _fileFilters = fileFilters.ToArray();

    public PerforceFilterDecision EvaluateChangelist(
        PerforceChangelist changelist,
        PerforceFilterOptions options)
    {
        foreach (var filter in _changelistFilters)
        {
            var decision = filter.Evaluate(changelist, options);
            if (!decision.Included)
            {
                return decision;
            }
        }

        return PerforceFilterDecision.Include();
    }

    public PerforceFilterDecision EvaluateFile(
        PerforceFileChange file,
        PerforceFilterOptions options)
    {
        foreach (var filter in _fileFilters)
        {
            var decision = filter.Evaluate(file, options);
            if (!decision.Included)
            {
                return decision;
            }
        }

        return PerforceFilterDecision.Include();
    }
}

public sealed class ChangelistUserFilter : IChangelistFilter
{
    public PerforceFilterDecision Evaluate(PerforceChangelist changelist, PerforceFilterOptions options)
    {
        return options.ExcludedUsers.Any(user => user.Equals(changelist.User, StringComparison.OrdinalIgnoreCase))
            ? PerforceFilterDecision.Exclude($"author '{changelist.User}' is excluded")
            : PerforceFilterDecision.Include();
    }
}

public sealed class ChangelistDescriptionFilter : IChangelistFilter
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public PerforceFilterDecision Evaluate(PerforceChangelist changelist, PerforceFilterOptions options)
    {
        foreach (var pattern in options.ExcludedDescriptionPatterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            try
            {
                if (Regex.IsMatch(
                        changelist.Description ?? string.Empty,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        RegexTimeout))
                {
                    return PerforceFilterDecision.Exclude($"description matched '{pattern}'");
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid Perforce description filter regex: {pattern}", ex);
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new InvalidOperationException($"Perforce description filter regex timed out: {pattern}", ex);
            }
        }

        return PerforceFilterDecision.Include();
    }
}

public sealed class FileActionFilter : IFileChangeFilter
{
    public PerforceFilterDecision Evaluate(PerforceFileChange file, PerforceFilterOptions options)
    {
        if (options.IncludedActions.Count == 0
            || options.IncludedActions.Any(action => action.Equals(file.Action, StringComparison.OrdinalIgnoreCase)))
        {
            return PerforceFilterDecision.Include();
        }

        return PerforceFilterDecision.Exclude($"action '{file.Action}' is not included");
    }
}

public sealed class FilePathFilter : IFileChangeFilter
{
    public PerforceFilterDecision Evaluate(PerforceFileChange file, PerforceFilterOptions options)
    {
        var relativePath = NormalizeRelativePath(file.WorkspaceRelativePath);
        var depotPath = file.DepotPath.Replace('\\', '/');

        var excludedPattern = options.ExcludedPathGlobs.FirstOrDefault(pattern =>
            GlobMatcher.IsMatch(relativePath, pattern, options.CaseSensitivePaths)
            || GlobMatcher.IsMatch(depotPath, pattern, options.CaseSensitivePaths));
        if (excludedPattern != null)
        {
            return PerforceFilterDecision.Exclude($"path matched exclusion '{excludedPattern}'");
        }

        if (options.IncludedPathGlobs.Count == 0
            || options.IncludedPathGlobs.Any(pattern =>
                GlobMatcher.IsMatch(relativePath, pattern, options.CaseSensitivePaths)
                || GlobMatcher.IsMatch(depotPath, pattern, options.CaseSensitivePaths)))
        {
            return PerforceFilterDecision.Include();
        }

        return PerforceFilterDecision.Exclude("path did not match an inclusion rule");
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}

/// <summary>
/// Implements the explicit content rule: allowed suffix OR text-like filetype, after binary and suffix vetoes.
/// </summary>
public sealed class FileContentTypeFilter : IFileChangeFilter
{
    public PerforceFilterDecision Evaluate(PerforceFileChange file, PerforceFilterOptions options)
    {
        var path = file.WorkspaceRelativePath;
        var fileType = NormalizeFileType(file.FileType);

        if (IsBinaryFileType(fileType))
        {
            return PerforceFilterDecision.Exclude($"p4 filetype '{file.FileType}' is binary");
        }

        var excludedSuffix = FindSuffix(path, options.ExcludedSuffixes);
        if (excludedSuffix != null)
        {
            return PerforceFilterDecision.Exclude($"suffix matched exclusion '{excludedSuffix}'");
        }

        if (FindSuffix(path, options.IncludedSuffixes) != null)
        {
            return PerforceFilterDecision.Include();
        }

        if (options.AllowTextFileType && IsTextFileType(fileType))
        {
            return PerforceFilterDecision.Include();
        }

        return PerforceFilterDecision.Exclude(
            $"neither an included suffix nor an allowed text filetype (filetype: '{file.FileType}')");
    }

    private static string NormalizeFileType(string value)
    {
        var modifierIndex = value.IndexOf('+');
        return (modifierIndex >= 0 ? value[..modifierIndex] : value).Trim();
    }

    private static bool IsBinaryFileType(string fileType)
    {
        return fileType.Equals("binary", StringComparison.OrdinalIgnoreCase)
               || fileType.Equals("apple", StringComparison.OrdinalIgnoreCase)
               || fileType.Equals("resource", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextFileType(string fileType)
    {
        return fileType.Equals("text", StringComparison.OrdinalIgnoreCase)
               || fileType.Equals("unicode", StringComparison.OrdinalIgnoreCase)
               || fileType.Equals("utf8", StringComparison.OrdinalIgnoreCase)
               || fileType.Equals("utf16", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindSuffix(string path, IEnumerable<string> suffixes)
    {
        return suffixes.FirstOrDefault(suffix =>
            !string.IsNullOrWhiteSpace(suffix)
            && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class GlobMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Cache = new();

    public static bool IsMatch(string path, string pattern, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = Cache.GetOrAdd(
            BuildCacheKey(pattern, caseSensitive),
            static key =>
            {
                var separator = key.IndexOf('\n');
                var patternText = key[(separator + 1)..];
                var caseSensitiveFlag = key[0] == '1';
                return new Regex(
                    GlobToRegex(patternText),
                    RegexOptions.CultureInvariant | RegexOptions.Compiled | (caseSensitiveFlag ? RegexOptions.None : RegexOptions.IgnoreCase),
                    MatchTimeout);
            });

        try
        {
            return regex.IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string BuildCacheKey(string pattern, bool caseSensitive)
        => (caseSensitive ? "1\n" : "0\n") + pattern.Replace('\\', '/');

    private static string GlobToRegex(string pattern)
    {
        var normalizedPattern = pattern.Replace('\\', '/');
        var regex = new StringBuilder("^");
        for (var index = 0; index < normalizedPattern.Length; index++)
        {
            var current = normalizedPattern[index];
            if (current == '*' && index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '*')
            {
                regex.Append(".*");
                index++;
            }
            else if (current == '*')
            {
                regex.Append("[^/]*");
            }
            else if (current == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(current.ToString()));
            }
        }

        regex.Append('$');
        return regex.ToString();
    }
}
