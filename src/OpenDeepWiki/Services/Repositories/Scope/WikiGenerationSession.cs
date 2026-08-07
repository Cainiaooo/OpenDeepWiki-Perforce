using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories.Scope;

/// <summary>
/// 全量生成会话：绑定 Scope 策略、staging generation，并在成功发布/失败清理。
/// </summary>
public sealed class WikiGenerationSession : IAsyncDisposable
{
    private readonly IContext? _context;
    private readonly IWikiGenerationService? _wikiGenerationService;
    private readonly WikiGeneration? _generation;
    private bool _completed;

    private WikiGenerationSession(
        IContext? context,
        IWikiGenerationService? wikiGenerationService,
        WikiGeneration? generation)
    {
        _context = context;
        _wikiGenerationService = wikiGenerationService;
        _generation = generation;
    }

    public string? GenerationId => _generation?.Id;

    public static async Task<WikiGenerationSession> BeginFullGenerationAsync(
        IContext context,
        IWikiGenerationService wikiGenerationService,
        IScopeConfigurationService scopeConfigurationService,
        IRepositoryFileSelectionPolicyFactory policyFactory,
        Repository repository,
        RepositoryBranch branch,
        BranchLanguage language,
        string? ownerTaskId,
        string? ownerTaskType,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await ApplySelectionPolicyAsync(
            scopeConfigurationService,
            policyFactory,
            repository.Id,
            workingDirectory,
            cancellationToken);

        int? scopeVersion = null;
        string? scopeHash = null;
        var currentConfig = await context.RepositoryScopeConfigurations
            .AsNoTracking()
            .Where(item => item.RepositoryId == repository.Id && item.IsCurrent && !item.IsDeleted)
            .Select(item => new { item.ConfigurationVersion, item.ContentHash })
            .FirstOrDefaultAsync(cancellationToken);
        if (currentConfig is not null)
        {
            scopeVersion = currentConfig.ConfigurationVersion;
            scopeHash = currentConfig.ContentHash;
        }

        var generation = await wikiGenerationService.BeginStagingAsync(
            context,
            new BeginWikiGenerationRequest
            {
                RepositoryId = repository.Id,
                BranchId = branch.Id,
                BranchLanguageId = language.Id,
                LanguageCode = language.LanguageCode,
                ScopeConfigurationVersion = scopeVersion,
                ScopeContentHash = scopeHash,
                TargetRevision = branch.LastCommitId,
                GenerationEngineVersion = SnapshotIdentityBuilder.GenerationEngineVersion,
                OwnerTaskId = ownerTaskId,
                OwnerTaskType = ownerTaskType
            },
            cancellationToken);

        WikiGenerationContext.CurrentGenerationId = generation.Id;
        return new WikiGenerationSession(context, wikiGenerationService, generation);
    }

    public static async Task<WikiGenerationSession> BeginPolicyOnlyAsync(
        IScopeConfigurationService scopeConfigurationService,
        IRepositoryFileSelectionPolicyFactory policyFactory,
        string repositoryId,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await ApplySelectionPolicyAsync(
            scopeConfigurationService,
            policyFactory,
            repositoryId,
            workingDirectory,
            cancellationToken);

        // 增量写遗留/已发布 generation，不创建 staging。
        // 若已有 published generation，Agent 写入该 generation 需要 CurrentGenerationId 指向它。
        return new WikiGenerationSession(null, null, null);
    }

    public static async Task ApplySelectionPolicyAsync(
        IScopeConfigurationService scopeConfigurationService,
        IRepositoryFileSelectionPolicyFactory policyFactory,
        string repositoryId,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopeConfigurationService.GetResolvedCurrentAsync(repositoryId, cancellationToken);
        WikiGenerationContext.SelectionPolicy = resolved is null
            ? policyFactory.CreateLegacyFallback(workingDirectory)
            : policyFactory.Create(resolved, workingDirectory);
    }

    public async Task PublishAsync(CancellationToken cancellationToken = default)
    {
        if (_generation is null || _wikiGenerationService is null || _context is null || _completed)
        {
            _completed = true;
            return;
        }

        await _wikiGenerationService.PublishAsync(_context, _generation.Id, cancellationToken);
        _completed = true;
    }

    public async Task FailAsync(string error, CancellationToken cancellationToken = default)
    {
        if (_generation is null || _wikiGenerationService is null || _context is null || _completed)
        {
            _completed = true;
            return;
        }

        await _wikiGenerationService.FailAsync(_context, _generation.Id, error, cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_generation is not null
                && _wikiGenerationService is not null
                && _context is not null
                && !_completed)
            {
                await _wikiGenerationService.FailAsync(
                    _context,
                    _generation.Id,
                    "Generation session disposed without publish.",
                    CancellationToken.None);
            }
        }
        finally
        {
            WikiGenerationContext.Clear();
        }
    }
}
