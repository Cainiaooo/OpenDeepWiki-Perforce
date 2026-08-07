using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories.Scope;
using OpenDeepWiki.Services.UeKnowledge;

namespace OpenDeepWiki.Services.Context;

public interface IContextAssemblyService
{
    Task<ContextEnvelope> GetChangeReviewContextAsync(
        ChangeReviewContextRequest request,
        CancellationToken cancellationToken = default);

    Task<ContextEnvelope> GetEditorTaskContextAsync(
        EditorTaskContextRequest request,
        CancellationToken cancellationToken = default);

    Task<ContextEnvelope> GetModuleOverviewAsync(
        ModuleOverviewRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 统一 Context Assembly：Web / Chat / MCP 共用，避免分叉检索逻辑。
/// 先通过稳定映射（SourceFiles、路径前缀）定位页面，再按 persona 排序与预算截断。
/// </summary>
public sealed class ContextAssemblyService(
    IContext context,
    IWikiSnapshotResolver snapshotResolver,
    IScopeConfigurationService scopeConfigurationService,
    IUeKnowledgePackageService ueKnowledgePackageService) : IContextAssemblyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ContextEnvelope> GetChangeReviewContextAsync(
        ChangeReviewContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repoInfo = await LoadRepositoryAsync(request.RepositoryId, request.BranchName, cancellationToken);
        if (repoInfo is null)
        {
            return ContextEnvelope.Rejected(
                request.RepositoryId,
                request.BranchName ?? string.Empty,
                request.TargetChangelist,
                ContextReasonCodes.RejectedRepositoryNotFound,
                "仓库不存在",
                persona: ContextPersona.CodeReview);
        }

        var snapshot = await snapshotResolver.ResolveAsync(
            request.RepositoryId,
            repoInfo.BranchName,
            request.LanguageCode,
            request.TargetChangelist,
            new SnapshotResolveOptions
            {
                // AI CR 不得混入明显未来内容：允许有限回退，但不接受未知远超
                AllowStale = false,
                MaxCompatibleFallbackDistance = 5000
            },
            cancellationToken);

        if (snapshot.IsRejected)
        {
            return ToRejectedEnvelope(
                repoInfo,
                request.TargetChangelist,
                request.LanguageCode,
                snapshot,
                ContextPersona.CodeReview);
        }

        var policy = await scopeConfigurationService.GetPolicyAsync(request.RepositoryId, cancellationToken);
        var allPaths = CollectReviewPaths(request);
        var pageIndex = await LoadPublishedPagesAsync(
            snapshot.BranchLanguageId!,
            snapshot.GenerationId,
            cancellationToken);

        var items = new List<ContextItem>();
        var citations = new List<ContextCitation>();
        var warnings = new List<ContextWarning>(snapshot.Warnings);
        var citationSeq = 0;

        warnings.Add(new ContextWarning
        {
            ReasonCode = ContextReasonCodes.AuthorMetadataUntrusted,
            Message = "变更作者与 CL 描述不可作为可信技术结论",
            Severity = "info"
        });

        foreach (var pathEntry in allPaths.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            var path = ScopePathUtility.NormalizeRelativePath(pathEntry.Path);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var scopeDecision = policy.EvaluateDocumentCandidate(path);
            if (!scopeDecision.Accepted)
            {
                var contextDecision = policy.EvaluateContextRead(path);
                warnings.Add(new ContextWarning
                {
                    ReasonCode = ContextReasonCodes.FileOutsideDocumentScope,
                    Message = contextDecision.Accepted
                        ? $"文件在 ContextScope 内但不在 DocumentScope：{path}（{scopeDecision.ReasonCode}）"
                        : $"文件不在 DocumentScope：{path}（{scopeDecision.ReasonCode}）",
                    Severity = "warning",
                    RelatedPath = path
                });

                items.Add(new ContextItem
                {
                    Id = $"gap:{path}",
                    Kind = ContextItemKind.Gap,
                    Title = $"范围外文件: {path}",
                    Summary = scopeDecision.Detail ?? scopeDecision.ReasonCode,
                    Path = path,
                    EvidenceKind = contextDecision.Accepted
                        ? ContextEvidenceKind.ContextOnly
                        : ContextEvidenceKind.LiveUnknown,
                    SelectionReasonCode = ContextReasonCodes.FileOutsideDocumentScope,
                    Score = 0,
                    Attributes = new Dictionary<string, string>
                    {
                        ["action"] = pathEntry.Action,
                        ["scopeReason"] = scopeDecision.ReasonCode
                    }
                });
                continue;
            }

            var matchedPages = MatchPagesForFile(pageIndex, path);
            if (matchedPages.Count == 0)
            {
                // 删除/move 后文件不存在时，仍应保留历史映射尝试
                if (pathEntry.Action is "deleted" or "moved_from")
                {
                    warnings.Add(new ContextWarning
                    {
                        ReasonCode = ContextReasonCodes.FileDeletedHistory,
                        Message = $"已删除/移走文件暂无历史页面映射: {path}",
                        Severity = "warning",
                        RelatedPath = path
                    });
                }
                else
                {
                    warnings.Add(new ContextWarning
                    {
                        ReasonCode = ContextReasonCodes.FileNoEvidence,
                        Message = $"未找到与文件关联的 Wiki 页面: {path}",
                        Severity = "warning",
                        RelatedPath = path
                    });
                }

                items.Add(new ContextItem
                {
                    Id = $"gap:{path}",
                    Kind = ContextItemKind.Gap,
                    Title = $"证据不足: {path}",
                    Summary = "没有足够证据生成确定性风险结论",
                    Path = path,
                    EvidenceKind = ContextEvidenceKind.LiveUnknown,
                    SelectionReasonCode = ContextReasonCodes.GapInsufficientEvidence,
                    Score = 0.1,
                    Attributes = new Dictionary<string, string> { ["action"] = pathEntry.Action }
                });
                continue;
            }

            foreach (var page in matchedPages.Take(3))
            {
                citationSeq++;
                var citationId = $"c{citationSeq}";
                citations.Add(new ContextCitation
                {
                    Id = citationId,
                    SourceKind = "wiki_page",
                    Label = page.Title,
                    Path = page.Path,
                    SnapshotId = snapshot.GenerationId,
                    Fragment = Truncate(page.Content, 400),
                    ScopeKind = "document"
                });

                citationSeq++;
                var sourceCitationId = $"c{citationSeq}";
                citations.Add(new ContextCitation
                {
                    Id = sourceCitationId,
                    SourceKind = "source_file",
                    Label = path,
                    Path = path,
                    SnapshotId = snapshot.GenerationId,
                    ScopeKind = "document"
                });

                items.Add(new ContextItem
                {
                    Id = $"page:{page.Path}:{path}",
                    Kind = ContextItemKind.WikiPage,
                    Title = page.Title,
                    Summary = ExtractRelevantSnippet(page.Content, path, request.ReviewFocus),
                    Content = Truncate(page.Content, 2000),
                    Path = page.Path,
                    DomainId = InferDomainFromPath(page.Path),
                    ModuleId = InferModuleFromSourcePath(path),
                    EvidenceKind = ContextEvidenceKind.Document,
                    SelectionReasonCode = page.MatchReason,
                    Score = page.Score,
                    RelatedPaths = page.SourceFiles,
                    CitationIds = [citationId, sourceCitationId],
                    Attributes = new Dictionary<string, string>
                    {
                        ["changedFile"] = path,
                        ["action"] = pathEntry.Action
                    }
                });

                // 从正文中提取测试/验证提示（启发式）
                var testHints = ExtractTestHints(page.Content);
                var hintIndex = 0;
                foreach (var hint in testHints.Take(2))
                {
                    hintIndex++;
                    items.Add(new ContextItem
                    {
                        Id = $"test:{page.Path}:{hintIndex}",
                        Kind = ContextItemKind.TestHint,
                        Title = "建议验证",
                        Summary = hint,
                        Path = page.Path,
                        EvidenceKind = ContextEvidenceKind.AiSynthesis,
                        SelectionReasonCode = ContextReasonCodes.FileMappedToPage,
                        Score = page.Score * 0.5,
                        CitationIds = [citationId]
                    });
                }
            }
        }

        // 焦点关键词补充检索
        if (!string.IsNullOrWhiteSpace(request.ReviewFocus))
        {
            foreach (var page in pageIndex
                         .Where(p => ContainsIgnoreCase(p.Title, request.ReviewFocus!)
                                     || ContainsIgnoreCase(p.Content, request.ReviewFocus!))
                         .OrderByDescending(p => ScoreTextMatch(p.Title, p.Content, request.ReviewFocus!))
                         .Take(5))
            {
                if (items.Any(i => i.Path == page.Path && i.Kind == ContextItemKind.WikiPage))
                {
                    continue;
                }

                citationSeq++;
                var citationId = $"c{citationSeq}";
                citations.Add(new ContextCitation
                {
                    Id = citationId,
                    SourceKind = "wiki_page",
                    Label = page.Title,
                    Path = page.Path,
                    SnapshotId = snapshot.GenerationId,
                    Fragment = Truncate(page.Content, 400)
                });

                items.Add(new ContextItem
                {
                    Id = $"focus:{page.Path}",
                    Kind = ContextItemKind.WikiPage,
                    Title = page.Title,
                    Summary = ExtractRelevantSnippet(page.Content, request.ReviewFocus!, null),
                    Content = Truncate(page.Content, 1500),
                    Path = page.Path,
                    EvidenceKind = ContextEvidenceKind.Document,
                    SelectionReasonCode = ContextReasonCodes.TitleMatch,
                    Score = ScoreTextMatch(page.Title, page.Content, request.ReviewFocus!),
                    CitationIds = [citationId]
                });
            }
        }

        // 覆盖状态基于截断前的完整分析，避免预算截断被报告为 Complete
        var coverage = InferCoverage(items, allPaths.Count);

        var (budgetedItems, budgetedCitations, budget) = ApplyBudget(
            items,
            citations,
            request.MaxItems,
            request.MaxChars,
            ContextPersona.CodeReview);

        if (budget.Truncated)
        {
            warnings.Add(new ContextWarning
            {
                ReasonCode = ContextReasonCodes.BudgetTruncated,
                Message = $"结果已按预算截断：items={budget.ReturnedItems}/{items.Count}, chars={budget.ReturnedChars}",
                Severity = "info"
            });
            coverage = DegradeCoverageWhenTruncated(coverage);
        }

        return new ContextEnvelope
        {
            RepositoryId = repoInfo.RepositoryId,
            RepositoryFullName = repoInfo.FullName,
            Branch = repoInfo.BranchName,
            RequestedRevision = request.TargetChangelist,
            ResolvedSnapshotId = snapshot.GenerationId,
            ResolvedTargetRevision = snapshot.TargetRevision,
            SnapshotIdentity = snapshot.SnapshotIdentity,
            LanguageCode = snapshot.LanguageCode ?? request.LanguageCode,
            Compatibility = snapshot.Compatibility,
            CoverageStatus = coverage,
            Persona = nameof(ContextPersona.CodeReview),
            Items = budgetedItems,
            Citations = budgetedCitations,
            Warnings = warnings,
            Budget = budget,
            Metadata = new Dictionary<string, string>
            {
                ["baseChangelist"] = request.BaseChangelist ?? string.Empty,
                ["targetChangelist"] = request.TargetChangelist ?? string.Empty,
                ["changedFileCount"] = allPaths.Count.ToString(),
                ["reviewFocus"] = request.ReviewFocus ?? string.Empty
            }
        };
    }

    public async Task<ContextEnvelope> GetEditorTaskContextAsync(
        EditorTaskContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 先按请求/默认分支加载 Wiki 侧仓库身份，再与 Build Identity 声明的分支做握手
        var repoInfo = await LoadRepositoryAsync(request.RepositoryId, request.BranchName, cancellationToken);
        if (repoInfo is null)
        {
            return ContextEnvelope.Rejected(
                request.RepositoryId,
                request.BranchName ?? string.Empty,
                request.BuildIdentity?.BuildChangelist,
                ContextReasonCodes.RejectedRepositoryNotFound,
                "仓库或分支不存在",
                persona: ContextPersona.EditorAgent);
        }

        // Build Identity 跨分支检查：编辑器声明的 branch/stream 必须与 Wiki 分支一致
        if (!string.IsNullOrWhiteSpace(request.BuildIdentity?.Branch)
            && !string.Equals(
                request.BuildIdentity.Branch,
                repoInfo.BranchName,
                StringComparison.OrdinalIgnoreCase))
        {
            return ContextEnvelope.Rejected(
                repoInfo.RepositoryId,
                repoInfo.BranchName,
                request.BuildIdentity?.BuildChangelist,
                ContextReasonCodes.RejectedCrossBranch,
                $"Build Identity 分支 '{request.BuildIdentity!.Branch}' 与仓库分支 '{repoInfo.BranchName}' 不一致",
                repoInfo.FullName,
                request.LanguageCode,
                ContextPersona.EditorAgent);
        }

        // BuildIdentity.Branch 缺省时绑定到已解析的 Wiki 分支，避免 resolver 另选默认分支造成跨分支混用
        var identity = NormalizeBuildIdentity(request.BuildIdentity, repoInfo.BranchName);

        var snapshot = await snapshotResolver.ResolveForBuildIdentityAsync(
            request.RepositoryId,
            identity,
            request.LanguageCode,
            request.RiskLevel,
            new SnapshotResolveOptions
            {
                AllowStale = request.RiskLevel == EditorOperationRiskLevel.Query,
                MaxCompatibleFallbackDistance = 2000
            },
            cancellationToken);

        if (snapshot.IsRejected)
        {
            return ToRejectedEnvelope(
                repoInfo,
                identity.BuildChangelist,
                request.LanguageCode,
                snapshot,
                ContextPersona.EditorAgent);
        }

        var items = new List<ContextItem>();
        var citations = new List<ContextCitation>();
        var warnings = new List<ContextWarning>(snapshot.Warnings);
        var citationSeq = 0;

        var pageIndex = await LoadPublishedPagesAsync(
            snapshot.BranchLanguageId!,
            snapshot.GenerationId,
            cancellationToken);

        var tokens = Tokenize(request.TaskDescription)
            .Concat(request.CurrentAssetOrTypeHints.SelectMany(Tokenize))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Wiki 概念/工作流
        foreach (var page in pageIndex
                     .Select(p => (Page: p, Score: ScoreTokens(p.Title, p.Content, tokens)))
                     .Where(x => x.Score > 0)
                     .OrderByDescending(x => x.Score)
                     .Take(8))
        {
            citationSeq++;
            var citationId = $"c{citationSeq}";
            citations.Add(new ContextCitation
            {
                Id = citationId,
                SourceKind = "wiki_page",
                Label = page.Page.Title,
                Path = page.Page.Path,
                SnapshotId = snapshot.GenerationId,
                Fragment = Truncate(page.Page.Content, 400)
            });

            items.Add(new ContextItem
            {
                Id = $"wiki:{page.Page.Path}",
                Kind = ContextItemKind.WikiPage,
                Title = page.Page.Title,
                Summary = ExtractRelevantSnippet(page.Page.Content, request.TaskDescription, null),
                Content = Truncate(page.Page.Content, 1800),
                Path = page.Page.Path,
                EvidenceKind = ContextEvidenceKind.Document,
                SelectionReasonCode = ContextReasonCodes.TitleMatch,
                Score = page.Score,
                CitationIds = [citationId]
            });
        }

        // UE 事实与 MCP 工具契约
        UeKnowledgeFactIndex? factIndex = null;
        try
        {
            factIndex = await ueKnowledgePackageService.GetCurrentFactIndexAsync(
                request.RepositoryId,
                repoInfo.BranchId,
                cancellationToken);
        }
        catch
        {
            // UE 包可选
        }

        if (factIndex is not null)
        {
            if (factIndex.IsPartial)
            {
                warnings.Add(new ContextWarning
                {
                    ReasonCode = ContextReasonCodes.CoverageWarning,
                    Message = "UE Knowledge Package 为 Partial/Truncated，部分事实可能缺失",
                    Severity = "warning"
                });
            }

            if (!string.IsNullOrWhiteSpace(identity.BuildChangelist)
                && !string.Equals(identity.BuildChangelist, factIndex.BuildChangelist, StringComparison.Ordinal))
            {
                warnings.Add(new ContextWarning
                {
                    ReasonCode = ContextReasonCodes.CompatibleFallback,
                    Message =
                        $"Build CL {identity.BuildChangelist} 与 UE 包 CL {factIndex.BuildChangelist} 不同",
                    Severity = "warning"
                });
            }

            // MCP 契约兼容
            var contractCheck = new UeKnowledgeMcpContractChecker().Check(
                factIndex,
                identity.BuildChangelist ?? factIndex.BuildChangelist,
                request.RequiredMcpToolNames);

            if (!contractCheck.IsCompatible && request.RiskLevel != EditorOperationRiskLevel.Query)
            {
                return ContextEnvelope.Rejected(
                    repoInfo.RepositoryId,
                    repoInfo.BranchName,
                    identity.BuildChangelist,
                    ContextReasonCodes.RejectedIncompatibleContract,
                    "UE MCP 工具契约与当前 Build 不兼容: "
                    + string.Join("; ", contractCheck.MissingTools.Concat(contractCheck.IncompatibleTools)),
                    repoInfo.FullName,
                    request.LanguageCode,
                    ContextPersona.EditorAgent);
            }

            foreach (var warning in contractCheck.Warnings)
            {
                warnings.Add(new ContextWarning
                {
                    ReasonCode = ContextReasonCodes.CompatibleFallback,
                    Message = warning,
                    Severity = "warning"
                });
            }

            foreach (var tool in factIndex.McpTools
                         .Where(t => tokens.Count == 0
                                     || tokens.Any(tok =>
                                         ContainsIgnoreCase(t.ToolName, tok)
                                         || ContainsIgnoreCase(t.Description, tok)))
                         .Take(10))
            {
                items.Add(new ContextItem
                {
                    Id = $"mcp:{tool.ToolName}",
                    Kind = ContextItemKind.McpToolContract,
                    Title = tool.ToolName,
                    Summary = tool.Description,
                    EvidenceKind = ContextEvidenceKind.UeExport,
                    SelectionReasonCode = ContextReasonCodes.UeFactMatch,
                    Score = 5,
                    Attributes = new Dictionary<string, string>
                    {
                        ["preconditions"] = string.Join("; ", tool.Preconditions),
                        ["sideEffects"] = string.Join("; ", tool.SideEffects),
                        ["minBuild"] = tool.MinBuildChangelist ?? string.Empty,
                        ["maxBuild"] = tool.MaxBuildChangelist ?? string.Empty
                    }
                });
            }

            foreach (var schema in factIndex.DataAssetSchemas
                         .Where(s => tokens.Any(tok =>
                             ContainsIgnoreCase(s.TypeName, tok)
                             || ContainsIgnoreCase(s.StableId, tok)))
                         .Take(5))
            {
                items.Add(new ContextItem
                {
                    Id = $"schema:{schema.StableId}",
                    Kind = ContextItemKind.UeFact,
                    Title = $"DataAsset Schema: {schema.TypeName}",
                    Summary = $"Fields: {string.Join(", ", schema.Fields.Select(f => f.Name).Take(12))}",
                    EvidenceKind = ContextEvidenceKind.UeExport,
                    SelectionReasonCode = ContextReasonCodes.UeFactMatch,
                    Score = 4,
                    Attributes = new Dictionary<string, string>
                    {
                        ["superType"] = schema.SuperType ?? string.Empty
                    }
                });
            }

            foreach (var action in factIndex.EditorActions
                         .Where(a => tokens.Any(tok =>
                             ContainsIgnoreCase(a.Name, tok)
                             || ContainsIgnoreCase(a.Description, tok)
                             || ContainsIgnoreCase(a.Category, tok)))
                         .Take(5))
            {
                items.Add(new ContextItem
                {
                    Id = $"workflow:{action.StableId}",
                    Kind = ContextItemKind.Workflow,
                    Title = action.Name,
                    Summary = action.Description,
                    EvidenceKind = ContextEvidenceKind.UeExport,
                    SelectionReasonCode = ContextReasonCodes.UeFactMatch,
                    Score = 3.5,
                    Attributes = new Dictionary<string, string>
                    {
                        ["category"] = action.Category ?? string.Empty,
                        ["preconditions"] = string.Join("; ", action.Preconditions)
                    }
                });
            }
        }
        else
        {
            warnings.Add(new ContextWarning
            {
                ReasonCode = ContextReasonCodes.GapInsufficientEvidence,
                Message = "未找到当前 UE Knowledge Package，资产/Schema/MCP 契约不可用",
                Severity = "warning"
            });
        }

        // 项目命名与安全约束（固定条目，非可执行写命令）
        items.Add(new ContextItem
        {
            Id = "constraint:no-proxy-write",
            Kind = ContextItemKind.Constraint,
            Title = "OpenDeepWiki 不代理 UE 写操作",
            Summary =
                "本上下文仅提供稳定项目知识与约束。实际编辑器写操作必须通过 UE MCP 执行，并在执行后验证结果。",
            EvidenceKind = ContextEvidenceKind.HumanIntent,
            SelectionReasonCode = ContextReasonCodes.LiveQueryRequired,
            Score = 100
        });

        // 需要实时查询的状态清单
        var liveQueries = new[]
        {
            "当前选中 Actor / 资产的完整路径与类名",
            "当前 World / Level 与 PIE 状态",
            "目标资产是否 checked out / 可写",
            "相关 GameplayTag / DataAsset 实例当前值",
            "操作完成后的验证结果（引用完整性、编译错误）"
        };

        for (var liveIndex = 0; liveIndex < liveQueries.Length; liveIndex++)
        {
            items.Add(new ContextItem
            {
                Id = $"live:{liveIndex + 1}",
                Kind = ContextItemKind.LiveQueryRequired,
                Title = "需 UE MCP 实时确认",
                Summary = liveQueries[liveIndex],
                EvidenceKind = ContextEvidenceKind.LiveUnknown,
                SelectionReasonCode = ContextReasonCodes.LiveQueryRequired,
                Score = 50
            });
        }

        var coverage = factIndex is null
            ? ContextCoverageStatus.Partial
            : factIndex.IsPartial
                ? ContextCoverageStatus.CompletedWithCoverageWarnings
                : ContextCoverageStatus.Complete;

        var (budgetedItems, budgetedCitations, budget) = ApplyBudget(
            items,
            citations,
            request.MaxItems,
            request.MaxChars,
            ContextPersona.EditorAgent);

        if (budget.Truncated)
        {
            warnings.Add(new ContextWarning
            {
                ReasonCode = ContextReasonCodes.BudgetTruncated,
                Message = $"结果已按预算截断：items={budget.ReturnedItems}/{items.Count}",
                Severity = "info"
            });
            coverage = DegradeCoverageWhenTruncated(coverage);
        }

        return new ContextEnvelope
        {
            RepositoryId = repoInfo.RepositoryId,
            RepositoryFullName = repoInfo.FullName,
            Branch = repoInfo.BranchName,
            RequestedRevision = identity.BuildChangelist,
            ResolvedSnapshotId = snapshot.GenerationId,
            ResolvedTargetRevision = snapshot.TargetRevision,
            SnapshotIdentity = snapshot.SnapshotIdentity,
            LanguageCode = snapshot.LanguageCode ?? request.LanguageCode,
            Compatibility = snapshot.Compatibility,
            CoverageStatus = coverage,
            Persona = nameof(ContextPersona.EditorAgent),
            Items = budgetedItems,
            Citations = budgetedCitations,
            Warnings = warnings,
            Budget = budget,
            Metadata = new Dictionary<string, string>
            {
                ["task"] = Truncate(request.TaskDescription, 200) ?? string.Empty,
                ["riskLevel"] = request.RiskLevel.ToString(),
                ["engineVersion"] = identity.EngineVersion ?? string.Empty,
                ["targetPlatform"] = identity.TargetPlatform ?? string.Empty,
                ["ueMcpContractVersion"] = identity.UeMcpContractVersion ?? string.Empty,
                ["projectId"] = identity.ProjectId ?? string.Empty,
                ["hasUePackage"] = (factIndex is not null).ToString()
            }
        };
    }

    public async Task<ContextEnvelope> GetModuleOverviewAsync(
        ModuleOverviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repoInfo = await LoadRepositoryAsync(request.RepositoryId, request.BranchName, cancellationToken);
        if (repoInfo is null)
        {
            return ContextEnvelope.Rejected(
                request.RepositoryId,
                request.BranchName ?? string.Empty,
                request.TargetChangelist,
                ContextReasonCodes.RejectedRepositoryNotFound,
                "仓库不存在",
                persona: ContextPersona.Onboarding);
        }

        var snapshot = await snapshotResolver.ResolveAsync(
            request.RepositoryId,
            repoInfo.BranchName,
            request.LanguageCode,
            request.TargetChangelist,
            new SnapshotResolveOptions { AllowStale = true },
            cancellationToken);

        if (snapshot.IsRejected)
        {
            return ToRejectedEnvelope(
                repoInfo,
                request.TargetChangelist,
                request.LanguageCode,
                snapshot,
                ContextPersona.Onboarding);
        }

        var pageIndex = await LoadPublishedPagesAsync(
            snapshot.BranchLanguageId!,
            snapshot.GenerationId,
            cancellationToken);

        var query = request.ModuleOrDomainQuery.Trim();
        var tokens = Tokenize(query);
        var ranked = pageIndex
            .Select(p =>
            {
                var score = ScoreTokens(p.Title, p.Content, tokens);
                if (ContainsIgnoreCase(p.Path, query) || ContainsIgnoreCase(p.Title, query))
                {
                    score += 10;
                }

                foreach (var source in p.SourceFiles)
                {
                    if (ContainsIgnoreCase(source, query))
                    {
                        score += 5;
                    }
                }

                return (Page: p, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Page.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<ContextItem>();
        var citations = new List<ContextCitation>();
        var warnings = new List<ContextWarning>(snapshot.Warnings);
        var citationSeq = 0;

        // 渐进式结构：1 职责概览 → 2 入口 → 3 依赖 → 4 风险 → 5 阅读路径
        if (ranked.Count == 0)
        {
            items.Add(new ContextItem
            {
                Id = "gap:module",
                Kind = ContextItemKind.Gap,
                Title = $"未找到模块/领域: {query}",
                Summary = "请尝试更精确的模块名、插件名或源码路径",
                EvidenceKind = ContextEvidenceKind.LiveUnknown,
                SelectionReasonCode = ContextReasonCodes.GapInsufficientEvidence,
                Score = 0
            });
        }
        else
        {
            var top = ranked[0].Page;
            citationSeq++;
            var overviewCitation = $"c{citationSeq}";
            citations.Add(new ContextCitation
            {
                Id = overviewCitation,
                SourceKind = "wiki_page",
                Label = top.Title,
                Path = top.Path,
                SnapshotId = snapshot.GenerationId,
                Fragment = Truncate(top.Content, 500)
            });

            items.Add(new ContextItem
            {
                Id = "overview:responsibility",
                Kind = ContextItemKind.ModuleOverview,
                Title = $"1. 职责与边界 — {top.Title}",
                Summary = ExtractSectionOrLead(top.Content, "职责", "边界", "概述", "Overview"),
                Content = Truncate(top.Content, 2000),
                Path = top.Path,
                ModuleId = query,
                DomainId = InferDomainFromPath(top.Path),
                EvidenceKind = ContextEvidenceKind.Document,
                SelectionReasonCode = ContextReasonCodes.ModulePathMatch,
                Score = ranked[0].Score + 20,
                RelatedPaths = top.SourceFiles,
                CitationIds = [overviewCitation]
            });

            items.Add(new ContextItem
            {
                Id = "overview:entrypoints",
                Kind = ContextItemKind.ModuleOverview,
                Title = "2. 关键入口与生命周期",
                Summary = ExtractSectionOrLead(top.Content, "入口", "生命周期", "流程", "Lifecycle", "Entry"),
                Path = top.Path,
                ModuleId = query,
                EvidenceKind = ContextEvidenceKind.Document,
                SelectionReasonCode = ContextReasonCodes.ModulePathMatch,
                Score = ranked[0].Score + 15,
                RelatedPaths = top.SourceFiles.Take(8).ToList(),
                CitationIds = [overviewCitation]
            });

            var related = ranked.Skip(1).Take(5).ToList();
            items.Add(new ContextItem
            {
                Id = "overview:dependencies",
                Kind = ContextItemKind.ModuleOverview,
                Title = "3. 上下游依赖与相关页面",
                Summary = related.Count == 0
                    ? "暂无额外相关页面"
                    : string.Join("; ", related.Select(r => $"{r.Page.Title} ({r.Page.Path})")),
                ModuleId = query,
                EvidenceKind = ContextEvidenceKind.Document,
                SelectionReasonCode = ContextReasonCodes.PathPrefixMatch,
                Score = ranked[0].Score + 10,
                RelatedPaths = related.Select(r => r.Page.Path).ToList()
            });

            items.Add(new ContextItem
            {
                Id = "overview:risks",
                Kind = ContextItemKind.ModuleOverview,
                Title = "4. 常见修改位置、测试入口与风险",
                Summary = ExtractSectionOrLead(top.Content, "风险", "测试", "注意", "Risk", "Test", "修改"),
                Path = top.Path,
                ModuleId = query,
                EvidenceKind = ContextEvidenceKind.Document,
                SelectionReasonCode = ContextReasonCodes.FileMappedToPage,
                Score = ranked[0].Score + 8,
                CitationIds = [overviewCitation]
            });

            var readingPath = ranked.Take(6)
                .Select((r, i) => $"{i + 1}. {r.Page.Title} — {r.Page.Path}")
                .ToList();

            items.Add(new ContextItem
            {
                Id = "overview:reading-path",
                Kind = ContextItemKind.ModuleOverview,
                Title = "5. 进一步阅读路径与源码入口",
                Summary = string.Join("\n", readingPath),
                ModuleId = query,
                EvidenceKind = ContextEvidenceKind.Document,
                SelectionReasonCode = ContextReasonCodes.ModulePathMatch,
                Score = ranked[0].Score + 5,
                RelatedPaths = top.SourceFiles.Take(10).ToList()
            });

            // 附带 top pages 作为可深入阅读的叶子
            foreach (var entry in ranked.Take(5))
            {
                citationSeq++;
                var cid = $"c{citationSeq}";
                citations.Add(new ContextCitation
                {
                    Id = cid,
                    SourceKind = "wiki_page",
                    Label = entry.Page.Title,
                    Path = entry.Page.Path,
                    SnapshotId = snapshot.GenerationId,
                    Fragment = Truncate(entry.Page.Content, 300)
                });

                items.Add(new ContextItem
                {
                    Id = $"page:{entry.Page.Path}",
                    Kind = ContextItemKind.WikiPage,
                    Title = entry.Page.Title,
                    Summary = Truncate(entry.Page.Content, 300),
                    Path = entry.Page.Path,
                    EvidenceKind = ContextEvidenceKind.Document,
                    SelectionReasonCode = ContextReasonCodes.TitleMatch,
                    Score = entry.Score,
                    RelatedPaths = entry.Page.SourceFiles,
                    CitationIds = [cid]
                });
            }
        }

        var coverage = ranked.Count == 0
            ? ContextCoverageStatus.Partial
            : ContextCoverageStatus.Complete;

        var (budgetedItems, budgetedCitations, budget) = ApplyBudget(
            items,
            citations,
            request.MaxItems,
            request.MaxChars,
            ContextPersona.Onboarding);

        if (budget.Truncated)
        {
            warnings.Add(new ContextWarning
            {
                ReasonCode = ContextReasonCodes.BudgetTruncated,
                Message = $"结果已按预算截断：items={budget.ReturnedItems}/{items.Count}",
                Severity = "info"
            });
            coverage = DegradeCoverageWhenTruncated(coverage);
        }

        return new ContextEnvelope
        {
            RepositoryId = repoInfo.RepositoryId,
            RepositoryFullName = repoInfo.FullName,
            Branch = repoInfo.BranchName,
            RequestedRevision = request.TargetChangelist,
            ResolvedSnapshotId = snapshot.GenerationId,
            ResolvedTargetRevision = snapshot.TargetRevision,
            SnapshotIdentity = snapshot.SnapshotIdentity,
            LanguageCode = snapshot.LanguageCode ?? request.LanguageCode,
            Compatibility = snapshot.Compatibility,
            CoverageStatus = coverage,
            Persona = nameof(ContextPersona.Onboarding),
            Items = budgetedItems,
            Citations = budgetedCitations,
            Warnings = warnings,
            Budget = budget,
            Metadata = new Dictionary<string, string>
            {
                ["moduleQuery"] = query,
                ["matchedPageCount"] = ranked.Count.ToString()
            }
        };
    }

    private async Task<RepoBranchInfo?> LoadRepositoryAsync(
        string repositoryId,
        string? branchName,
        CancellationToken cancellationToken)
    {
        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken);

        if (repository is null)
        {
            return null;
        }

        var branchQuery = context.RepositoryBranches
            .AsNoTracking()
            .Where(b => b.RepositoryId == repositoryId && !b.IsDeleted);

        RepositoryBranch? branch;
        if (!string.IsNullOrWhiteSpace(branchName))
        {
            branch = await branchQuery.FirstOrDefaultAsync(b => b.BranchName == branchName, cancellationToken);
        }
        else
        {
            branch = await branchQuery
                .OrderBy(b => b.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (branch is null)
        {
            return null;
        }

        return new RepoBranchInfo(
            repository.Id,
            $"{repository.OrgName}/{repository.RepoName}",
            branch.Id,
            branch.BranchName);
    }

    private async Task<List<PageIndexEntry>> LoadPublishedPagesAsync(
        string branchLanguageId,
        string? generationId,
        CancellationToken cancellationToken)
    {
        var catalogs = WikiPublicationQuery.FilterVisibleCatalogs(
                context.DocCatalogs.AsNoTracking(),
                branchLanguageId,
                generationId)
            .Where(c => !string.IsNullOrEmpty(c.DocFileId));

        var files = WikiPublicationQuery.FilterVisibleFiles(
            context.DocFiles.AsNoTracking(),
            branchLanguageId,
            generationId);

        var rows = await catalogs
            .Join(
                files,
                c => c.DocFileId!,
                f => f.Id,
                (c, f) => new
                {
                    c.Title,
                    c.Path,
                    f.Content,
                    f.SourceFiles
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new PageIndexEntry
            {
                Title = r.Title,
                Path = r.Path,
                Content = r.Content ?? string.Empty,
                SourceFiles = ParseSourceFiles(r.SourceFiles),
                MatchReason = ContextReasonCodes.FileMappedToPage,
                Score = 1
            })
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<PathAction> CollectReviewPaths(ChangeReviewContextRequest request)
    {
        var list = new List<PathAction>();
        foreach (var path in request.ChangedFiles)
        {
            list.Add(new PathAction(path, "changed"));
        }

        foreach (var path in request.DeletedFiles)
        {
            list.Add(new PathAction(path, "deleted"));
        }

        foreach (var moved in request.MovedFiles)
        {
            list.Add(new PathAction(moved.OldPath, "moved_from"));
            list.Add(new PathAction(moved.NewPath, "moved_to"));
        }

        return list
            .GroupBy(p => ScopePathUtility.NormalizeRelativePath(p.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<PageIndexEntry> MatchPagesForFile(IReadOnlyList<PageIndexEntry> pages, string filePath)
    {
        var normalized = ScopePathUtility.NormalizeRelativePath(filePath);
        var results = new List<PageIndexEntry>();

        foreach (var page in pages)
        {
            double score = 0;
            var reason = ContextReasonCodes.FileMappedToPage;

            foreach (var source in page.SourceFiles)
            {
                var sourceNorm = ScopePathUtility.NormalizeRelativePath(source);
                if (string.Equals(sourceNorm, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    score = 100;
                    reason = ContextReasonCodes.SourceFilesMatch;
                    break;
                }

                if (PathEqualsOrEndsWithSegment(sourceNorm, normalized)
                    || PathEqualsOrEndsWithSegment(normalized, sourceNorm))
                {
                    score = Math.Max(score, 80);
                    reason = ContextReasonCodes.SourceFilesMatch;
                }
            }

            // 路径前缀/文件名启发式（目录匹配要求路径段边界，避免 Combat 误匹配 CombatUI）
            var fileName = Path.GetFileNameWithoutExtension(normalized);
            if (score < 80 && !string.IsNullOrEmpty(fileName))
            {
                if (ContainsIgnoreCase(page.Title, fileName) || ContainsIgnoreCase(page.Path, fileName))
                {
                    score = Math.Max(score, 40);
                    reason = ContextReasonCodes.TitleMatch;
                }

                var dir = ScopePathUtility.NormalizeRelativePath(Path.GetDirectoryName(normalized) ?? string.Empty);
                if (!string.IsNullOrEmpty(dir)
                    && page.SourceFiles.Any(s =>
                        IsUnderDirectory(ScopePathUtility.NormalizeRelativePath(s), dir)))
                {
                    score = Math.Max(score, 30);
                    reason = ContextReasonCodes.PathPrefixMatch;
                }
            }

            if (score > 0)
            {
                results.Add(new PageIndexEntry
                {
                    Title = page.Title,
                    Path = page.Path,
                    Content = page.Content,
                    SourceFiles = page.SourceFiles,
                    MatchReason = reason,
                    Score = score
                });
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (IReadOnlyList<ContextItem> Items, IReadOnlyList<ContextCitation> Citations, ContextBudgetInfo Budget)
        ApplyBudget(
            IReadOnlyList<ContextItem> items,
            IReadOnlyList<ContextCitation> citations,
            int maxItems,
            int maxChars,
            ContextPersona persona)
    {
        maxItems = Math.Clamp(maxItems, 1, 100);
        maxChars = Math.Clamp(maxChars, 500, 200_000);

        var ordered = items
            .OrderByDescending(i => PersonaBoost(i, persona) + i.Score)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        var selected = new List<ContextItem>();
        var usedChars = 0;
        var usedCitationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in ordered)
        {
            if (selected.Count >= maxItems)
            {
                break;
            }

            var itemChars = EstimateChars(item);
            if (selected.Count > 0 && usedChars + itemChars > maxChars)
            {
                break;
            }

            selected.Add(item);
            usedChars += itemChars;
            foreach (var cid in item.CitationIds)
            {
                usedCitationIds.Add(cid);
            }
        }

        var selectedCitations = citations
            .Where(c => usedCitationIds.Contains(c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        return (
            selected,
            selectedCitations,
            new ContextBudgetInfo
            {
                MaxItems = maxItems,
                MaxChars = maxChars,
                ReturnedItems = selected.Count,
                ReturnedChars = usedChars,
                Truncated = selected.Count < items.Count || usedChars >= maxChars
            });
    }

    private static double PersonaBoost(ContextItem item, ContextPersona persona)
    {
        return persona switch
        {
            ContextPersona.CodeReview => item.Kind switch
            {
                ContextItemKind.Gap => 30,
                ContextItemKind.TestHint => 20,
                ContextItemKind.Constraint => 25,
                ContextItemKind.WikiPage => 15,
                ContextItemKind.SourceCitation => 10,
                _ => 0
            },
            ContextPersona.EditorAgent => item.Kind switch
            {
                ContextItemKind.Constraint => 40,
                ContextItemKind.LiveQueryRequired => 35,
                ContextItemKind.McpToolContract => 25,
                ContextItemKind.Workflow => 20,
                ContextItemKind.UeFact => 18,
                ContextItemKind.WikiPage => 10,
                _ => 0
            },
            ContextPersona.Onboarding => item.Kind switch
            {
                ContextItemKind.ModuleOverview => 40,
                ContextItemKind.WikiPage => 15,
                _ => 0
            },
            _ => 0
        };
    }

    private static int EstimateChars(ContextItem item)
        => (item.Title?.Length ?? 0)
           + (item.Summary?.Length ?? 0)
           + (item.Content?.Length ?? 0)
           + 32;

    private static ContextEnvelope ToRejectedEnvelope(
        RepoBranchInfo repo,
        string? requestedRevision,
        string? languageCode,
        SnapshotResolveResult snapshot,
        ContextPersona persona)
    {
        var primary = snapshot.Warnings.LastOrDefault(w => w.Severity == "error")
                      ?? snapshot.Warnings.LastOrDefault();

        return new ContextEnvelope
        {
            RepositoryId = repo.RepositoryId,
            RepositoryFullName = repo.FullName,
            Branch = repo.BranchName,
            RequestedRevision = requestedRevision,
            Compatibility = ContextCompatibility.Rejected,
            CoverageStatus = ContextCoverageStatus.None,
            LanguageCode = languageCode,
            Persona = persona.ToString(),
            Items = [],
            Citations = [],
            Warnings = snapshot.Warnings,
            Metadata = primary is null
                ? null
                : new Dictionary<string, string> { ["rejectReason"] = primary.ReasonCode }
        };
    }

    private static ContextCoverageStatus InferCoverage(
        IReadOnlyList<ContextItem> items,
        int pathCount)
    {
        if (pathCount == 0)
        {
            return ContextCoverageStatus.Unknown;
        }

        var gaps = items.Count(i => i.Kind == ContextItemKind.Gap);
        var pages = items.Count(i => i.Kind == ContextItemKind.WikiPage);
        if (pages == 0)
        {
            return ContextCoverageStatus.Partial;
        }

        if (gaps > 0)
        {
            return ContextCoverageStatus.CompletedWithCoverageWarnings;
        }

        return ContextCoverageStatus.Complete;
    }

    /// <summary>
    /// 预算截断不得报告为完整成功。
    /// </summary>
    private static ContextCoverageStatus DegradeCoverageWhenTruncated(ContextCoverageStatus coverage)
        => coverage switch
        {
            ContextCoverageStatus.Complete => ContextCoverageStatus.CompletedWithCoverageWarnings,
            ContextCoverageStatus.Unknown => ContextCoverageStatus.Partial,
            _ => coverage
        };

    private static BuildIdentity NormalizeBuildIdentity(BuildIdentity? raw, string resolvedBranchName)
    {
        return new BuildIdentity
        {
            ProjectId = raw?.ProjectId,
            Branch = string.IsNullOrWhiteSpace(raw?.Branch) ? resolvedBranchName : raw!.Branch.Trim(),
            BuildChangelist = raw?.BuildChangelist,
            BuildVersion = raw?.BuildVersion,
            EngineVersion = raw?.EngineVersion,
            TargetPlatform = raw?.TargetPlatform,
            UeMcpContractVersion = raw?.UeMcpContractVersion
        };
    }

    /// <summary>
    /// 目录前缀匹配要求完整路径段边界：dir 本身或其后紧跟 '/'。
    /// </summary>
    private static bool IsUnderDirectory(string path, string directory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var dir = directory.TrimEnd('/');
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }

        return path.Equals(dir, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 路径相等或以 "/suffix" 结尾（段边界），避免 Combat 误匹配 MyCombat。
    /// </summary>
    private static bool PathEqualsOrEndsWithSegment(string fullPath, string suffixOrEqual)
    {
        if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(suffixOrEqual))
        {
            return false;
        }

        if (fullPath.Equals(suffixOrEqual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.EndsWith("/" + suffixOrEqual, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseSourceFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string? ExtractRelevantSnippet(string content, string needle, string? focus)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var lines = content.Split('\n');
        var searchTerms = new List<string> { needle };
        if (!string.IsNullOrWhiteSpace(focus))
        {
            searchTerms.Add(focus!);
        }

        foreach (var term in searchTerms)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    var start = Math.Max(0, i - 1);
                    return string.Join('\n', lines.Skip(start).Take(5)).Trim();
                }
            }
        }

        return Truncate(content, 300);
    }

    private static string? ExtractSectionOrLead(string content, params string[] headings)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (headings.Any(h => line.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                var block = new StringBuilder();
                for (var j = i + 1; j < lines.Length && j < i + 12; j++)
                {
                    if (lines[j].TrimStart().StartsWith('#') && j > i + 1)
                    {
                        break;
                    }

                    block.AppendLine(lines[j]);
                }

                var text = block.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return Truncate(text, 600);
                }
            }
        }

        return Truncate(content, 400);
    }

    private static List<string> ExtractTestHints(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var hints = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 8)
            {
                continue;
            }

            if (ContainsIgnoreCase(trimmed, "test")
                || ContainsIgnoreCase(trimmed, "测试")
                || ContainsIgnoreCase(trimmed, "verify")
                || ContainsIgnoreCase(trimmed, "验证")
                || ContainsIgnoreCase(trimmed, "Automation"))
            {
                hints.Add(trimmed.TrimStart('-', '*', ' ', '\t'));
            }

            if (hints.Count >= 5)
            {
                break;
            }
        }

        return hints;
    }

    private static string? InferDomainFromPath(string path)
    {
        var parts = ScopePathUtility.NormalizeRelativePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    private static string? InferModuleFromSourcePath(string path)
    {
        var normalized = ScopePathUtility.NormalizeRelativePath(path);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // UE 常见: .../Source/ModuleName/...
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "Source", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[i], "Plugins", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return parts.Length > 1 ? parts[^2] : parts.FirstOrDefault();
    }

    private static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split([' ', '\t', '\r', '\n', ',', '.', '/', '\\', ':', ';', '|', '(', ')', '[', ']', '{', '}', '"', '\'', '-', '_'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double ScoreTokens(string title, string content, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        double score = 0;
        foreach (var token in tokens)
        {
            if (ContainsIgnoreCase(title, token))
            {
                score += 5;
            }

            if (ContainsIgnoreCase(content, token))
            {
                score += 1;
            }
        }

        return score;
    }

    private static double ScoreTextMatch(string title, string content, string query)
        => ScoreTokens(title, content, Tokenize(query));

    private static bool ContainsIgnoreCase(string? haystack, string? needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return false;
        }

        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max] + "...";
    }

    private sealed record RepoBranchInfo(
        string RepositoryId,
        string FullName,
        string BranchId,
        string BranchName);

    private sealed class PageIndexEntry
    {
        public required string Title { get; init; }

        public required string Path { get; init; }

        public required string Content { get; init; }

        public required IReadOnlyList<string> SourceFiles { get; init; }

        public required string MatchReason { get; init; }

        public double Score { get; init; }
    }

    private sealed record PathAction(string Path, string Action);
}
