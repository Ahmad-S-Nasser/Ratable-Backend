using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Moderation.Services;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Moderation.Endpoints;

public sealed record ReportBody(string? TargetType, string? TargetId, string? Reason, string? Details);
public sealed record LookupBody(string? TargetType, string? TargetId, string? Reason);
public sealed record ErrorBody(string Error);
public sealed record ReportedResponse(string Id);
public sealed record LookupResponse(string ContentKind, string ContentId, string UserId, string? DisplayName, DateTime AccountCreatedAt, string AccountStatus);

public static class ModerationEndpoints
{
    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/reports", Report).WithTags("Moderation").RequireAuthorization(PolicyNames.SignedIn);
        app.MapPost("/api/admin/anonymous-author-lookups", Lookup).WithTags("Admin").RequireAuthorization(PolicyNames.Admin);
        return app;
    }

    private static async Task<IResult> Report(ReportBody body, ClaimsPrincipal user, ReportService service, CancellationToken ct)
    {
        var result = await service.FileAsync(user.FindFirstValue(ClaimTypes.NameIdentifier)!, body.TargetType, body.TargetId, body.Reason, body.Details, ct);
        return result.Failure switch
        {
            ReportFailure.None => Results.Created("/api/reports", new ReportedResponse(result.Report!.Id)),
            ReportFailure.InvalidTarget => Results.BadRequest(new ErrorBody("invalid_target")),
            ReportFailure.InvalidReason => Results.BadRequest(new ErrorBody("invalid_reason")),
            ReportFailure.NotFound => Results.NotFound(new ErrorBody("not_found")),
            ReportFailure.OwnContent => Results.BadRequest(new ErrorBody("own_content")),
            ReportFailure.AlreadyReported => Results.Conflict(new ErrorBody("already_reported")),
            _ => Results.Json(new ErrorBody("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests),
        };
    }

    private static async Task<IResult> Lookup(LookupBody body, ClaimsPrincipal admin, AnonymousAuthorLookupService service, CancellationToken ct)
    {
        var result = await service.LookUpAsync(admin.FindFirstValue(ClaimTypes.NameIdentifier)!, body.TargetType, body.TargetId, body.Reason, ct);
        return result.Failure switch
        {
            LookupFailure.None when result.Account is { } a => Results.Ok(new LookupResponse(result.ContentKind!, result.ContentId!, a.UserId, a.DisplayName, a.CreatedAt, a.Status)),
            LookupFailure.None => Results.NotFound(new ErrorBody("account_not_found")),
            LookupFailure.InvalidTarget => Results.BadRequest(new ErrorBody("invalid_target")),
            LookupFailure.ReasonRequired => Results.BadRequest(new ErrorBody("reason_required")),
            LookupFailure.NotAnonymous => Results.BadRequest(new ErrorBody("not_anonymous")),
            _ => Results.NotFound(new ErrorBody("not_found")),
        };
    }
}
