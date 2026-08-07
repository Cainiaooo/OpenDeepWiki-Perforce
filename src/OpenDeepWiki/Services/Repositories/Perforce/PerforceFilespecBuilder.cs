using OpenDeepWiki.Services.Repositories.Scope;

namespace OpenDeepWiki.Services.Repositories.Perforce;

/// <summary>
/// Builds explicit local workspace filespecs. Perforce commands must not rely on
/// the process current directory to interpret a bare <c>...</c> filespec.
/// </summary>
public static class PerforceFilespecBuilder
{
    public static IReadOnlyList<string> BuildChangeTriggerFilespecs(
        string workspaceRoot,
        ResolvedScopeConfiguration? scopeConfiguration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var fullRoot = Path.GetFullPath(workspaceRoot);
        var roots = new List<string>();
        if (scopeConfiguration is null)
        {
            roots.Add(string.Empty);
        }
        else
        {
            if (scopeConfiguration.ChangeTriggerScope.InheritsDocumentScopes)
            {
                roots.AddRange(scopeConfiguration.DocumentScopes.Select(scope => scope.Root));
            }

            roots.AddRange(scopeConfiguration.ChangeTriggerScope.AdditionalRoots);
        }

        if (roots.Count == 0)
        {
            return [];
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var filespecs = new HashSet<string>(comparer);

        foreach (var root in roots)
        {
            var normalizedRoot = ScopePathUtility.NormalizeRoot(root);
            var scopePath = string.IsNullOrEmpty(normalizedRoot)
                ? fullRoot
                : Path.GetFullPath(Path.Combine(fullRoot, normalizedRoot.Replace('/', Path.DirectorySeparatorChar)));

            EnsureInsideWorkspace(fullRoot, scopePath);
            filespecs.Add(Path.Combine(EscapeLiteralPath(scopePath), "..."));
        }

        return filespecs.Order(comparer).ToArray();
    }

    /// <summary>
    /// Escapes Perforce filespec metacharacters so a path is treated as a literal.
    /// Safe for both workspace paths and depot paths before appending revision syntax.
    /// </summary>
    public static string EscapeLiteralPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // P4 treats these characters as filespec syntax even when the process
        // API passes the argument without shell expansion. Encode the literal
        // path first, then append intentional wildcards or revision selectors.
        return path
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("@", "%40", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal)
            .Replace("*", "%2A", StringComparison.Ordinal);
    }

    private static void EnsureInsideWorkspace(string workspaceRoot, string candidate)
    {
        var relative = Path.GetRelativePath(workspaceRoot, candidate).Replace('\\', '/');
        if (relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                $"ChangeTriggerScope root resolves outside the Perforce workspace: {relative}");
        }
    }
}
