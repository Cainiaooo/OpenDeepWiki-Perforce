using System.Text;
using System.Text.RegularExpressions;

namespace OpenDeepWiki.Services.Repositories.Scope;

/// <summary>
/// 相对路径规范化、安全校验与 glob/后缀匹配。
/// </summary>
public static class ScopePathUtility
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> GlobCache = new();

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');
        return normalized;
    }

    public static string NormalizeRoot(string root)
    {
        var normalized = NormalizeRelativePath(root).TrimEnd('/');
        return normalized;
    }

    public static FileSelectionDecision? EvaluateSafety(string path, string? workspaceRoot = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.OutsideAnyScope, "empty path");
        }

        var raw = path.Trim();
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.SafetyUncPath, raw);
        }

        if (Path.IsPathRooted(raw) || raw.StartsWith('/'))
        {
            // Unix absolute or Windows rooted
            if (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':')
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.SafetyDriveLetter, raw);
            }

            if (raw.StartsWith('/'))
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.SafetyAbsolutePath, raw);
            }

            return FileSelectionDecision.Reject(FileSelectionReasonCodes.SafetyAbsolutePath, raw);
        }

        if (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':')
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.SafetyDriveLetter, raw);
        }

        var normalized = NormalizeRelativePath(raw);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.SafetyDotDot, normalized);
        }

        var fileName = segments.Length == 0 ? normalized : segments[^1];
        foreach (var sensitive in ScopeDefaults.SensitiveFileNamePatterns)
        {
            if (fileName.Equals(sensitive, StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.SafetySensitive, fileName);
            }
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var symlinkDecision = EvaluateSymlinkEscape(normalized, workspaceRoot);
            if (symlinkDecision is not null)
            {
                return symlinkDecision;
            }
        }

        return null;
    }

    /// <summary>
    /// 解析最终链接目标，确认仍位于工作区根内；越界符号链接拒绝。
    /// </summary>
    public static FileSelectionDecision? EvaluateSymlinkEscape(string relativePath, string workspaceRoot)
    {
        try
        {
            var rootFull = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(
                rootFull,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!IsPathInsideRoot(candidate, rootFull))
            {
                return FileSelectionDecision.Reject(
                    FileSelectionReasonCodes.SafetySymlinkEscape,
                    $"path escapes workspace: {relativePath}");
            }

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return null;
            }

            FileSystemInfo? resolved = null;
            try
            {
                resolved = File.ResolveLinkTarget(candidate, returnFinalTarget: true)
                           ?? Directory.ResolveLinkTarget(candidate, returnFinalTarget: true);
            }
            catch (IOException)
            {
                // Broken link or unsupported reparse — treat as escape risk if reparse point.
                var attrs = File.GetAttributes(candidate);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    return FileSelectionDecision.Reject(
                        FileSelectionReasonCodes.SafetySymlinkEscape,
                        $"unresolvable reparse point: {relativePath}");
                }
            }

            if (resolved is null)
            {
                return null;
            }

            var targetFull = Path.GetFullPath(resolved.FullName);
            if (!IsPathInsideRoot(targetFull, rootFull))
            {
                return FileSelectionDecision.Reject(
                    FileSelectionReasonCodes.SafetySymlinkEscape,
                    $"symlink target outside workspace: {relativePath} -> {resolved.FullName}");
            }
        }
        catch (Exception)
        {
            return FileSelectionDecision.Reject(
                FileSelectionReasonCodes.SafetySymlinkEscape,
                $"failed to validate path containment: {relativePath}");
        }

        return null;
    }

    public static bool IsPathInsideRoot(string fullPath, string rootFullPath)
    {
        var root = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(fullPath);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderRoot(string relativePath, string root)
    {
        var path = NormalizeRelativePath(relativePath);
        var normalizedRoot = NormalizeRoot(root);
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return true;
        }

        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetPathRelativeToRoot(string relativePath, string root)
    {
        var path = NormalizeRelativePath(relativePath);
        var normalizedRoot = NormalizeRoot(root);
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return path;
        }

        if (path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (!path.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return path[(normalizedRoot.Length + 1)..];
    }

    public static bool MatchesAnyGlob(string pathRelativeToRoot, IEnumerable<string> globs, bool caseSensitive = false)
    {
        var path = NormalizeRelativePath(pathRelativeToRoot);
        foreach (var glob in globs)
        {
            if (string.IsNullOrWhiteSpace(glob))
            {
                continue;
            }

            if (IsGlobMatch(path, glob.Replace('\\', '/'), caseSensitive))
            {
                return true;
            }

            // Scope root itself and nested directory semantics: "**" matches everything under root.
            if (glob is "**" or "**/*" or "*")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsGlobMatch(string path, string pattern, bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = GlobCache.GetOrAdd(
            (caseSensitive ? "1\n" : "0\n") + pattern,
            static key =>
            {
                var separator = key.IndexOf('\n');
                var patternText = key[(separator + 1)..];
                var caseSensitiveFlag = key[0] == '1';
                return new Regex(
                    GlobToRegex(patternText),
                    RegexOptions.CultureInvariant | RegexOptions.Compiled
                    | (caseSensitiveFlag ? RegexOptions.None : RegexOptions.IgnoreCase),
                    RegexTimeout);
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

    /// <summary>
    /// 复合后缀最长匹配（.Build.cs 优先于 .cs）。
    /// </summary>
    public static string? FindLongestMatchingSuffix(string path, IEnumerable<string> suffixes)
    {
        var normalized = NormalizeRelativePath(path);
        string? best = null;
        foreach (var suffix in suffixes)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                continue;
            }

            var candidate = suffix.StartsWith('.') ? suffix : "." + suffix;
            if (!normalized.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || candidate.Length > best.Length)
            {
                best = candidate;
            }
        }

        return best;
    }

    public static int RootSpecificity(string root)
    {
        var normalized = NormalizeRoot(root);
        if (string.IsNullOrEmpty(normalized))
        {
            return 0;
        }

        return normalized.Count(c => c == '/') + 1;
    }

    private static string GlobToRegex(string pattern)
    {
        // **/ at a path boundary matches zero or more directories so that
        // **/*.cpp matches Foo.cpp and **/Binaries/** matches Binaries/x.dll.
        var normalizedPattern = pattern.Replace('\\', '/');
        var regex = new StringBuilder("^");
        for (var index = 0; index < normalizedPattern.Length; index++)
        {
            var current = normalizedPattern[index];
            if (current == '*' && index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '*')
            {
                var nextAfterStarStar = index + 2 < normalizedPattern.Length
                    ? normalizedPattern[index + 2]
                    : '\0';
                if (nextAfterStarStar == '/')
                {
                    // **/  → optional directory prefix
                    regex.Append("(?:.+/)?");
                    index += 2; // consume "**/'s trailing /"
                }
                else
                {
                    regex.Append(".*");
                    index++; // consume second *
                }
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
