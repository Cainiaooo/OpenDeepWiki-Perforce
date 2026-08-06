namespace OpenDeepWiki.Services.Repositories.Scope;

/// <summary>
/// 来源无关的统一文件选择策略。扫描、增量和 Agent 工具的唯一判定入口。
/// 判定顺序：安全拒绝 → 路径排除 → 路径包含 → 后缀/filetype → 操作权限。
/// </summary>
public interface IRepositoryFileSelectionPolicy
{
    ResolvedScopeConfiguration Configuration { get; }

    FileSelectionDecision EvaluateDocumentCandidate(string path, SourceFileMetadata? metadata = null);

    FileSelectionDecision EvaluateChangeTrigger(string path, SourceFileMetadata? metadata = null);

    FileSelectionDecision EvaluateContextRead(string path, SourceFileMetadata? metadata = null);

    FileSelectionDecision EvaluateDirectoryPrune(string path, FileSelectionOperation operation);

    bool IsDocumentCandidate(string path, SourceFileMetadata? metadata = null);

    bool ShouldTriggerUpdate(string path, SourceFileMetadata? metadata = null);

    bool CanReadAsContext(string path, SourceFileMetadata? metadata = null);

    bool ShouldPruneDirectory(string path, FileSelectionOperation operation);

    MovePathSelectionResult EvaluateMove(string oldPath, string newPath, SourceFileMetadata? metadata = null);
}

public interface IRepositoryFileSelectionPolicyFactory
{
    IRepositoryFileSelectionPolicy Create(
        ResolvedScopeConfiguration configuration,
        string? workspaceRoot = null);

    /// <summary>
    /// 无 Scope 配置时的兼容策略：保持宽松接受，并标记 migration hint。
    /// </summary>
    IRepositoryFileSelectionPolicy CreateLegacyFallback(string? workspaceRoot = null);
}

public sealed class RepositoryFileSelectionPolicyFactory : IRepositoryFileSelectionPolicyFactory
{
    public IRepositoryFileSelectionPolicy Create(
        ResolvedScopeConfiguration configuration,
        string? workspaceRoot = null)
        => new RepositoryFileSelectionPolicy(configuration, workspaceRoot);

    public IRepositoryFileSelectionPolicy CreateLegacyFallback(string? workspaceRoot = null)
        => new RepositoryFileSelectionPolicy(CreateLegacyConfiguration(), workspaceRoot);

    private static ResolvedScopeConfiguration CreateLegacyConfiguration()
    {
        var document = new ScopeConfigurationDocument
        {
            SchemaVersion = 1,
            WorkspaceContentPolicy = WorkspaceContentPolicy.SubmittedHaveOnly,
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "legacy-all",
                    Root = string.Empty,
                    IncludedPathGlobs = ["**"],
                    ExcludedPathGlobs = [],
                    IncludedSuffixes = ScopeDefaults.DefaultDocumentSuffixes.ToList()
                }
            ],
            ChangeTriggerScope = new ChangeTriggerScopeRuleDto
            {
                InheritsDocumentScopes = true
            }
        };

        var normalizer = new ScopeConfigurationNormalizer();
        var resolved = normalizer.Normalize(document);
        return new ResolvedScopeConfiguration
        {
            SchemaVersion = resolved.SchemaVersion,
            WorkspaceContentPolicy = resolved.WorkspaceContentPolicy,
            DocumentScopes = resolved.DocumentScopes,
            ContextScope = resolved.ContextScope,
            ChangeTriggerScope = resolved.ChangeTriggerScope,
            ContentHash = resolved.ContentHash,
            NormalizedJson = resolved.NormalizedJson,
            IsLegacyFallback = true
        };
    }
}

public sealed class RepositoryFileSelectionPolicy : IRepositoryFileSelectionPolicy
{
    private readonly string? _workspaceRoot;

