using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Moderation.Services;
using Ratable.Shared.Auth;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Moderation.Endpoints;

public sealed record ReportBody(string? TargetType, string? TargetId, string? Reason, string? Details);
public sealed record LookupBody(string? TargetType, string? TargetId, string? Reason);
public sealed record ReasonBody(string? Reason);
public sealed record ResolveBody(string? Action, string? Reason);
public sealed record DisputeBody(string? Reason);
public sealed record ErrorBody(string Error);
public sealed record ReportedResponse(string Id);
public sealed record LookupResponse(string ContentKind, string ContentId, string UserId, string? DisplayName, DateTime AccountCreatedAt, string AccountStatus);

public sealed record FlagDto(bool Flagged, double Confidence);
public sealed record PrescreenDto(FlagDto Abuse, FlagDto PersonalData, FlagDto Spam, FlagDto SuspectedFake, FlagDto IdentifyingDetails, string? Model);

/// <summary>One item in the review/comment queue. It shows the content and why it is here; it never shows who wrote it.</summary>
public sealed record CaseDto(
    string Id, string Kind, string ContentId, int Revision, string Route, List<string> Reasons, string AiStatus, PrescreenDto? Prescreen,
    bool AuthorPhoneVerified, bool HasVerifiedProof, DateTime CreatedAt, ModerationContentView? Content,
    string State, string? Decision, string? DecisionReason);
public sealed record CasePage(List<CaseDto> Items, long Total);

public sealed record DisputeDto(string Id, string ReviewId, string Reason, DateTime CreatedAt, ModerationContentView? Review);

public sealed record AuditEntryDto(string Id, string Actor, string Action, string TargetType, string TargetId, string Reason, string? Model, DateTime At);
public sealed record AuditPage(List<AuditEntryDto> Items, long Total, int Page, int PageSize);

public static class ModerationEndpoints
{
    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/reports", Report).WithTags("Moderation").RequireAuthorization(PolicyNames.SignedIn).Produces<ReportedResponse>(StatusCodes.Status201Created);
        app.MapPost("/api/reviews/{id}/disputes", RaiseDispute).WithTags("Moderation").RequireAuthorization(PolicyNames.BusinessOwner).Produces<ReportedResponse>(StatusCodes.Status201Created);
        app.MapPost("/api/admin/anonymous-author-lookups", Lookup).WithTags("Admin").RequireAuthorization(PolicyNames.Admin).Produces<LookupResponse>();
        app.MapGet("/api/admin/audit", Audit).WithTags("Admin").RequireAuthorization(PolicyNames.Admin).Produces<AuditPage>();

        var mod = app.MapGroup("/api/moderation").WithTags("Moderation").RequireAuthorization(PolicyNames.Moderator);
        mod.MapGet("/overview", GetOverview).Produces<Overview>();
        mod.MapGet("/cases", ListCases).Produces<CasePage>();
        mod.MapPost("/cases/{id}/publish", PublishCase).Produces<CaseDto>();
        mod.MapPost("/cases/{id}/hide", HideCase).Produces<CaseDto>();
        mod.MapGet("/reports", ListReports).Produces<List<ReportGroup>>();
        mod.MapPost("/reports/{id}/resolve", ResolveReport).Produces(StatusCodes.Status204NoContent);
        mod.MapGet("/disputes", ListDisputes).Produces<List<DisputeDto>>();
        mod.MapPost("/disputes/{id}/uphold", UpholdDispute).Produces(StatusCodes.Status204NoContent);
        mod.MapPost("/disputes/{id}/dismiss", DismissDispute).Produces(StatusCodes.Status204NoContent);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ---- readers ----

    private static async Task<IResult> Report(ReportBody body, ClaimsPrincipal user, ReportService service, CancellationToken ct)
    {
        var result = await service.FileAsync(UserId(user), body.TargetType, body.TargetId, body.Reason, body.Details, ct);
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

    private static async Task<IResult> RaiseDispute(string id, DisputeBody body, ClaimsPrincipal user, DisputeService service, CancellationToken ct)
    {
        var r = await service.RaiseAsync(UserId(user), id, body.Reason, ct);
        return r.Failure switch
        {
            DisputeFailure.None => Results.Created("/api/moderation/disputes", new ReportedResponse(r.Dispute!.Id)),
            DisputeFailure.InvalidReason => Results.BadRequest(new ErrorBody("invalid_reason")),
            DisputeFailure.OwnReview => Results.BadRequest(new ErrorBody("own_review")),
            DisputeFailure.NotOwner => Results.Json(new ErrorBody("not_owner"), statusCode: StatusCodes.Status403Forbidden),
            DisputeFailure.AlreadyOpen => Results.Conflict(new ErrorBody("dispute_already_open")),
            _ => Results.NotFound(new ErrorBody("not_found")),
        };
    }

    // ---- the queue ----

    private static async Task<IResult> GetOverview(OverviewService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct));

