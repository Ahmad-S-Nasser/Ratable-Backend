using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ratable.Modules.Businesses.Models;
using Ratable.Modules.Businesses.Services;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Businesses.Endpoints;

public sealed record ErrorBody(string Error);
public sealed record ReplyBody(string? Text);
public sealed record ReasonBody(string? Reason);

/// <summary>The claimant's view of their own claim.</summary>
public sealed record OwnClaim(string Id, string EntityId, string Status, string Role, bool HasDocument, string? DocumentType, string? DecisionReason, DateTime CreatedAt);

/// <summary>A moderator's view of a claim waiting for a decision. Never holds the document, only that it exists.</summary>
public sealed record ClaimForReview(
    string Id, string EntityId, string CompanyEmail, string EmailDomain, string Role, string? Website, string? Note, string? DocumentType, bool FreeEmail, bool DomainMatchesPlace, DateTime CreatedAt);

public sealed record PlanDto(string Id, IReadOnlyList<string> Features, decimal? PriceEgpMonthly, bool ShowsContact);

public static class BusinessEndpoints
{
    public static IEndpointRouteBuilder MapBusinessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plans", Plans).WithTags("Businesses").Produces<List<PlanDto>>();

        app.MapPost("/api/entities/{entityId}/claims", CreateClaim).WithTags("Businesses").RequireAuthorization(PolicyNames.SignedIn).Produces<OwnClaim>(201).Produces<ErrorBody>(400).Produces<ErrorBody>(403).Produces<ErrorBody>(404).Produces<ErrorBody>(409).Produces<ErrorBody>(429);
        app.MapPut("/api/me/claims/{id}/document", UploadDocument).WithTags("Businesses").RequireAuthorization(PolicyNames.SignedIn).Produces<OwnClaim>().Produces<ErrorBody>(400).Produces<ErrorBody>(404);
        app.MapGet("/api/me/claims", MyClaims).WithTags("Businesses").RequireAuthorization(PolicyNames.SignedIn).Produces<List<OwnClaim>>();

        var reply = app.MapGroup("/api/reviews/{reviewId}/owner-reply").WithTags("Businesses").RequireAuthorization(PolicyNames.BusinessOwner);
        reply.MapPost("", CreateReply).Produces<OwnerReplyDto>(201).Produces<ErrorBody>(400).Produces<ErrorBody>(403).Produces<ErrorBody>(404).Produces<ErrorBody>(409);
        reply.MapPut("", EditReply).Produces<OwnerReplyDto>().Produces<ErrorBody>(400).Produces<ErrorBody>(404);
        reply.MapDelete("", DeleteReply).Produces(204).Produces<ErrorBody>(404);

        var biz = app.MapGroup("/api/business").WithTags("Businesses").RequireAuthorization(PolicyNames.BusinessOwner);
        biz.MapGet("/places", Places).Produces<List<DashboardPlace>>();
        biz.MapGet("/places/{entityId}/dashboard", GetDashboard).Produces<Dashboard>().Produces(404);
        biz.MapPost("/places/{entityId}/seen", MarkSeen).Produces(204).Produces(404);

        var mod = app.MapGroup("/api/moderation").WithTags("Moderation").RequireAuthorization(PolicyNames.Moderator);
        mod.MapGet("/claims", ClaimQueue).Produces<List<ClaimForReview>>();
        mod.MapGet("/claims/{id}/document", ClaimDocument).Produces(200).Produces(404);
        mod.MapPost("/claims/{id}/approve", Approve).Produces<OwnClaim>().Produces<ErrorBody>(404).Produces<ErrorBody>(409);
        mod.MapPost("/claims/{id}/reject", Reject).Produces<OwnClaim>().Produces<ErrorBody>(400).Produces<ErrorBody>(404).Produces<ErrorBody>(409);
        mod.MapPost("/ownerships/{entityId}/revoke", Revoke).Produces(204).Produces<ErrorBody>(400).Produces<ErrorBody>(404);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static OwnClaim ToOwn(BusinessClaim c) => new(c.Id, c.EntityId, c.Status.ToString(), c.Role, c.DocumentUploadedAt is not null, c.DocumentType, c.DecisionReason, c.CreatedAt);

