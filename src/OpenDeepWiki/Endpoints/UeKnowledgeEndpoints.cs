using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.UeKnowledge;

namespace OpenDeepWiki.Endpoints;

public static class UeKnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapUeKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/repositories/{repositoryId}/ue-knowledge")
            .WithTags("UE Knowledge Package");

        group.MapPost("/ingest", IngestAsync)
            .WithName("IngestUeKnowledgePackage")
            .WithSummary("摄取并验证 UE Knowledge Package")
            .WithDescription("验证 manifest/schema/digest/project identity，写入事实索引。导出器不在 OpenDeepWiki 仓库内。");

        group.MapGet("/", ListAsync)
            .WithName("ListUeKnowledgePackages")
            .WithSummary("列出仓库 UE Knowledge Package");

        group.MapGet("/current", GetCurrentAsync)
            .WithName("GetCurrentUeKnowledgePackage")
            .WithSummary("获取分支当前 UE Knowledge Package 摘要");

        group.MapGet("/current/facts", GetCurrentFactsAsync)
            .WithName("GetCurrentUeKnowledgeFacts")
            .WithSummary("获取分支当前事实索引");

        group.MapGet("/packages/{packageId}/facts", GetPackageFactsAsync)
            .WithName("GetUeKnowledgePackageFacts")
            .WithSummary("按包 ID 获取事实索引");

        group.MapPost("/diff", DiffAsync)
            .WithName("DiffUeKnowledgePackages")
            .WithSummary("当前包与指定包的语义 diff");

        group.MapPost("/mcp-compatibility", CheckMcpCompatibilityAsync)
            .WithName("CheckUeMcpContractCompatibility")
            .WithSummary("检查 Build CL 与 MCP 工具契约兼容性");

        return app;
    }

    private static async Task<IResult> IngestAsync(
        string repositoryId,
        [FromBody] IngestUeKnowledgePackageRequest request,
        [FromServices] IUeKnowledgePackageService service,
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
            var result = await service.IngestAsync(repositoryId, request, cancellationToken);
            return Results.Ok(result);
        }
        catch (UeKnowledgeException ex)
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> ListAsync(
        string repositoryId,
        [FromQuery] string? branchId,
        [FromServices] IUeKnowledgePackageService service,
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

        var items = await service.ListAsync(repositoryId, branchId, cancellationToken);
        return Results.Ok(items);
    }

    private static async Task<IResult> GetCurrentAsync(
        string repositoryId,
        [FromQuery] string branchId,
        [FromServices] IUeKnowledgePackageService service,
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

        if (string.IsNullOrWhiteSpace(branchId))
        {
            return Results.BadRequest(new { success = false, code = "BRANCH_REQUIRED", message = "branchId 必填" });
        }

        var item = await service.GetCurrentAsync(repositoryId, branchId, cancellationToken);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> GetCurrentFactsAsync(
        string repositoryId,
        [FromQuery] string branchId,
        [FromServices] IUeKnowledgePackageService service,
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

        if (string.IsNullOrWhiteSpace(branchId))
        {
            return Results.BadRequest(new { success = false, code = "BRANCH_REQUIRED", message = "branchId 必填" });
        }

        var facts = await service.GetCurrentFactIndexAsync(repositoryId, branchId, cancellationToken);
        return facts is null ? Results.NotFound() : Results.Ok(facts);
    }

    private static async Task<IResult> GetPackageFactsAsync(
        string repositoryId,
        string packageId,
        [FromServices] IUeKnowledgePackageService service,
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

        var facts = await service.GetFactIndexAsync(repositoryId, packageId, cancellationToken);
        return facts is null ? Results.NotFound() : Results.Ok(facts);
    }

    private static async Task<IResult> DiffAsync(
        string repositoryId,
        [FromBody] UeKnowledgeDiffRequest request,
        [FromServices] IUeKnowledgePackageService service,
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

        if (string.IsNullOrWhiteSpace(request.BranchId) || string.IsNullOrWhiteSpace(request.OtherPackageId))
        {
            return Results.BadRequest(new
            {
                success = false,
                code = "INVALID_REQUEST",
                message = "branchId 与 otherPackageId 必填"
            });
        }

        var diff = await service.DiffCurrentWithAsync(
            repositoryId,
            request.BranchId,
            request.OtherPackageId,
            cancellationToken);

        return diff is null ? Results.NotFound() : Results.Ok(diff);
    }

    private static async Task<IResult> CheckMcpCompatibilityAsync(
        string repositoryId,
        [FromBody] UeMcpCompatibilityRequest request,
        [FromServices] IUeKnowledgePackageService service,
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

        if (string.IsNullOrWhiteSpace(request.BranchId))
        {
            return Results.BadRequest(new { success = false, code = "BRANCH_REQUIRED", message = "branchId 必填" });
        }

        var result = await service.CheckMcpCompatibilityAsync(
            repositoryId,
            request.BranchId,
            request.BuildChangelist ?? string.Empty,
            request.RequiredToolNames,
            cancellationToken);

        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static IResult ToError(UeKnowledgeException ex)
    {
        var status = ex.ErrorCode switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "VALIDATION_FAILED" or "INVALID_PACKAGE" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(
            new
            {
                success = false,
                code = ex.ErrorCode,
                message = ex.Message,
                errors = ex.Errors,
                warnings = ex.Warnings
            },
            statusCode: status);
    }
}

public sealed class UeKnowledgeDiffRequest
{
    public string BranchId { get; set; } = string.Empty;

    public string OtherPackageId { get; set; } = string.Empty;
}

public sealed class UeMcpCompatibilityRequest
{
    public string BranchId { get; set; } = string.Empty;

    public string? BuildChangelist { get; set; }

    public List<string>? RequiredToolNames { get; set; }
}