    private static async Task<IResult> ListCases(string? kind, int? skip, int? limit, ModerationService service, CancellationToken ct)
    {
        ContentKind? k = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<ContentKind>(kind, ignoreCase: true, out var parsed)) return Results.BadRequest(new ErrorBody("invalid_kind"));
            k = parsed;
        }
        var (items, total) = await service.QueueAsync(k, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 20, 1, 100), ct);
        return Results.Ok(new CasePage([.. items.Select(i => ToDto(i.Case, i.Content))], total));
    }

    private static async Task<IResult> PublishCase(string id, ClaimsPrincipal user, ModerationService service, CancellationToken ct) =>
        ToResult(await service.DecideAsync(UserId(user), id, publish: true, reason: null, ct));

    private static async Task<IResult> HideCase(string id, ReasonBody body, ClaimsPrincipal user, ModerationService service, CancellationToken ct) =>
        ToResult(await service.DecideAsync(UserId(user), id, publish: false, body.Reason, ct));

    private static IResult ToResult(CaseResult r) => r.Failure switch
    {
        CaseFailure.None => Results.Ok(ToDto(r.Case!, null)),
        CaseFailure.NotFound => Results.NotFound(new ErrorBody("case_not_found")),
        CaseFailure.WrongState => Results.Conflict(new ErrorBody("wrong_state")),
        CaseFailure.ContentGone => Results.Conflict(new ErrorBody("content_changed")),
        _ => Results.BadRequest(new ErrorBody("reason_required")),
    };

    private static CaseDto ToDto(ModerationCase c, ModerationContentView? content) => new(
        c.Id, c.Kind.ToString(), c.ContentId, c.RevisionNumber, c.Route.ToString(), c.Reasons, c.AiStatus,
        c.Prescreen is null ? null : new PrescreenDto(F(c.Prescreen.Abuse), F(c.Prescreen.PersonalData), F(c.Prescreen.Spam), F(c.Prescreen.SuspectedFake), F(c.Prescreen.IdentifyingDetails), c.Prescreen.Model),
        c.AuthorPhoneVerified, c.HasVerifiedProof, c.CreatedAt, content, c.State.ToString(), c.Decision?.ToString(), c.DecisionReason);

    private static FlagDto F(FlagResult f) => new(f.Flagged, f.Confidence);

    // ---- reports and disputes ----

    private static async Task<IResult> ListReports(ReportQueueService service, CancellationToken ct) => Results.Ok(await service.QueueAsync(ct));

    private static async Task<IResult> ResolveReport(string id, ResolveBody body, ClaimsPrincipal user, ReportQueueService service, CancellationToken ct)
    {
        var r = await service.ResolveAsync(UserId(user), id, body.Action, body.Reason, ct);
        return r.Failure switch
        {
            ResolveFailure.None => Results.NoContent(),
            ResolveFailure.NotFound => Results.NotFound(new ErrorBody("report_not_found")),
            ResolveFailure.WrongState => Results.Conflict(new ErrorBody("wrong_state")),
            ResolveFailure.ReasonRequired => Results.BadRequest(new ErrorBody("reason_required")),
            _ => Results.BadRequest(new ErrorBody("invalid_action")),
        };
    }

    private static async Task<IResult> ListDisputes(DisputeService service, CancellationToken ct) =>
        Results.Ok((await service.QueueAsync(ct)).Select(x => new DisputeDto(x.Dispute.Id, x.Dispute.ReviewId, x.Dispute.Reason, x.Dispute.CreatedAt, x.Review)));

    private static async Task<IResult> UpholdDispute(string id, ReasonBody body, ClaimsPrincipal user, DisputeService service, CancellationToken ct) =>
        ToDisputeResult(await service.DecideAsync(UserId(user), id, uphold: true, body.Reason, ct));

    private static async Task<IResult> DismissDispute(string id, ReasonBody body, ClaimsPrincipal user, DisputeService service, CancellationToken ct) =>
        ToDisputeResult(await service.DecideAsync(UserId(user), id, uphold: false, body.Reason, ct));

    private static IResult ToDisputeResult(DisputeResult r) => r.Failure switch
    {
        DisputeFailure.None => Results.NoContent(),
        DisputeFailure.NotFound => Results.NotFound(new ErrorBody("dispute_not_found")),
        DisputeFailure.WrongState => Results.Conflict(new ErrorBody("wrong_state")),
        _ => Results.BadRequest(new ErrorBody("reason_required")),
    };

    // ---- admin ----

    private static async Task<IResult> Lookup(LookupBody body, ClaimsPrincipal admin, AnonymousAuthorLookupService service, CancellationToken ct)
    {
        var result = await service.LookUpAsync(UserId(admin), body.TargetType, body.TargetId, body.Reason, ct);
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

    private static async Task<IResult> Audit(
        string? action, string? targetType, string? actor, DateTime? from, DateTime? to, int? page, int? pageSize, AuditLogRepository repository, CancellationToken ct)
    {
        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        var p = Math.Max(1, page ?? 1);
        var (items, total) = await repository.SearchAsync(action, targetType, actor, from, to, (p - 1) * size, size, ct);
        return Results.Ok(new AuditPage([.. items.Select(e => new AuditEntryDto(e.Id, e.Actor, e.Action, e.TargetType, e.TargetId, e.Reason, e.Model, e.At))], total, p, size));
    }
}