    private static IResult Failed(ClaimFailure f) => f switch
    {
        ClaimFailure.NotFound or ClaimFailure.EntityNotFound => Results.NotFound(new ErrorBody("not_found")),
        ClaimFailure.PhoneNotVerified => Results.Json(new ErrorBody("phone_not_verified"), statusCode: StatusCodes.Status403Forbidden),
        ClaimFailure.AlreadyOwned => Results.Conflict(new ErrorBody("already_owned")),
        ClaimFailure.AlreadyOwnerHere => Results.Conflict(new ErrorBody("already_owner")),
        ClaimFailure.ClaimOpen => Results.Conflict(new ErrorBody("claim_open")),
        ClaimFailure.OwnerConflict => Results.Conflict(new ErrorBody("already_owned")),
        ClaimFailure.WrongState => Results.Conflict(new ErrorBody("wrong_state")),
        ClaimFailure.TooManyClaims => Results.Json(new ErrorBody("too_many_claims"), statusCode: StatusCodes.Status429TooManyRequests),
        ClaimFailure.InvalidEmail => Results.BadRequest(new ErrorBody("invalid_email")),
        ClaimFailure.InvalidRole => Results.BadRequest(new ErrorBody("invalid_role")),
        ClaimFailure.InvalidDocumentType => Results.BadRequest(new ErrorBody("invalid_document_type")),
        ClaimFailure.InvalidDocument or ClaimFailure.DocumentTooLarge => Results.BadRequest(new ErrorBody(f == ClaimFailure.DocumentTooLarge ? "document_too_large" : "invalid_document")),
        ClaimFailure.ReasonRequired => Results.BadRequest(new ErrorBody("reason_required")),
        _ => Results.NotFound(new ErrorBody("not_found")),
    };

    private static IResult Plans(IOptions<BusinessesOptions> options) =>
        Results.Ok(options.Value.Plans.Select(p => new PlanDto(p.Id, p.Features, p.PriceEgpMonthly, p.ShowsContact)).ToList());

    private static async Task<IResult> CreateClaim(string entityId, ClaimInput body, ClaimsPrincipal user, ClaimService service, CancellationToken ct)
    {
        var r = await service.CreateAsync(UserId(user), entityId, body, ct);
        return r.Ok ? Results.Created("/api/me/claims", ToOwn(r.Claim!)) : Failed(r.Failure);
    }

