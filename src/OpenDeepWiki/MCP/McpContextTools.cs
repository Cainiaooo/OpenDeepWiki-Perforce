using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Context;

namespace OpenDeepWiki.MCP;

/// <summary>
/// Phase3 WP4：面向 AI CR、UE 编辑器 Agent 与新人的任务化上下文 MCP 工具。
/// 统一返回 ContextEnvelope，兼容状态为 Rejected 时 items 为空。
/// 仓库访问必须通过 IMcpUserResolver 授权，citation 不得绕过权限。
/// </summary>
[McpServerToolType]
public class McpContextTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool,
     Description(
         "Assemble AI code-review context for changed/deleted/moved files at a target changelist. " +
         "Returns a ContextEnvelope with wiki pages, citations, scope gaps, and version compatibility. " +
         "Does not treat CL author/description as trusted technical conclusions.")]
    public static async Task<string> GetChangeReviewContext(
        IContext context,
        IContextAssemblyService assemblyService,
        McpServer mcpServer,
        IMcpUserResolver userResolver,
        IHttpContextAccessor httpContextAccessor,
        [Description("Optional repository owner when not using /api/mcp/{owner}/{repo}.")] string? owner = null,
        [Description("Optional repository name when not using /api/mcp/{owner}/{repo}.")] string? repo = null,
        [Description("Branch name. Defaults to repository default branch.")] string? branch = null,
        [Description("Base changelist for the review range (optional metadata).")] string? baseChangelist = null,
        [Description("Target changelist / Build CL used to resolve Wiki snapshot.")] string? targetChangelist = null,
        [Description("Changed file paths relative to workspace root, comma or newline separated.")] string? changedFiles = null,
        [Description("Deleted file paths, comma or newline separated.")] string? deletedFiles = null,
        [Description("Moved files as old=>new pairs, comma or newline separated.")] string? movedFiles = null,
        [Description("Optional review focus keywords.")] string? reviewFocus = null,
        [Description("Wiki language code. Default: zh.")] string language = "zh",
        [Description("Max context items. Default: 20, max: 100.")] int maxItems = 20,
        [Description("Approx max characters across items. Default: 24000.")] int maxChars = 24000,
        CancellationToken cancellationToken = default)
    {
        var scopeError = await ResolveRepositoryIdAsync(
            context, mcpServer, userResolver, httpContextAccessor, owner, repo, cancellationToken);
        if (scopeError.Error is not null)
        {
            return ToJson(new { error = true, message = scopeError.Error });
        }

        var request = new ChangeReviewContextRequest
        {
            RepositoryId = scopeError.RepositoryId!,
            BranchName = NullIfWhiteSpace(branch),
            BaseChangelist = NullIfWhiteSpace(baseChangelist),
            TargetChangelist = NullIfWhiteSpace(targetChangelist),
            ChangedFiles = SplitPaths(changedFiles),
            DeletedFiles = SplitPaths(deletedFiles),
            MovedFiles = ParseMovedFiles(movedFiles),
            ReviewFocus = NullIfWhiteSpace(reviewFocus),
            LanguageCode = string.IsNullOrWhiteSpace(language) ? "zh" : language.Trim(),
            MaxItems = maxItems,
            MaxChars = maxChars
        };

        var envelope = await assemblyService.GetChangeReviewContextAsync(request, cancellationToken);
        return ToJson(envelope);
    }

    [McpServerTool,
     Description(
         "Assemble UE editor task context from Build Identity, task description, and optional asset/type hints. " +
         "Returns stable project knowledge, MCP tool contracts, safety constraints, and a live UE MCP query checklist. " +
         "Write risk level requires Exact Wiki/Build match. OpenDeepWiki never proxies UE write operations.")]
    public static async Task<string> GetEditorTaskContext(
        IContext context,
        IContextAssemblyService assemblyService,
        McpServer mcpServer,
        IMcpUserResolver userResolver,
        IHttpContextAccessor httpContextAccessor,
        [Description("Task description for the editor agent.")] string taskDescription,
        [Description("Optional repository owner when not using scoped MCP URL.")] string? owner = null,
        [Description("Optional repository name when not using scoped MCP URL.")] string? repo = null,
        [Description("Branch name override. Build Identity branch takes precedence when set.")] string? branch = null,
        [Description("UE project identity from build manifest.")] string? projectId = null,
        [Description("Build/stream branch reported by the editor.")] string? buildBranch = null,
        [Description("Build changelist.")] string? buildChangelist = null,
        [Description("Build version string.")] string? buildVersion = null,
        [Description("Engine version.")] string? engineVersion = null,
        [Description("Target platform.")] string? targetPlatform = null,
        [Description("UE MCP contract version.")] string? ueMcpContractVersion = null,
        [Description("Current asset paths or type names, comma/newline separated.")] string? assetOrTypeHints = null,
        [Description("Operation risk: Query | Suggest | Write. Default: Query.")] string riskLevel = "Query",
        [Description("Required UE MCP tool names for compatibility check, comma separated.")] string? requiredMcpTools = null,
        [Description("Wiki language code. Default: zh.")] string language = "zh",
        [Description("Max context items. Default: 20.")] int maxItems = 20,
        [Description("Approx max characters. Default: 24000.")] int maxChars = 24000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskDescription))
        {
            return ToJson(new { error = true, message = "taskDescription is required" });
        }

        var scopeError = await ResolveRepositoryIdAsync(
            context, mcpServer, userResolver, httpContextAccessor, owner, repo, cancellationToken);
        if (scopeError.Error is not null)
        {
            return ToJson(new { error = true, message = scopeError.Error });
        }

        if (!Enum.TryParse<EditorOperationRiskLevel>(riskLevel, ignoreCase: true, out var risk))
        {
            risk = EditorOperationRiskLevel.Query;
        }

        var request = new EditorTaskContextRequest
        {
            RepositoryId = scopeError.RepositoryId!,
            BranchName = NullIfWhiteSpace(branch),
            TaskDescription = taskDescription.Trim(),
            BuildIdentity = new BuildIdentity
            {
                ProjectId = NullIfWhiteSpace(projectId),
                Branch = NullIfWhiteSpace(buildBranch) ?? NullIfWhiteSpace(branch),
                BuildChangelist = NullIfWhiteSpace(buildChangelist),
                BuildVersion = NullIfWhiteSpace(buildVersion),
                EngineVersion = NullIfWhiteSpace(engineVersion),
                TargetPlatform = NullIfWhiteSpace(targetPlatform),
                UeMcpContractVersion = NullIfWhiteSpace(ueMcpContractVersion)
            },
            CurrentAssetOrTypeHints = SplitPaths(assetOrTypeHints),
            RiskLevel = risk,
            LanguageCode = string.IsNullOrWhiteSpace(language) ? "zh" : language.Trim(),
            MaxItems = maxItems,
            MaxChars = maxChars,
            RequiredMcpToolNames = SplitPaths(requiredMcpTools)
        };

        var envelope = await assemblyService.GetEditorTaskContextAsync(request, cancellationToken);
        return ToJson(envelope);
    }

    [McpServerTool,
     Description(
         "Return a progressive module/domain overview for onboarding: responsibilities, entry points, " +
         "dependencies, risks, and a reading path with source citations.")]
    public static async Task<string> GetModuleOverview(
        IContext context,
        IContextAssemblyService assemblyService,
        McpServer mcpServer,
        IMcpUserResolver userResolver,
        IHttpContextAccessor httpContextAccessor,
        [Description("Module, plugin, domain name, or source path query.")] string moduleOrDomainQuery,
        [Description("Optional repository owner when not using scoped MCP URL.")] string? owner = null,
        [Description("Optional repository name when not using scoped MCP URL.")] string? repo = null,
        [Description("Branch name. Defaults to repository default branch.")] string? branch = null,
        [Description("Optional target changelist for snapshot resolution.")] string? targetChangelist = null,
        [Description("Wiki language code. Default: zh.")] string language = "zh",
        [Description("Max context items. Default: 15.")] int maxItems = 15,
        [Description("Approx max characters. Default: 20000.")] int maxChars = 20000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleOrDomainQuery))
        {
            return ToJson(new { error = true, message = "moduleOrDomainQuery is required" });
        }

        var scopeError = await ResolveRepositoryIdAsync(
            context, mcpServer, userResolver, httpContextAccessor, owner, repo, cancellationToken);
        if (scopeError.Error is not null)
        {
            return ToJson(new { error = true, message = scopeError.Error });
        }

        var request = new ModuleOverviewRequest
        {
            RepositoryId = scopeError.RepositoryId!,
            BranchName = NullIfWhiteSpace(branch),
            ModuleOrDomainQuery = moduleOrDomainQuery.Trim(),
            TargetChangelist = NullIfWhiteSpace(targetChangelist),
            LanguageCode = string.IsNullOrWhiteSpace(language) ? "zh" : language.Trim(),
            MaxItems = maxItems,
            MaxChars = maxChars
        };

        var envelope = await assemblyService.GetModuleOverviewAsync(request, cancellationToken);
        return ToJson(envelope);
    }

    private static async Task<(string? RepositoryId, string? Error)> ResolveRepositoryIdAsync(
        IContext context,
        McpServer mcpServer,
        IMcpUserResolver userResolver,
        IHttpContextAccessor httpContextAccessor,
        string? owner,
        string? repo,
        CancellationToken cancellationToken)
    {
        var scope = McpRepositoryScopeAccessor.GetScope(mcpServer);
        var resolvedOwner = NullIfWhiteSpace(scope.Owner) ?? NullIfWhiteSpace(owner);
        var resolvedRepo = NullIfWhiteSpace(scope.Repo) ?? NullIfWhiteSpace(repo);

        if (string.IsNullOrWhiteSpace(resolvedOwner) || string.IsNullOrWhiteSpace(resolvedRepo))
        {
            return (null, "Repository scope is required. Call via /api/mcp/{owner}/{repo} or pass owner/repo.");
        }

        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.OrgName == resolvedOwner
                     && r.RepoName == resolvedRepo
                     && !r.IsDeleted,
                cancellationToken);

        if (repository is null)
        {
            return (null, $"Repository {resolvedOwner}/{resolvedRepo} not found");
        }

        var userId = await ResolveMcpUserIdAsync(userResolver, httpContextAccessor.HttpContext?.User);
        var canAccess = await userResolver.CanAccessRepositoryAsync(
            userId ?? string.Empty,
            resolvedOwner,
            resolvedRepo);

        if (!canAccess)
        {
            return (null, $"Access denied to repository {resolvedOwner}/{resolvedRepo}");
        }

        return (repository.Id, null);
    }

    private static async Task<string?> ResolveMcpUserIdAsync(
        IMcpUserResolver userResolver,
        ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var user = await userResolver.ResolveUserAsync(principal);
        return user?.UserId;
    }

    private static IReadOnlyList<string> SplitPaths(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<MovedFilePair> ParseMovedFiles(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var pairs = new List<MovedFilePair>();
        foreach (var part in SplitPaths(raw))
        {
            var separators = new[] { "=>", "->", "|" };
            string? oldPath = null;
            string? newPath = null;
            foreach (var sep in separators)
            {
                var idx = part.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0)
                {
                    oldPath = part[..idx].Trim();
                    newPath = part[(idx + sep.Length)..].Trim();
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(oldPath) && !string.IsNullOrWhiteSpace(newPath))
            {
                pairs.Add(new MovedFilePair { OldPath = oldPath, NewPath = newPath });
            }
        }

        return pairs;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToJson(object value)
        => JsonSerializer.Serialize(value, JsonOptions);
}
