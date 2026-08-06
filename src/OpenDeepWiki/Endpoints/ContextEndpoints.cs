using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Context;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// HTTP 入口，与 MCP 共用 IContextAssemblyService，便于 Web/Chat 复用。
/// 读取前执行与 WP1/WP3 一致的仓库授权。
/// </summary>
public static class ContextEndpoints
{
    public static void MapContextEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/repositories/{repositoryId}/context")
            .WithTags("Context");

        group.MapPost("/change-review", GetChangeReviewContextAsync)
            .WithName("GetChangeReviewContext")
            .WithSummary("Assemble AI code-review context envelope");

        group.MapPost("/editor-task", GetEditorTaskContextAsync)
            .WithName("GetEditorTaskContext")
            .WithSummary("Assemble UE editor task context envelope");

        group.MapPost("/module-overview", GetModuleOverviewAsync)
            .WithName("GetModuleOverview")
            .WithSummary("Assemble progressive module/domain overview envelope");
    }

    private static async Task<IResult> GetChangeReviewContextAsync(
        string repositoryId,
        [FromBody] ChangeReviewContextBody body,
        IContextAssemblyService assemblyService,
        IContext context,
        IUserContext userContext,
        CancellationToken cancellationToken)
    {
        var auth = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        if (auth is not null)
        {
            return auth;
        }

        var request = new ChangeReviewContextRequest
        {
            RepositoryId = repositoryId,
            BranchName = body.BranchName,
            BaseChangelist = body.BaseChangelist,
            TargetChangelist = body.TargetChangelist,
            ChangedFiles = body.ChangedFiles ?? [],
            DeletedFiles = body.DeletedFiles ?? [],
            MovedFiles = body.MovedFiles ?? [],
            ReviewFocus = body.ReviewFocus,
            LanguageCode = string.IsNullOrWhiteSpace(body.LanguageCode) ? "zh" : body.LanguageCode,
            MaxItems = body.MaxItems ?? 20,
            MaxChars = body.MaxChars ?? 24_000
        };

        var envelope = await assemblyService.GetChangeReviewContextAsync(request, cancellationToken);
        return Results.Ok(envelope);
    }

    private static async Task<IResult> GetEditorTaskContextAsync(
        string repositoryId,
        [FromBody] EditorTaskContextBody body,
        IContextAssemblyService assemblyService,
        IContext context,
        IUserContext userContext,
        CancellationToken cancellationToken)
    {
        var auth = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(body.TaskDescription))
        {
            return Results.BadRequest(new
            {
                success = false,
                code = "TASK_REQUIRED",
                message = "taskDescription 必填"
            });
        }

        var request = new EditorTaskContextRequest
        {
            RepositoryId = repositoryId,
            BranchName = body.BranchName,
            TaskDescription = body.TaskDescription,
            BuildIdentity = body.BuildIdentity,
            CurrentAssetOrTypeHints = body.CurrentAssetOrTypeHints ?? [],
            RiskLevel = body.RiskLevel ?? EditorOperationRiskLevel.Query,
            LanguageCode = string.IsNullOrWhiteSpace(body.LanguageCode) ? "zh" : body.LanguageCode,
            MaxItems = body.MaxItems ?? 20,
            MaxChars = body.MaxChars ?? 24_000,
            RequiredMcpToolNames = body.RequiredMcpToolNames
        };

        var envelope = await assemblyService.GetEditorTaskContextAsync(request, cancellationToken);
        return Results.Ok(envelope);
    }

    private static async Task<IResult> GetModuleOverviewAsync(
        string repositoryId,
        [FromBody] ModuleOverviewBody body,
        IContextAssemblyService assemblyService,
        IContext context,
        IUserContext userContext,
        CancellationToken cancellationToken)
    {
        var auth = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(body.ModuleOrDomainQuery))
        {
            return Results.BadRequest(new
            {
                success = false,
                code = "MODULE_QUERY_REQUIRED",
                message = "moduleOrDomainQuery 必填"
            });
        }

        var request = new ModuleOverviewRequest
        {
            RepositoryId = repositoryId,
            BranchName = body.BranchName,
            ModuleOrDomainQuery = body.ModuleOrDomainQuery,
            TargetChangelist = body.TargetChangelist,
            LanguageCode = string.IsNullOrWhiteSpace(body.LanguageCode) ? "zh" : body.LanguageCode,
            MaxItems = body.MaxItems ?? 15,
            MaxChars = body.MaxChars ?? 20_000
        };

        var envelope = await assemblyService.GetModuleOverviewAsync(request, cancellationToken);
        return Results.Ok(envelope);
    }
}

/// <summary>
/// HTTP body：repositoryId 仅来自路径，不在 body 中要求。
/// </summary>
public sealed class ChangeReviewContextBody
{
    public string? BranchName { get; init; }

    public string? BaseChangelist { get; init; }

    public string? TargetChangelist { get; init; }

    public List<string>? ChangedFiles { get; init; }

    public List<string>? DeletedFiles { get; init; }

    public List<MovedFilePair>? MovedFiles { get; init; }

    public string? ReviewFocus { get; init; }

    public string? LanguageCode { get; init; }

    public int? MaxItems { get; init; }

    public int? MaxChars { get; init; }
}

public sealed class EditorTaskContextBody
{
    public string? BranchName { get; init; }

    public string? TaskDescription { get; init; }

    public BuildIdentity? BuildIdentity { get; init; }

    public List<string>? CurrentAssetOrTypeHints { get; init; }

    public EditorOperationRiskLevel? RiskLevel { get; init; }

    public string? LanguageCode { get; init; }

    public int? MaxItems { get; init; }

    public int? MaxChars { get; init; }

    public List<string>? RequiredMcpToolNames { get; init; }
}

public sealed class ModuleOverviewBody
{
    public string? BranchName { get; init; }

    public string? ModuleOrDomainQuery { get; init; }

    public string? TargetChangelist { get; init; }

    public string? LanguageCode { get; init; }

    public int? MaxItems { get; init; }

    public int? MaxChars { get; init; }
}
