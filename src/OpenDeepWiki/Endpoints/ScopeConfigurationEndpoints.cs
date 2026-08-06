using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories.Scope;

namespace OpenDeepWiki.Endpoints;

public static class ScopeConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapScopeConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/repositories/{repositoryId}/scope")
            .WithTags("仓库 Scope");

        group.MapGet("/", GetScopeAsync)
            .WithName("GetRepositoryScopeConfiguration")
            .WithSummary("获取仓库 Scope 配置")
            .WithDescription("无配置时返回兼容行为与迁移提示，不回显凭据或宿主绝对路径。");

        group.MapPost("/preview", PreviewScopeAsync)
            .WithName("PreviewRepositoryScopeConfiguration")
            .WithSummary("预览 Scope 配置变更影响");

        group.MapPut("/", UpdateScopeAsync)
            .WithName("UpdateRepositoryScopeConfiguration")
            .WithSummary("保存版本化 Scope 配置");

        return app;
    }

    private static async Task<IResult> GetScopeAsync(
        string repositoryId,
        [FromServices] IScopeConfigurationService scopeService,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        CancellationToken cancellationToken)
    {
        var auth = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        // 读取：登录用户可读自己仓库；Admin 可读。为简化，复用 mutation 授权（owner/admin）。
        if (auth is not null)
        {
            return auth;
        }

        try
        {
            var response = await scopeService.GetAsync(repositoryId, cancellationToken);
            return Results.Ok(response);
        }
        catch (ScopeConfigurationException ex)
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> PreviewScopeAsync(
        string repositoryId,
        [FromBody] ScopeConfigurationDocument document,
        [FromServices] IScopeConfigurationService scopeService,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        CancellationToken cancellationToken)
    {
        var auth = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        if (auth is not null)
        {
            return auth;
        }

        try
        {
            var response = await scopeService.PreviewAsync(repositoryId, document, cancellationToken);
            return response.IsValid
                ? Results.Ok(response)
                : Results.BadRequest(response);
        }
        catch (ScopeConfigurationException ex)
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> UpdateScopeAsync(
        string repositoryId,
        [FromBody] UpdateScopeRequest request,
        [FromServices] IScopeConfigurationService scopeService,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        CancellationToken cancellationToken)
    {
        var auth = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        if (auth is not null)
        {
            return auth;
        }

        if (request.Configuration is null)
        {
            return Results.BadRequest(new ScopeErrorResponse(false, "INVALID_BODY", "configuration is required"));
        }

        try
        {
            var response = await scopeService.UpdateAsync(
                repositoryId,
                request.Configuration,
                userContext.UserId,
                request.ChangeSummary,
                cancellationToken);
            return Results.Ok(response);
        }
        catch (ScopeConfigurationException ex)
        {
            return ToError(ex);
        }
    }

    private static IResult ToError(ScopeConfigurationException ex)
    {
        var status = ex.ErrorCode switch
        {
            "REPOSITORY_NOT_FOUND" => StatusCodes.Status404NotFound,
            "REPOSITORY_LOCKED" => StatusCodes.Status409Conflict,
            "SCOPE_VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(
            new ScopeErrorResponse(false, ex.ErrorCode, ex.Message),
            statusCode: status);
    }

    public sealed record UpdateScopeRequest(
        ScopeConfigurationDocument Configuration,
        string? ChangeSummary);

    public sealed record ScopeErrorResponse(
        bool Success,
        string ErrorCode,
        string Message);
}
