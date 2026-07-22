using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Notifications;
using OpenDeepWiki.Services.Wiki;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Core incremental update service implementation.
/// </summary>
public class IncrementalUpdateService : IIncrementalUpdateService
{
    /// <summary>
    /// 版本标识最大长度，与 <c>RepositoryBranch.LastCommitId</c> /
    /// <c>IncrementalUpdateTask.TargetCommitId</c> / <c>ExternalTargetRevision</c> 列一致。
    /// </summary>
    public const int MaxRevisionIdLength = 40;

    private readonly IRepositoryAnalyzer _repositoryAnalyzer;
    private readonly IWikiGenerator _wikiGenerator;
    private readonly IRepositorySkillMarkdownBuilder _skillMarkdownBuilder;
    private readonly ISubscriberNotificationService _notificationService;
    private readonly IContext _context;
    private readonly IncrementalUpdateOptions _options;
    private readonly ILogger<IncrementalUpdateService> _logger;

    public IncrementalUpdateService(
        IRepositoryAnalyzer repositoryAnalyzer,
        IWikiGenerator wikiGenerator,
        IRepositorySkillMarkdownBuilder skillMarkdownBuilder,
        ISubscriberNotificationService notificationService,
        IContext context,
        IOptions<IncrementalUpdateOptions> options,
        ILogger<IncrementalUpdateService> logger)
    {
        _repositoryAnalyzer = repositoryAnalyzer;
        _wikiGenerator = wikiGenerator;
        _skillMarkdownBuilder = skillMarkdownBuilder;
        _notificationService = notificationService;
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Checking for updates. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
            repositoryId, branchId);