    public RepositoryFileSelectionPolicy(
        ResolvedScopeConfiguration configuration,
        string? workspaceRoot = null)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? null
            : Path.GetFullPath(workspaceRoot);
    }

    public ResolvedScopeConfiguration Configuration { get; }

    public bool IsDocumentCandidate(string path, SourceFileMetadata? metadata = null)
        => EvaluateDocumentCandidate(path, metadata).Accepted;

    public bool ShouldTriggerUpdate(string path, SourceFileMetadata? metadata = null)
        => EvaluateChangeTrigger(path, metadata).Accepted;

    public bool CanReadAsContext(string path, SourceFileMetadata? metadata = null)
        => EvaluateContextRead(path, metadata).Accepted;

    public bool ShouldPruneDirectory(string path, FileSelectionOperation operation)
    {
        var decision = EvaluateDirectoryPrune(path, operation);
        return decision.ReasonCode == FileSelectionReasonCodes.PruneDirectory;
    }

    public FileSelectionDecision EvaluateDocumentCandidate(string path, SourceFileMetadata? metadata = null)
    {
        var safety = ScopePathUtility.EvaluateSafety(path, _workspaceRoot);
        if (safety is not null)
        {
            return safety;
        }

        var workspace = EvaluateWorkspacePolicy(metadata);
        if (workspace is not null)
        {
            return workspace;
        }

        var normalized = ScopePathUtility.NormalizeRelativePath(path);
        var documentMatch = FindBestDocumentScope(normalized);
        if (documentMatch is not null)
        {
            var (scope, relative) = documentMatch.Value;
            if (IsExcluded(relative, scope.ExcludedPathGlobs))
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.PathExcluded, relative, scope.Id);
            }

            if (!IsIncluded(relative, scope.IncludedPathGlobs))
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.PathNotIncluded, relative, scope.Id);
            }

            if (!MatchesSuffixOrText(normalized, scope, metadata))
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.SuffixNotIncluded, normalized, scope.Id);
            }

            return FileSelectionDecision.Accept(
                Configuration.IsLegacyFallback
                    ? FileSelectionReasonCodes.LegacyAccept
                    : FileSelectionReasonCodes.AcceptedDocument,
                scope.Id);
        }

        // Context scope must never become Document candidate via filetype fallback.
        if (IsInContextScope(normalized, out _))
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.ContextNotDocument, normalized);
        }

        return FileSelectionDecision.Reject(FileSelectionReasonCodes.OutsideAnyScope, normalized);
    }

    public FileSelectionDecision EvaluateChangeTrigger(string path, SourceFileMetadata? metadata = null)
    {
        var safety = ScopePathUtility.EvaluateSafety(path, _workspaceRoot);
        if (safety is not null)
        {
            return safety;
        }

        var normalized = ScopePathUtility.NormalizeRelativePath(path);
        var trigger = Configuration.ChangeTriggerScope;

        if (trigger.InheritsDocumentScopes)
        {
            var document = EvaluateDocumentCandidate(path, metadata);
            if (document.Accepted)
            {
                return FileSelectionDecision.Accept(FileSelectionReasonCodes.AcceptedTrigger, document.MatchedScopeId);
            }

            // Document rejects due to workspace/suffix still don't auto-include via additional roots unless under them.
            if (document.ReasonCode is FileSelectionReasonCodes.SafetyAbsolutePath
                or FileSelectionReasonCodes.SafetyUncPath
                or FileSelectionReasonCodes.SafetyDriveLetter
                or FileSelectionReasonCodes.SafetyDotDot
                or FileSelectionReasonCodes.SafetySensitive)
            {
                return document;
            }
        }

        foreach (var root in trigger.AdditionalRoots)
        {
            if (!ScopePathUtility.IsUnderRoot(normalized, root))
            {
                continue;
            }

            var relative = ScopePathUtility.GetPathRelativeToRoot(normalized, root) ?? string.Empty;
            if (IsExcluded(relative, trigger.ExcludedPathGlobs))
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.PathExcluded, relative);
            }

            if (!IsIncluded(relative, trigger.IncludedPathGlobs))
            {
                return FileSelectionDecision.Reject(FileSelectionReasonCodes.PathNotIncluded, relative);
            }

            return FileSelectionDecision.Accept(FileSelectionReasonCodes.AcceptedTrigger, detail: root);
        }

        if (!trigger.InheritsDocumentScopes)
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.OutsideAnyScope, normalized);
        }

        return FileSelectionDecision.Reject(FileSelectionReasonCodes.OutsideAnyScope, normalized);
    }

    public FileSelectionDecision EvaluateContextRead(string path, SourceFileMetadata? metadata = null)
    {
        var safety = ScopePathUtility.EvaluateSafety(path, _workspaceRoot);
        if (safety is not null)
        {
            return safety;
        }

        var workspace = EvaluateWorkspacePolicy(metadata);
        if (workspace is not null)
        {
            return workspace;
        }

        var normalized = ScopePathUtility.NormalizeRelativePath(path);

        // Document candidates are always readable as context.
        var document = EvaluateDocumentCandidate(path, metadata);
        if (document.Accepted)
        {
            return FileSelectionDecision.Accept(FileSelectionReasonCodes.AcceptedContext, document.MatchedScopeId);
        }

        if (document.ReasonCode.StartsWith("safety.", StringComparison.Ordinal))
        {
            return document;
        }

        if (!IsInContextScope(normalized, out var matchedRoot))
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.OutsideAnyScope, normalized);
        }

        var context = Configuration.ContextScope!;
        if (metadata?.SizeBytes is long size && size > context.MaxFileBytes)
        {
            return FileSelectionDecision.Reject(
                FileSelectionReasonCodes.OperationDenied,
                $"file size {size} exceeds maxFileBytes {context.MaxFileBytes}",
                matchedRoot);
        }

        return FileSelectionDecision.Accept(FileSelectionReasonCodes.AcceptedContext, matchedRoot);
    }

    public FileSelectionDecision EvaluateDirectoryPrune(string path, FileSelectionOperation operation)
    {
        var safety = ScopePathUtility.EvaluateSafety(path, _workspaceRoot);
        if (safety is not null)
        {
            return FileSelectionDecision.Accept(FileSelectionReasonCodes.PruneDirectory, detail: safety.ReasonCode);
        }

        var normalized = ScopePathUtility.NormalizeRelativePath(path).TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
        {
            return FileSelectionDecision.Accept(FileSelectionReasonCodes.AcceptedKeepDirectory);
        }

        var relevantRoots = operation switch
        {
            FileSelectionOperation.DocumentCandidate or FileSelectionOperation.ChangeTrigger
                => Configuration.DocumentScopes.Select(scope => scope.Root)
                    .Concat(Configuration.ChangeTriggerScope.AdditionalRoots),
            FileSelectionOperation.ContextRead
                => Configuration.DocumentScopes.Select(scope => scope.Root)
                    .Concat(Configuration.ContextScope?.Roots ?? Array.Empty<string>()),
            _ => Configuration.DocumentScopes.Select(scope => scope.Root)
        };

        foreach (var root in relevantRoots)
        {
            var normalizedRoot = ScopePathUtility.NormalizeRoot(root);
            if (string.IsNullOrEmpty(normalizedRoot)
                || ScopePathUtility.IsUnderRoot(normalized, normalizedRoot)
                || ScopePathUtility.IsUnderRoot(normalizedRoot, normalized)
                || normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return FileSelectionDecision.Accept(FileSelectionReasonCodes.AcceptedKeepDirectory, detail: root);
            }
        }

        return FileSelectionDecision.Accept(FileSelectionReasonCodes.PruneDirectory, detail: normalized);
    }

    public MovePathSelectionResult EvaluateMove(
        string oldPath,
        string newPath,
        SourceFileMetadata? metadata = null)
    {
        var oldDecision = ClassifyForMove(oldPath, metadata);
        var newDecision = ClassifyForMove(newPath, metadata);
        var transition = ResolveTransition(oldDecision.Kind, newDecision.Kind);

        return new MovePathSelectionResult
        {
            OldPath = oldDecision.Decision,
            NewPath = newDecision.Decision,
            Transition = transition
        };
    }

    private (PathKind Kind, FileSelectionDecision Decision) ClassifyForMove(
        string path,
        SourceFileMetadata? metadata)
    {
        var document = EvaluateDocumentCandidate(path, metadata);
        if (document.Accepted)
        {
            return (PathKind.Document, document);
        }

        var context = EvaluateContextRead(path, metadata);
        if (context.Accepted)
        {
            return (PathKind.Context, context);
        }

        return (PathKind.Excluded, document.Accepted ? document : context);
    }

    private static MoveScopeTransition ResolveTransition(PathKind oldKind, PathKind newKind)
        => (oldKind, newKind) switch
        {
            (PathKind.Document, PathKind.Document) => MoveScopeTransition.DocumentToDocument,
            (PathKind.Document, PathKind.Context) => MoveScopeTransition.DocumentToContext,
            (PathKind.Context, PathKind.Document) => MoveScopeTransition.ContextToDocument,
            (PathKind.Document, PathKind.Excluded) => MoveScopeTransition.DocumentToExcluded,
            (PathKind.Context, PathKind.Excluded) => MoveScopeTransition.ContextToExcluded,
            (PathKind.Excluded, PathKind.Document) => MoveScopeTransition.ExcludedToDocument,
            (PathKind.Excluded, PathKind.Context) => MoveScopeTransition.ExcludedToContext,
            (PathKind.Excluded, PathKind.Excluded) => MoveScopeTransition.UnchangedOutside,
            _ => MoveScopeTransition.Other
        };

    private enum PathKind
    {
        Document,
        Context,
        Excluded
    }

    private (ResolvedDocumentScope Scope, string Relative)? FindBestDocumentScope(string normalizedPath)
    {
        ResolvedDocumentScope? best = null;
        string? bestRelative = null;
        var bestSpecificity = -1;

        foreach (var scope in Configuration.DocumentScopes)
        {
            // Empty root covers the whole repository.
            if (!string.IsNullOrEmpty(scope.Root) && !ScopePathUtility.IsUnderRoot(normalizedPath, scope.Root))
            {
                continue;
            }

            var relative = ScopePathUtility.GetPathRelativeToRoot(normalizedPath, scope.Root) ?? string.Empty;
            var specificity = ScopePathUtility.RootSpecificity(scope.Root);
            if (specificity > bestSpecificity)
            {
                best = scope;
                bestRelative = relative;
                bestSpecificity = specificity;
            }
        }

        return best is null ? null : (best, bestRelative ?? string.Empty);
    }

    private bool IsInContextScope(string normalizedPath, out string? matchedRoot)
    {
        matchedRoot = null;
        var context = Configuration.ContextScope;
        if (context is null)
        {
            return false;
        }

        foreach (var root in context.Roots.OrderByDescending(ScopePathUtility.RootSpecificity))
        {
            if (!ScopePathUtility.IsUnderRoot(normalizedPath, root)
                && !string.IsNullOrEmpty(root))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(root) && !ScopePathUtility.IsUnderRoot(normalizedPath, root))
            {
                continue;
            }

            var relative = ScopePathUtility.GetPathRelativeToRoot(normalizedPath, root) ?? string.Empty;
            if (IsExcluded(relative, context.ExcludedPathGlobs))
            {
                continue;
            }

            if (!IsIncluded(relative, context.IncludedPathGlobs))
            {
                continue;
            }

            matchedRoot = root;
            return true;
        }

        return false;
    }

    private static bool IsExcluded(string relativePath, IReadOnlyList<string> excluded)
        => excluded.Count > 0 && ScopePathUtility.MatchesAnyGlob(relativePath, excluded);

    private static bool IsIncluded(string relativePath, IReadOnlyList<string> included)
        => included.Count == 0 || ScopePathUtility.MatchesAnyGlob(relativePath, included);

    private static bool MatchesSuffixOrText(
        string path,
        ResolvedDocumentScope scope,
        SourceFileMetadata? metadata)
    {
        if (scope.AcceptAllTextFiles)
        {
            if (metadata?.FileType is string fileType && IsBinaryFileType(fileType))
            {
                return false;
            }

            return true;
        }

        if (ScopePathUtility.FindLongestMatchingSuffix(path, scope.IncludedSuffixes) is not null)
        {
            return true;
        }

        return false;
    }

    private FileSelectionDecision? EvaluateWorkspacePolicy(SourceFileMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        if (metadata.IsTracked == false)
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.WorkspaceUntracked);
        }

        if (metadata.IsOpened == true)
        {
            if (Configuration.WorkspaceContentPolicy == WorkspaceContentPolicy.AllowOpenedFiles)
            {
                return null;
            }

            return FileSelectionDecision.Reject(
                FileSelectionReasonCodes.WorkspaceOpened,
                metadata.OpenedAction);
        }

        if (metadata.MatchesHaveContent == false
            && Configuration.WorkspaceContentPolicy == WorkspaceContentPolicy.SubmittedHaveOnly)
        {
            return FileSelectionDecision.Reject(FileSelectionReasonCodes.WorkspaceContentMismatch);
        }

        return null;
    }

    private static bool IsBinaryFileType(string fileType)
    {
        var normalized = fileType;
        var modifierIndex = normalized.IndexOf('+');
        if (modifierIndex >= 0)
        {
            normalized = normalized[..modifierIndex];
        }

        return normalized.Equals("binary", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("apple", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("resource", StringComparison.OrdinalIgnoreCase);
    }
}
