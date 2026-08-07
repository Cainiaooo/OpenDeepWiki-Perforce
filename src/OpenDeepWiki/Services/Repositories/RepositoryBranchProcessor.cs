using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Generation;
using OpenDeepWiki.Services.Repositories.Scope;
using OpenDeepWiki.Services.Wiki;

namespace OpenDeepWiki.Services.Repositories;

public interface IRepositoryBranchProcessor
{
    Task<string?> ProcessBranchAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        string? generationTaskId,
        bool forceFullGeneration,
        CancellationToken cancellationToken = default);
}

public sealed class RepositoryBranchProcessor(
    IRepositoryAnalyzer repositoryAnalyzer,
    IWikiGenerator wikiGenerator,
    IRepositorySkillMarkdownBuilder? skillMarkdownBuilder,
    IRepositoryScanPlanResolver? scanPlanResolver,
    IProcessingLogService? processingLogService,
    IWikiGenerationService wikiGenerationService,
    IScopeConfigurationService scopeConfigurationService,
    IRepositoryFileSelectionPolicyFactory policyFactory,
    ILogger<RepositoryBranchProcessor> logger,
    ISourceInventoryBuilder? sourceInventoryBuilder = null,
    IDomainTopicPlanner? domainTopicPlanner = null,
    ICoverageAuditor? coverageAuditor = null,
    IWorkspaceManifestService? workspaceManifestService = null) : IRepositoryBranchProcessor
{
    public async Task<string?> ProcessBranchAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        string? generationTaskId,
        bool forceFullGeneration,
        CancellationToken cancellationToken = default)
    {
        var branchStopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting branch processing. BranchId: {BranchId}, Branch: {BranchName}, Repository: {Org}/{Repo}, LastCommitId: {LastCommitId}, TaskId: {TaskId}, ForceFull: {ForceFull}",
            branch.Id, branch.BranchName, repository.OrgName, repository.RepoName, branch.LastCommitId ?? "none", generationTaskId ?? "none", forceFullGeneration);

        var wikiGeneratorImpl = wikiGenerator as WikiGenerator;
        if (wikiGeneratorImpl is not null)
        {
            wikiGeneratorImpl.SetCurrentRepository(repository.Id, $"{repository.OrgName}/{repository.RepoName}");
            wikiGeneratorImpl.SetCurrentGenerationContext(branch.Id, generationTaskId);
        }

        await LogAsync(repository.Id, branch.Id, generationTaskId, ProcessingStep.Workspace,
            $"Preparing workspace, branch: {branch.BranchName}", cancellationToken);

        var previousCommitId = forceFullGeneration ? null : branch.LastCommitId;
        var workspace = await repositoryAnalyzer.PrepareWorkspaceAsync(
            repository,
            branch.BranchName,
            previousCommitId,
            cancellationToken);

        try
        {
            await ResolveScanPlanAsync(context, repository, workspace.WorkingDirectory, branch.Id, generationTaskId, cancellationToken);

            await LogAsync(repository.Id, branch.Id, generationTaskId, ProcessingStep.Workspace,
                $"Workspace ready, Commit: {workspace.CommitId[..Math.Min(7, workspace.CommitId.Length)]}", cancellationToken);

            if (string.IsNullOrEmpty(repository.PrimaryLanguage))
            {
                var detectedLanguage = await repositoryAnalyzer.DetectPrimaryLanguageAsync(workspace, cancellationToken);
                if (!string.IsNullOrEmpty(detectedLanguage))
                {
                    repository.PrimaryLanguage = detectedLanguage;
                    context.Repositories.Update(repository);
                    await context.SaveChangesAsync(cancellationToken);

                    await LogAsync(repository.Id, branch.Id, generationTaskId, ProcessingStep.Workspace,
                        $"Primary language detected: {detectedLanguage}", cancellationToken);
                }
            }

            var languages = await context.BranchLanguages
                .Where(l => l.RepositoryBranchId == branch.Id && !l.IsDeleted)
                .ToListAsync(cancellationToken);

            if (languages.Count == 0)
            {
                // Fail closed: completing without processing would still advance LastCommitId in
                // BranchGenerationWorker and permanently skip the requested interval.
                logger.LogWarning(
                    "No languages found for branch. BranchId: {BranchId}, Branch: {BranchName}",
                    branch.Id, branch.BranchName);
                throw new InvalidOperationException(
                    $"分支未配置语言，无法生成文档 (BranchId: {branch.Id}, Branch: {branch.BranchName})");
            }

            var isIncremental = !forceFullGeneration &&
                                workspace.IsIncremental &&
                                workspace.PreviousCommitId != workspace.CommitId;

            string[]? changedFiles = null;
            if (isIncremental)
            {
                changedFiles = await repositoryAnalyzer.GetChangedFilesAsync(
                    workspace,
                    workspace.PreviousCommitId,
                    workspace.CommitId,
                    cancellationToken);
            }

            foreach (var language in languages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await ProcessLanguageAsync(
                    context,
                    repository,
                    branch,
                    workspace,
                    language,
                    isIncremental,
                    changedFiles,
                    cancellationToken);
            }

            // Full generation is claimed by BranchGenerationWorker, which resolves the final baseline
            // (e.g. preserving a numeric Perforce event target over the p4-initial workspace sentinel).
            // Skip the intermediate write so concurrent readers never observe p4-initial as the baseline.
            if (!forceFullGeneration)
            {
                branch.LastCommitId = workspace.CommitId;
                branch.LastProcessedAt = DateTime.UtcNow;
                context.RepositoryBranches.Update(branch);
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                branch.LastProcessedAt = DateTime.UtcNow;
                context.RepositoryBranches.Update(branch);
                await context.SaveChangesAsync(cancellationToken);
            }

            branchStopwatch.Stop();
            logger.LogInformation(
                "Branch processing completed. BranchId: {BranchId}, Branch: {BranchName}, CommitId: {CommitId}, Duration: {Duration}ms",
                branch.Id, branch.BranchName, workspace.CommitId, branchStopwatch.ElapsedMilliseconds);

            return workspace.CommitId;
        }
        finally
        {
            if (wikiGeneratorImpl is not null)
            {
                wikiGeneratorImpl.SetCurrentGenerationContext(null, null);
            }

            await repositoryAnalyzer.CleanupWorkspaceAsync(workspace, cancellationToken);
        }
    }

    private async Task ResolveScanPlanAsync(
        IContext context,
        Repository repository,
        string workingDirectory,
        string branchId,
        string? generationTaskId,
        CancellationToken cancellationToken)
    {
        if (scanPlanResolver is null)
        {
            return;
        }

        var scanPlan = await scanPlanResolver.ResolveAndEnsureAsync(
            context,
            repository,
            workingDirectory,
            cancellationToken);

        await LogAsync(
            repository.Id,
            branchId,
            generationTaskId,
            ProcessingStep.Workspace,
            $"Resolved scan plan: {scanPlan.Source}, directoryDepth={scanPlan.DirectoryTreeDepth}, fileDepth={scanPlan.FileListDepth}, maxNodes={scanPlan.MaxTreeNodes}, maxFilesPerDirectory={scanPlan.MaxFilesPerDirectory}, maxTotalFiles={scanPlan.MaxTotalFiles}, profileHash={scanPlan.ProfileHash ?? "none"}",
            cancellationToken);
    }

    private async Task ProcessLanguageAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        RepositoryWorkspace workspace,
        BranchLanguage language,
        bool isIncremental,
        string[]? changedFiles,
        CancellationToken cancellationToken)
    {
        if (isIncremental && changedFiles != null && changedFiles.Length > 0)
        {
            await using var policySession = await WikiGenerationSession.BeginPolicyOnlyAsync(
                scopeConfigurationService,
                policyFactory,
                repository.Id,
                workspace.WorkingDirectory,
                cancellationToken);

            // 增量写入当前已发布 generation（若有），避免污染读者可见树的同时写进新 staging。
            var publishedId = await WikiPublicationQuery.GetPublishedGenerationIdAsync(
                context, language.Id, cancellationToken);
            WikiGenerationContext.CurrentGenerationId = publishedId;

            await wikiGenerator.IncrementalUpdateAsync(workspace, language, changedFiles, cancellationToken);
        }
        else
        {
            await using var session = await WikiGenerationSession.BeginFullGenerationAsync(
                context,
                wikiGenerationService,
                scopeConfigurationService,
                policyFactory,
                repository,
                branch,
                language,
                ownerTaskId: null,
                ownerTaskType: "branch-full",
                workspace.WorkingDirectory,
                cancellationToken);

            try
            {
                await TryLogHierarchicalPlanningAsync(
                    context,
                    repository,
                    branch,
                    language,
                    workspace.WorkingDirectory,
                    cancellationToken);

                await wikiGenerator.GenerateCatalogAsync(workspace, language, cancellationToken);
                await wikiGenerator.GenerateDocumentsAsync(workspace, language, cancellationToken);
                await session.PublishAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await session.FailAsync(ex.Message, cancellationToken);
                throw;
            }
        }

        if (repository.GenerateSkill && skillMarkdownBuilder is not null)
        {
            await skillMarkdownBuilder.RefreshSkillMarkdownAsync(
                context,
                repository,
                branch,
                language,
                cancellationToken);
        }
    }

    private async Task TryLogHierarchicalPlanningAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        BranchLanguage language,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (sourceInventoryBuilder is null || domainTopicPlanner is null || coverageAuditor is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            var resolved = await scopeConfigurationService.GetResolvedCurrentAsync(
                repository.Id,
                cancellationToken);
            if (resolved is null || resolved.IsLegacyFallback)
            {
                return;
            }

            var scopeVersion = await context.RepositoryScopeConfigurations
                .AsNoTracking()
                .Where(item => item.RepositoryId == repository.Id && item.IsCurrent && !item.IsDeleted)
                .Select(item => (int?)item.ConfigurationVersion)
                .FirstOrDefaultAsync(cancellationToken);

            var trackedConstraint = await ResolveTrackedInventoryConstraintAsync(
                context,
                repository,
                resolved,
                cancellationToken);

            var request = new GenerationRequest
            {
                Snapshot = new SnapshotIdentity
                {
                    RepositoryId = repository.Id,
                    BranchId = branch.Id,
                    ScopeConfigurationVersion = scopeVersion,
                    TargetChangelist = branch.LastCommitId,
                    TrackedManifestHash = trackedConstraint.ManifestHash,
                    GenerationEngineVersion = GenerationEngineVersions.Hierarchical
                },
                ResolvedScopes = resolved,
                Policy = new GenerationPolicy
                {
                    GenerateLeafContent = false,
                    BlockOnCoverageErrors = false
                },
                WorkingDirectory = workingDirectory,
                RepositoryId = repository.Id,
                BranchId = branch.Id,
                BranchLanguageId = language.Id,
                LanguageCode = language.LanguageCode,
                GenerationId = WikiGenerationContext.CurrentGenerationId,
                EngineId = GenerationEngineIds.Hierarchical,
                AllowedTrackedPaths = trackedConstraint.AllowedTrackedPaths,
                ManifestByPath = trackedConstraint.ManifestByPath
            };

            var inventory = sourceInventoryBuilder.Build(request);
            var domains = domainTopicPlanner.PlanDomains(inventory, request.Policy);
            var manifests = domainTopicPlanner.PlanTopics(inventory, domains, request.Policy);
            var coverage = coverageAuditor.Audit(
                request.Snapshot.ToStableString(),
                inventory,
                domains,
                manifests);

            logger.LogInformation(
                "WP2 inventory/plan ready. Repo={Org}/{Repo}, Branch={Branch}, Lang={Lang}, Files={Files}, Modules={Modules}, Domains={Domains}, Pages={Pages}, InventoryHash={Hash}, CoverageCovered={Covered}, Unassigned={Unassigned}, Blocking={Blocking}",
                repository.OrgName,
                repository.RepoName,
                branch.BranchName,
                language.LanguageCode,
                inventory.TotalDocumentFiles,
                inventory.Modules.Count,
                domains.Count,
                manifests.Count,
                inventory.ContentHash,
                coverage.CoveredCount,
                coverage.UnassignedCount,
                coverage.HasBlockingErrors);

            await LogAsync(
                repository.Id,
                branch.Id,
                generationTaskId: null,
                ProcessingStep.Catalog,
                $"WP2 inventory: files={inventory.TotalDocumentFiles}, modules={inventory.Modules.Count}, domains={domains.Count}, pages={manifests.Count}, hash={inventory.ContentHash}, coverage unassigned={coverage.UnassignedCount}, blocking={coverage.HasBlockingErrors}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 规划失败不阻断 Legacy 生成；记录后继续。
            logger.LogWarning(
                ex,
                "WP2 inventory/planning failed; continuing with legacy WikiGenerator. Repo={Org}/{Repo}",
                repository.OrgName,
                repository.RepoName);
        }
    }

    private async Task<TrackedInventoryConstraint> ResolveTrackedInventoryConstraintAsync(
        IContext context,
        Repository repository,
        ResolvedScopeConfiguration resolved,
        CancellationToken cancellationToken)
    {
        // 优先使用当前 staging generation 已绑定的 tracked/#have manifest。
        var generationId = WikiGenerationContext.CurrentGenerationId;
        if (!string.IsNullOrWhiteSpace(generationId) && workspaceManifestService is not null)
        {
            var generation = await context.WikiGenerations
                .AsNoTracking()
                .Where(item => item.Id == generationId && !item.IsDeleted)
                .Select(item => new { item.ManifestJson, item.TrackedManifestHash })
                .FirstOrDefaultAsync(cancellationToken);

            if (generation is not null
                && workspaceManifestService.TryParseManifestJson(
                    generation.ManifestJson,
                    out var entries,
                    out _)
                && entries.Count > 0)
            {
                var byPath = entries.ToDictionary(
                    entry => entry.RelativePath,
                    entry => entry,
                    StringComparer.OrdinalIgnoreCase);
                return new TrackedInventoryConstraint(
                    new HashSet<string>(byPath.Keys, StringComparer.OrdinalIgnoreCase),
                    byPath,
                    generation.TrackedManifestHash);
            }
        }

        // SubmittedHaveOnly 且缺少 tracked manifest 时，对 Perforce 来源 fail-closed：
        // 传空 allow-list，禁止磁盘任意枚举把 untracked/opened 内容计入 inventory。
        // 非 Perforce 来源保持 null（磁盘扫描），由 Inventory 发出 warning。
        if (resolved.WorkspaceContentPolicy == WorkspaceContentPolicy.SubmittedHaveOnly
            && RepositorySource.IsPerforce(repository.GitUrl))
        {
            logger.LogWarning(
                "SubmittedHaveOnly Perforce inventory has no tracked manifest; using empty allow-list. Repo={Org}/{Repo}",
                repository.OrgName,
                repository.RepoName);
            return new TrackedInventoryConstraint(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, WorkspaceManifestEntry>(StringComparer.OrdinalIgnoreCase),
                ManifestHash: null);
        }

        return TrackedInventoryConstraint.None;
    }

    private sealed record TrackedInventoryConstraint(
        IReadOnlySet<string>? AllowedTrackedPaths,
        IReadOnlyDictionary<string, WorkspaceManifestEntry>? ManifestByPath,
        string? ManifestHash)
    {
        public static TrackedInventoryConstraint None { get; } = new(null, null, null);
    }

    private Task LogAsync(
        string repositoryId,
        string branchId,
        string? generationTaskId,
        ProcessingStep step,
        string message,
        CancellationToken cancellationToken)
    {
        return processingLogService is null
            ? Task.CompletedTask
            : processingLogService.LogAsync(
                repositoryId,
                branchId,
                generationTaskId,
                step,
                message,
                cancellationToken: cancellationToken);
    }
}