        var repository = await _context.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken);

        if (repository == null)
        {
            _logger.LogWarning("Repository not found. RepositoryId: {RepositoryId}", repositoryId);
            return new UpdateCheckResult { NeedsUpdate = false };
        }

        var branch = await _context.RepositoryBranches
            .FirstOrDefaultAsync(b => b.Id == branchId && !b.IsDeleted, cancellationToken);

        if (branch == null)
        {
            _logger.LogWarning("Branch not found. BranchId: {BranchId}", branchId);
            return new UpdateCheckResult { NeedsUpdate = false };
        }

        var previousCommitId = branch.LastCommitId;

        try
        {
            var workspace = await PrepareWorkspaceWithRetryAsync(
                repository, branch.BranchName, previousCommitId, cancellationToken);

            var currentCommitId = workspace.CommitId;

            if (previousCommitId == currentCommitId)
            {
                _logger.LogInformation(
                    "No changes detected. RepositoryId: {RepositoryId}, CommitId: {CommitId}",
                    repositoryId, currentCommitId);

                return new UpdateCheckResult
                {
                    NeedsUpdate = false,
                    PreviousCommitId = previousCommitId,
                    CurrentCommitId = currentCommitId,
                    ChangedFiles = Array.Empty<string>()
                };
            }

            var changedFiles = await _repositoryAnalyzer.GetChangedFilesAsync(
                workspace, previousCommitId, currentCommitId, cancellationToken);

            stopwatch.Stop();

            _logger.LogInformation(
                "Update check completed. RepositoryId: {RepositoryId}, PreviousCommit: {PreviousCommit}, CurrentCommit: {CurrentCommit}, ChangedFiles: {ChangedFilesCount}, Duration: {Duration}ms",
                repositoryId, previousCommitId ?? "none", currentCommitId, changedFiles.Length, stopwatch.ElapsedMilliseconds);

            return new UpdateCheckResult
            {
                NeedsUpdate = changedFiles.Length > 0 || string.IsNullOrEmpty(previousCommitId),
                PreviousCommitId = previousCommitId,
                CurrentCommitId = currentCommitId,
                ChangedFiles = changedFiles
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "Failed to check for updates. RepositoryId: {RepositoryId}, BranchId: {BranchId}, Duration: {Duration}ms",
                repositoryId, branchId, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IncrementalUpdateResult> ProcessIncrementalUpdateAsync(
        string repositoryId,
        string branchId,
        string? externalChangedFilesJson = null,
        string? externalTargetRevision = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Processing incremental update. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
            repositoryId, branchId);

        try
        {
            var repository = await _context.Repositories
                .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken);

            var branch = await _context.RepositoryBranches
                .FirstOrDefaultAsync(b => b.Id == branchId && !b.IsDeleted, cancellationToken);

            if (repository == null || branch == null)
            {
                throw new InvalidOperationException(
                    $"Repository or branch not found. RepositoryId: {repositoryId}, BranchId: {branchId}");
            }

            var previousCommitId = branch.LastCommitId;
            var workspace = await PrepareWorkspaceWithRetryAsync(
                repository, branch.BranchName, previousCommitId, cancellationToken);
            var currentCommitId = workspace.CommitId;

            // 外部(如 Perforce CI)注入的变更列表优先于内部 diff。
            // 仅 Perforce 源允许用外部 revision 覆盖基线，避免误注入污染 Git 的 LastCommitId。
            // workspace.SourceType 已由 RepositorySource.Parse(repository.GitUrl) 推导，
            // 无需再对同一 GitUrl 重复解析判定。
            var isPerforceSource = workspace.SourceType == RepositorySourceType.Perforce;
            var externalChangedFiles = TryParseExternalChangedFiles(externalChangedFilesJson);
            var hasExternalInjection = externalChangedFiles != null && isPerforceSource;
            if (!isPerforceSource && externalChangedFiles != null)
            {
                _logger.LogWarning(
                    "Ignoring externally injected change list for non-Perforce source. RepositoryId: {RepositoryId}, SourceType: {SourceType}",
                    repositoryId, workspace.SourceType);
            }

            if (hasExternalInjection && !string.IsNullOrWhiteSpace(externalTargetRevision))
            {
                if (externalTargetRevision.Length > MaxRevisionIdLength)
                {
                    throw new InvalidOperationException(
                        $"外部目标版本长度不能超过 {MaxRevisionIdLength} 字符");
                }

                // 幂等/乱序兜底：若目标 changelist 已被当前基线覆盖(相等=重复投递已完成的
                // changelist；更早=乱序投递的旧 changelist)，说明该版本已处理，按无操作成功返回，
                // 避免重复跑 LLM 更新或把基线回退。
                // 入队端 TriggerExternalUpdateAsync 会用 IsRevisionRegression 对明显回退快速拒绝(400)，
                // 但对"任务已完成后重投"或"并发绕过入队去重"只能在处理端实时比对基线兜底。
                if (IsRevisionAtOrBehind(previousCommitId, externalTargetRevision))
                {
                    stopwatch.Stop();
                    _logger.LogInformation(
                        "External target revision {TargetRevision} already reached baseline {Baseline}; treating as idempotent no-op. RepositoryId: {RepositoryId}",
                        externalTargetRevision, previousCommitId, repositoryId);

                    return new IncrementalUpdateResult
                    {
                        Success = true,
                        PreviousCommitId = previousCommitId,
                        CurrentCommitId = previousCommitId,
                        ChangedFilesCount = 0,
                        UpdatedDocumentsCount = 0,
                        Duration = stopwatch.Elapsed
                    };
                }

                // 用外部目标版本(changelist 号)作为当前版本标识，替代工作区目录快照。
                currentCommitId = externalTargetRevision;
                // 同步到工作区上下文：WikiGenerator.IncrementalUpdateAsync 会把
                // workspace.CommitId / PreviousCommitId 写进 LLM 提示的 Runtime Context，
                // 否则 Agent 看到的 Current Commit 仍是旧基线，与实际 changelist 不一致。
                workspace.CommitId = externalTargetRevision;
            }

            // 外部注入是显式的"请按此列表更新"请求，即使版本标识未变也应处理
            // (重复投递的幂等去重在入队端点 TriggerExternalUpdateAsync 完成)；
            // 仅在无外部注入时沿用"版本未变即跳过"的短路逻辑。
            if (!hasExternalInjection && previousCommitId == currentCommitId)
            {
                stopwatch.Stop();
                _logger.LogInformation(
                    "No update needed. RepositoryId: {RepositoryId}, Duration: {Duration}ms",
                    repositoryId, stopwatch.ElapsedMilliseconds);

                return new IncrementalUpdateResult
                {
                    Success = true,
                    PreviousCommitId = previousCommitId,
                    CurrentCommitId = currentCommitId,
                    ChangedFilesCount = 0,
                    UpdatedDocumentsCount = 0,
                    Duration = stopwatch.Elapsed
                };
            }

            string[] changedFiles;
            if (hasExternalInjection)
            {
                changedFiles = externalChangedFiles!;
                _logger.LogInformation(
                    "Using externally injected change list. RepositoryId: {RepositoryId}, TargetRevision: {TargetRevision}, ChangedFiles: {Count}",
                    repositoryId, currentCommitId, changedFiles.Length);
            }
            else
            {
                changedFiles = await _repositoryAnalyzer.GetChangedFilesAsync(
                    workspace,
                    previousCommitId,
                    currentCommitId,
                    cancellationToken);
            }

            if (changedFiles.Length == 0 && !string.IsNullOrEmpty(previousCommitId))
            {
                // 说明：无注入的 Perforce 源在有基线时 workspace.CommitId 恒等于 previousCommitId
                // (见 RepositoryAnalyzer)，因此上面"版本未变即跳过"的短路已先行返回，不会到达此处；
                // 无需再对 Perforce 空内部变更集做特判(此前的守卫是永不执行的死代码)。
                await AdvanceBranchStateAsync(repository, branch, currentCommitId, cancellationToken);

                stopwatch.Stop();
                _logger.LogInformation(
                    "Commit advanced without document changes. RepositoryId: {RepositoryId}, PreviousCommit: {PreviousCommit}, CurrentCommit: {CurrentCommit}, Duration: {Duration}ms",
                    repositoryId, previousCommitId, currentCommitId, stopwatch.ElapsedMilliseconds);

                return new IncrementalUpdateResult
                {
                    Success = true,
                    PreviousCommitId = previousCommitId,
                    CurrentCommitId = currentCommitId,
                    ChangedFilesCount = 0,
                    UpdatedDocumentsCount = 0,
                    Duration = stopwatch.Elapsed
                };
            }

            var branchLanguages = await _context.BranchLanguages
                .Where(bl => bl.RepositoryBranchId == branchId && !bl.IsDeleted)
                .ToListAsync(cancellationToken);

            var updatedDocumentsCount = 0;

            foreach (var branchLanguage in branchLanguages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation(
                    "Updating wiki for language {LanguageCode}. RepositoryId: {RepositoryId}",
                    branchLanguage.LanguageCode, repositoryId);

                await _wikiGenerator.IncrementalUpdateAsync(
                    workspace,
                    branchLanguage,
                    changedFiles,
                    cancellationToken);

                if (repository.GenerateSkill)
                {
                    await _skillMarkdownBuilder.RefreshSkillMarkdownAsync(
                        _context,
                        repository,
                        branch,
                        branchLanguage,
                        cancellationToken);
                }

                updatedDocumentsCount++;
            }

            await AdvanceBranchStateAsync(repository, branch, currentCommitId, cancellationToken);

            await NotifySubscribersSafelyAsync(
                repository,
                branch,
                new UpdateCheckResult
                {
                    NeedsUpdate = true,
                    PreviousCommitId = previousCommitId,
                    CurrentCommitId = currentCommitId,
                    ChangedFiles = changedFiles
                },
                cancellationToken);

            stopwatch.Stop();

            _logger.LogInformation(
                "Incremental update completed. RepositoryId: {RepositoryId}, ChangedFiles: {ChangedFilesCount}, UpdatedLanguages: {UpdatedLanguagesCount}, Duration: {Duration}ms",
                repositoryId, changedFiles.Length, updatedDocumentsCount, stopwatch.ElapsedMilliseconds);

            return new IncrementalUpdateResult
            {
                Success = true,
                PreviousCommitId = previousCommitId,
                CurrentCommitId = currentCommitId,
                ChangedFilesCount = changedFiles.Length,
                UpdatedDocumentsCount = updatedDocumentsCount,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "Incremental update failed. RepositoryId: {RepositoryId}, BranchId: {BranchId}, Duration: {Duration}ms",
                repositoryId, branchId, stopwatch.ElapsedMilliseconds);

            return new IncrementalUpdateResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    /// <inheritdoc />
    public async Task<string> TriggerManualUpdateAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Manual update triggered. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
            repositoryId, branchId);

        var existingTask = await _context.IncrementalUpdateTasks
            .Where(t => !t.IsDeleted &&
                        t.RepositoryId == repositoryId &&
                        t.BranchId == branchId &&
                        (t.Status == IncrementalUpdateStatus.Pending
                            || t.Status == IncrementalUpdateStatus.Processing))
            .FirstOrDefaultAsync(cancellationToken);

        if (existingTask != null)
        {
            _logger.LogInformation(
                "Existing task found. TaskId: {TaskId}, Status: {Status}",
                existingTask.Id, existingTask.Status);
            return existingTask.Id;
        }

        var activeBranchGenerationTask = await _context.BranchGenerationTasks
            .AnyAsync(t => !t.IsDeleted &&
                           t.RepositoryId == repositoryId &&
                           t.BranchId == branchId &&
                           (t.Status == BranchGenerationTaskStatus.Pending ||
                            t.Status == BranchGenerationTaskStatus.Processing),
                cancellationToken);

        if (activeBranchGenerationTask)
        {
            throw new InvalidOperationException("该分支已有 full generation 任务正在排队或处理中");
        }

        var branch = await _context.RepositoryBranches
            .FirstOrDefaultAsync(b => b.Id == branchId && !b.IsDeleted, cancellationToken);

        var task = new IncrementalUpdateTask
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            BranchId = branchId,
            PreviousCommitId = branch?.LastCommitId,
            Status = IncrementalUpdateStatus.Pending,
            Priority = _options.ManualTriggerPriority,
            IsManualTrigger = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.IncrementalUpdateTasks.Add(task);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Manual update task created. TaskId: {TaskId}, Priority: {Priority}",
            task.Id, task.Priority);

        return task.Id;
    }

    /// <inheritdoc />
    public async Task<string> TriggerExternalUpdateAsync(
        string repositoryId,
        string branchId,
        string? targetRevision,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string>? deletedFiles = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "External incremental update injected. RepositoryId: {RepositoryId}, BranchId: {BranchId}, TargetRevision: {TargetRevision}, ChangedFiles: {ChangedCount}, DeletedFiles: {DeletedCount}",
            repositoryId, branchId, targetRevision ?? "none",
            changedFiles?.Count ?? 0, deletedFiles?.Count ?? 0);

        var repository = await _context.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("仓库不存在");

        if (!RepositorySource.IsPerforce(repository.GitUrl))
        {
            throw new ArgumentException(
                "外部注入增量仅支持 Perforce 源仓库",
                nameof(repositoryId));
        }

        var normalizedTargetRevision = string.IsNullOrWhiteSpace(targetRevision)
            ? null
            : targetRevision.Trim();

        if (normalizedTargetRevision != null && normalizedTargetRevision.Length > MaxRevisionIdLength)
        {
            throw new ArgumentException(
                $"targetRevision 长度不能超过 {MaxRevisionIdLength} 字符",
                nameof(targetRevision));
        }

        // targetRevision(若提供)必须为正整数 Perforce changelist 号：确保回退/幂等判定始终能做
        // 数值比较，避免带标签或非数字的 revision 让 IsRevisionRegression 静默放行、绕过单调性校验。
        if (normalizedTargetRevision != null
            && (!long.TryParse(normalizedTargetRevision, out var parsedChangelist) || parsedChangelist <= 0))
        {
            throw new ArgumentException(
                "targetRevision 必须为正整数 Perforce changelist 号",
                nameof(targetRevision));
        }

        var branch = await _context.RepositoryBranches
            .FirstOrDefaultAsync(b => b.Id == branchId && b.RepositoryId == repositoryId && !b.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("分支不存在");

        if (normalizedTargetRevision != null
            && IsRevisionRegression(branch.LastCommitId, normalizedTargetRevision))
        {
            throw new ArgumentException(
                $"targetRevision {normalizedTargetRevision} 早于当前基线 {branch.LastCommitId}，拒绝回退",
                nameof(targetRevision));
        }

        // 同分支同时最多一条未完成增量任务（与手动触发一致），避免不同 revision 并行导致基线回退。
        // 同一 ExternalTargetRevision 的重复注入仍幂等复用。
        var existingTask = await _context.IncrementalUpdateTasks
            .Where(t => !t.IsDeleted &&
                        t.RepositoryId == repositoryId &&
                        t.BranchId == branchId &&
                        (t.Status == IncrementalUpdateStatus.Pending
                            || t.Status == IncrementalUpdateStatus.Processing))
            .FirstOrDefaultAsync(cancellationToken);

        if (existingTask != null)
        {
            var sameRevision = string.Equals(
                existingTask.ExternalTargetRevision,
                normalizedTargetRevision,
                StringComparison.Ordinal);

            if (sameRevision)
            {
                _logger.LogInformation(
                    "Duplicate external update ignored; reusing existing task. TaskId: {TaskId}, Status: {Status}",
                    existingTask.Id, existingTask.Status);
                return existingTask.Id;
            }

            throw new InvalidOperationException(
                $"该分支已有增量更新任务正在排队或处理中 (TaskId: {existingTask.Id}, TargetRevision: {existingTask.ExternalTargetRevision ?? "none"})");
        }

        var activeBranchGenerationTask = await _context.BranchGenerationTasks
            .AnyAsync(t => !t.IsDeleted &&
                           t.RepositoryId == repositoryId &&
                           t.BranchId == branchId &&
                           (t.Status == BranchGenerationTaskStatus.Pending ||
                            t.Status == BranchGenerationTaskStatus.Processing),
                cancellationToken);

        if (activeBranchGenerationTask)
        {
            throw new InvalidOperationException("该分支已有 full generation 任务正在排队或处理中");
        }

        // 引擎(WikiGenerator.IncrementalUpdateAsync)无独立的删除处理路径，但删除仍是必须反映到
        // 文档的变更信号：把已删文件并入变更列表后，增量 Agent 读取失败即可识别其被删除并更新引用，
        // 避免"纯删除 changelist 静默推进基线、却永久遗留失效文档"。合并去重，changedFiles 优先。
        var mergedChangedFiles = MergeChangeAndDeletion(changedFiles, deletedFiles);
        var changedFilesPayload = JsonSerializer.Serialize(mergedChangedFiles);

        var task = new IncrementalUpdateTask
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            BranchId = branchId,
            // PreviousCommitId 仅供观测；实际基线在处理时从 branch.LastCommitId 实时读取。
            PreviousCommitId = branch.LastCommitId is { Length: <= MaxRevisionIdLength }
                ? branch.LastCommitId
                : null,
            ExternalChangedFiles = changedFilesPayload,
            ExternalTargetRevision = normalizedTargetRevision,
            Status = IncrementalUpdateStatus.Pending,
            Priority = _options.ManualTriggerPriority,
            IsManualTrigger = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.IncrementalUpdateTasks.Add(task);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "External incremental update task created. TaskId: {TaskId}, Priority: {Priority}, TargetRevision: {TargetRevision}",
            task.Id, task.Priority, task.ExternalTargetRevision ?? "none");

        return task.Id;
    }

    /// <summary>
    /// 判断外部目标版本是否相对当前基线回退。
    /// 仅当两侧都能解析为整数（Perforce changelist）时做数值比较；否则不做回退判定。
    /// </summary>
    internal static bool IsRevisionRegression(string? currentBaseline, string targetRevision)
    {
        if (string.IsNullOrWhiteSpace(currentBaseline)
            || string.IsNullOrWhiteSpace(targetRevision))
        {
            return false;
        }

        if (long.TryParse(currentBaseline, out var currentCl)
            && long.TryParse(targetRevision, out var targetCl))
        {
            return targetCl < currentCl;
        }

        return false;
    }

    /// <summary>
    /// 判断外部目标版本是否已被当前基线覆盖(数值上相等或更早)。
    /// 仅当两侧都能解析为整数(Perforce changelist)时做数值比较；否则返回 false(无法判定，按未达到处理)。
    /// 用于处理端幂等兜底：重复投递已完成的 changelist、或乱序投递的旧 changelist 都视为已达到。
    /// </summary>
    internal static bool IsRevisionAtOrBehind(string? currentBaseline, string targetRevision)
    {
        if (string.IsNullOrWhiteSpace(currentBaseline)
            || string.IsNullOrWhiteSpace(targetRevision))
        {
            return false;
        }

        if (long.TryParse(currentBaseline, out var currentCl)
            && long.TryParse(targetRevision, out var targetCl))
        {
            return targetCl <= currentCl;
        }

        return false;
    }

    /// <summary>
    /// 解析外部注入的变更文件 JSON 列表。返回 null 表示"无外部注入"(走内部 diff)；
    /// 返回数组(可能为空)表示外部显式提供的变更集合。JSON 非法时按无注入处理以避免误推进基线。
    /// </summary>
    private string[]? TryParseExternalChangedFiles(string? externalChangedFilesJson)
    {
        if (string.IsNullOrWhiteSpace(externalChangedFilesJson))
        {
            return null;
        }

        try
        {
            var files = JsonSerializer.Deserialize<string[]>(externalChangedFilesJson);
            return files ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to deserialize externally injected change list; treating as no external injection. PayloadLength: {Length}",
                externalChangedFilesJson.Length);
            return null;
        }
    }

    /// <summary>
    /// 合并变更文件与删除文件为单一去重列表(保序，changedFiles 优先)，过滤空白项。
    /// </summary>
    private static IReadOnlyList<string> MergeChangeAndDeletion(
        IReadOnlyList<string>? changedFiles,
        IReadOnlyList<string>? deletedFiles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>();

        void Append(IReadOnlyList<string>? files)
        {
            if (files == null)
            {
                return;
            }

            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                if (seen.Add(file))
                {
                    merged.Add(file);
                }
            }
        }

        Append(changedFiles);
        Append(deletedFiles);
        return merged;
    }

    private async Task AdvanceBranchStateAsync(
        Repository repository,
        RepositoryBranch branch,
        string currentCommitId,
        CancellationToken cancellationToken)
    {
        branch.LastCommitId = currentCommitId;
        branch.LastProcessedAt = DateTime.UtcNow;
        branch.UpdatedAt = DateTime.UtcNow;

        repository.LastUpdateCheckAt = DateTime.UtcNow;
        repository.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<RepositoryWorkspace> PrepareWorkspaceWithRetryAsync(
        Repository repository,
        string branchName,
        string? previousCommitId,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        Exception? lastException = null;

        while (retryCount < _options.MaxRetryAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await _repositoryAnalyzer.PrepareWorkspaceAsync(
                    repository, branchName, previousCommitId, cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;

                _logger.LogWarning(ex,
                    "Workspace preparation failed. Attempt {Attempt}/{MaxAttempts}, Repository: {Org}/{Repo}",
                    retryCount, _options.MaxRetryAttempts, repository.OrgName, repository.RepoName);

                if (retryCount < _options.MaxRetryAttempts)
                {
                    if (IsWorkspaceCorrupted(ex))
                    {
                        _logger.LogInformation(
                            "Workspace appears corrupted, cleaning up. Repository: {Org}/{Repo}",
                            repository.OrgName, repository.RepoName);

                        await CleanupCorruptedWorkspaceAsync(repository, cancellationToken);
                    }

                    var delay = _options.RetryBaseDelayMs * (int)Math.Pow(2, retryCount - 1);
                    _logger.LogInformation(
                        "Retrying in {Delay}ms. Repository: {Org}/{Repo}",
                        delay, repository.OrgName, repository.RepoName);

                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to prepare workspace after {_options.MaxRetryAttempts} attempts",
            lastException);
    }

    private static bool IsWorkspaceCorrupted(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("corrupt")
               || message.Contains("invalid")
               || message.Contains("not a git repository")
               || message.Contains("bad object")
               || message.Contains("broken");
    }

    private async Task CleanupCorruptedWorkspaceAsync(
        Repository repository,
        CancellationToken cancellationToken)
    {
        try
        {
            var workspace = new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName
            };

            await _repositoryAnalyzer.CleanupWorkspaceAsync(workspace, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to cleanup corrupted workspace. Repository: {Org}/{Repo}",
                repository.OrgName, repository.RepoName);
        }
    }

    private async Task NotifySubscribersSafelyAsync(
        Repository repository,
        RepositoryBranch branch,
        UpdateCheckResult checkResult,
        CancellationToken cancellationToken)
    {
        try
        {
            var notification = new RepositoryUpdateNotification
            {
                RepositoryId = repository.Id,
                RepositoryName = $"{repository.OrgName}/{repository.RepoName}",
                BranchName = branch.BranchName,
                Summary = $"Updated with {checkResult.ChangedFiles?.Length ?? 0} changed files",
                ChangedFilesCount = checkResult.ChangedFiles?.Length ?? 0,
                UpdatedAt = DateTime.UtcNow,
                CommitId = checkResult.CurrentCommitId ?? string.Empty
            };

            await _notificationService.NotifySubscribersAsync(notification, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to notify subscribers. RepositoryId: {RepositoryId}",
                repository.Id);
        }
    }
}