    private static async Task<IResult> UploadDocument(string id, string? type, HttpRequest request, ClaimsPrincipal user, ClaimService service, IOptions<BusinessesOptions> options, CancellationToken ct)
    {
        var max = options.Value.MaxDocumentBytes;
        if (request.ContentLength > max) return Failed(ClaimFailure.DocumentTooLarge);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return Failed(ClaimFailure.DocumentTooLarge);
            buffer.Write(chunk, 0, read);
        }
        var r = await service.UploadDocumentAsync(UserId(user), id, type, buffer.ToArray(), ct);
        return r.Ok ? Results.Ok(ToOwn(r.Claim!)) : Failed(r.Failure);
    }

    private static async Task<IResult> MyClaims(ClaimsPrincipal user, ClaimService service, CancellationToken ct) =>
        Results.Ok((await service.MineAsync(UserId(user), ct)).Select(ToOwn).ToList());

    // ---- owner replies ----

    private static IResult Replied(ReplyResult r, bool created) => r.Failure switch
    {
        ReplyFailure.None => created ? Results.Created("/api/me/claims", ToDto(r.Reply!)) : Results.Ok(ToDto(r.Reply!)),
        ReplyFailure.NotOwner => Results.Json(new ErrorBody("not_owner"), statusCode: StatusCodes.Status403Forbidden),
        ReplyFailure.InvalidText => Results.BadRequest(new ErrorBody("invalid_text")),
        ReplyFailure.AlreadyReplied => Results.Conflict(new ErrorBody("already_replied")),
        _ => Results.NotFound(new ErrorBody("not_found")),
    };

    private static OwnerReplyDto ToDto(OwnerReply r) => new(r.Id, r.ReviewId, r.Text, r.CreatedAt, r.EditedAt);

    private static async Task<IResult> CreateReply(string reviewId, ReplyBody body, ClaimsPrincipal user, OwnerReplyService service, CancellationToken ct) =>
        Replied(await service.CreateAsync(UserId(user), reviewId, body.Text, ct), created: true);

    private static async Task<IResult> EditReply(string reviewId, ReplyBody body, ClaimsPrincipal user, OwnerReplyService service, CancellationToken ct) =>
        Replied(await service.EditAsync(UserId(user), reviewId, body.Text, ct), created: false);

    private static async Task<IResult> DeleteReply(string reviewId, ClaimsPrincipal user, OwnerReplyService service, CancellationToken ct)
    {
        var r = await service.DeleteAsync(UserId(user), reviewId, ct);
        return r.Ok ? Results.NoContent() : Replied(r, false);
    }

    // ---- dashboard ----

    private static async Task<IResult> Places(ClaimsPrincipal user, DashboardService service, CancellationToken ct) => Results.Ok(await service.PlacesAsync(UserId(user), ct));

    private static async Task<IResult> GetDashboard(string entityId, ClaimsPrincipal user, DashboardService service, CancellationToken ct) =>
        await service.GetAsync(UserId(user), entityId, ct) is { } d ? Results.Ok(d) : Results.NotFound();

    private static async Task<IResult> MarkSeen(string entityId, ClaimsPrincipal user, DashboardService service, CancellationToken ct) =>
        await service.MarkSeenAsync(UserId(user), entityId, ct) ? Results.NoContent() : Results.NotFound();

    // ---- moderators ----

    private static async Task<IResult> ClaimQueue(ClaimService service, CancellationToken ct) =>
        Results.Ok((await service.QueueAsync(ct)).Select(c => new ClaimForReview(
            c.Id, c.EntityId, c.CompanyEmail, c.EmailDomain, c.Role, c.Website, c.Note, c.DocumentType, c.FreeEmail, c.DomainMatchesPlace, c.CreatedAt)).ToList());

    private static async Task<IResult> ClaimDocument(string id, ClaimsPrincipal user, ClaimService service, CancellationToken ct)
    {
        var (failure, content, contentType) = await service.ReadDocumentAsync(UserId(user), id, ct);
        return failure == ClaimFailure.None ? Results.File(content!, contentType) : Failed(failure);
    }

    private static async Task<IResult> Approve(string id, ClaimsPrincipal user, ClaimService service, CancellationToken ct)
    {
        var r = await service.DecideAsync(UserId(user), id, approve: true, null, ct);
        return r.Ok ? Results.Ok(ToOwn(r.Claim!)) : Failed(r.Failure);
    }

    private static async Task<IResult> Reject(string id, ReasonBody body, ClaimsPrincipal user, ClaimService service, CancellationToken ct)
    {
        var r = await service.DecideAsync(UserId(user), id, approve: false, body.Reason, ct);
        return r.Ok ? Results.Ok(ToOwn(r.Claim!)) : Failed(r.Failure);
    }

    private static async Task<IResult> Revoke(string entityId, ReasonBody body, ClaimsPrincipal user, ClaimService service, CancellationToken ct)
    {
        var f = await service.RevokeAsync(UserId(user), entityId, body.Reason, ct);
        return f == ClaimFailure.None ? Results.NoContent() : Failed(f);
    }
}

public sealed record OwnerReplyDto(string Id, string ReviewId, string Text, DateTime CreatedAt, DateTime? EditedAt);
