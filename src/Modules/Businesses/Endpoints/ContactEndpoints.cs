using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Businesses.Models;
using Ratable.Modules.Businesses.Services;
using Ratable.Shared.Auth;
using Ratable.Shared.Businesses;

namespace Ratable.Modules.Businesses.Endpoints;

public sealed record ContactBody(string? Phone, string? WhatsApp, List<string>? Links);
public sealed record ContactDto(string? Phone, string? WhatsApp, List<string> Links);
public sealed record ReplyRequestBody(string? Text);
public sealed record SubscriptionBody(string? PlanId, DateTime? ValidUntil);
public sealed record OwnRequest(string Id, string EntityId, string What, string? When, bool ShareWhatsApp, string Status, DateTime CreatedAt, DateTime ExpiresAt);
public sealed record StatsResponse(BusinessStats Stats);
public sealed record NudgesResponse(List<Nudge> Nudges);

public static class ContactEndpoints
{
    public static IEndpointRouteBuilder MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/entities/{entityId}/contact-requests", Create).WithTags("Contact").RequireAuthorization(PolicyNames.SignedIn)
            .Produces<OwnRequest>(201).Produces<ErrorBody>(400).Produces<ErrorBody>(404).Produces<ErrorBody>(409).Produces<ErrorBody>(429);
        app.MapGet("/api/me/contact-requests", Mine).WithTags("Contact").RequireAuthorization(PolicyNames.SignedIn).Produces<List<ReaderRequestView>>();

        var biz = app.MapGroup("/api/business/places/{entityId}").WithTags("Contact").RequireAuthorization(PolicyNames.BusinessOwner);
        biz.MapGet("/contact", GetContact).Produces<ContactDto>().Produces(404);
        biz.MapPut("/contact", SetContact).Produces<ContactDto>().Produces<ErrorBody>(400).Produces<ErrorBody>(403);
        biz.MapGet("/contact-requests", ForProvider).Produces<ProviderRequests>().Produces(404);
        biz.MapPost("/contact-requests/{requestId}/reply", Reply).Produces<OwnRequest>().Produces<ErrorBody>(400).Produces<ErrorBody>(403).Produces<ErrorBody>(404).Produces<ErrorBody>(409);
        biz.MapGet("/stats", Stats).Produces<StatsResponse>().Produces<ErrorBody>(403).Produces(404);
        biz.MapGet("/nudges", Nudges).Produces<NudgesResponse>().Produces(404);
        biz.MapPost("/nudges/{trigger}/dismiss", DismissNudge).Produces(204).Produces(404);

        app.MapPut("/api/admin/entities/{entityId}/subscription", Grant).WithTags("Admin").RequireAuthorization(PolicyNames.Admin).Produces(204).Produces<ErrorBody>(400);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static OwnRequest ToOwn(ContactRequest r) => new(r.Id, r.EntityId, r.What, r.When, r.ShareWhatsApp, r.Status.ToString(), r.CreatedAt, r.ExpiresAt);

    private static async Task<IResult> Create(string entityId, RequestInput body, ClaimsPrincipal user, ContactRequestService service, CancellationToken ct)
    {
        var r = await service.CreateAsync(UserId(user), entityId, body, ct);
        return r.Failure switch
        {
            RequestFailure.None => Results.Created("/api/me/contact-requests", ToOwn(r.Request!)),
            RequestFailure.EntityNotFound => Results.NotFound(new ErrorBody("not_found")),
            RequestFailure.NotLocked => Results.Conflict(new ErrorBody("not_locked")),
            RequestFailure.OwnPlace => Results.Conflict(new ErrorBody("own_place")),
            RequestFailure.AlreadyOpen => Results.Conflict(new ErrorBody("already_open")),
            RequestFailure.InvalidText => Results.BadRequest(new ErrorBody("invalid_text")),
            RequestFailure.PhoneRequired => Results.BadRequest(new ErrorBody("phone_required")),
            RequestFailure.TooManyToday => Results.Json(new ErrorBody("too_many_today"), statusCode: StatusCodes.Status429TooManyRequests),
            _ => Results.Json(new ErrorBody("provider_busy"), statusCode: StatusCodes.Status429TooManyRequests),
        };
    }

    private static async Task<IResult> Mine(ClaimsPrincipal user, ContactRequestService service, CancellationToken ct) => Results.Ok(await service.MineAsync(UserId(user), ct));

    private static async Task<IResult> GetContact(string entityId, ClaimsPrincipal user, ContactAdminService service, CancellationToken ct) =>
        await service.GetContactAsync(UserId(user), entityId, ct) is { } c ? Results.Ok(new ContactDto(c.Phone, c.WhatsApp, [.. c.Links])) : Results.NotFound();

    private static async Task<IResult> SetContact(string entityId, ContactBody body, ClaimsPrincipal user, ContactAdminService service, CancellationToken ct)
    {
        var (ok, error) = await service.SetContactAsync(UserId(user), entityId, body.Phone, body.WhatsApp, body.Links, ct);
        if (!ok) return error == "not_owner" ? Results.Json(new ErrorBody(error), statusCode: StatusCodes.Status403Forbidden) : Results.BadRequest(new ErrorBody(error ?? "invalid"));
        var c = (await service.GetContactAsync(UserId(user), entityId, ct))!;
        return Results.Ok(new ContactDto(c.Phone, c.WhatsApp, [.. c.Links]));
    }

    private static async Task<IResult> ForProvider(string entityId, ClaimsPrincipal user, ContactRequestService service, CancellationToken ct) =>
        await service.ForProviderAsync(UserId(user), entityId, ct) is { } r ? Results.Ok(r) : Results.NotFound();

    private static async Task<IResult> Reply(string entityId, string requestId, ReplyRequestBody body, ClaimsPrincipal user, ContactRequestService service, CancellationToken ct)
    {
        var r = await service.ReplyAsync(UserId(user), entityId, requestId, body.Text, ct);
        return r.Failure switch
        {
            RequestFailure.None => Results.Ok(ToOwn(r.Request!)),
            RequestFailure.PlanRequired => Results.Json(new ErrorBody("plan_required"), statusCode: StatusCodes.Status403Forbidden),
            RequestFailure.InvalidText => Results.BadRequest(new ErrorBody("invalid_text")),
            RequestFailure.WrongState => Results.Conflict(new ErrorBody("closed")),
            _ => Results.NotFound(new ErrorBody("not_found")),
        };
    }

    private static async Task<IResult> Stats(
        string entityId, ClaimsPrincipal user, OwnershipRepository ownerships, IContactAccess access, BusinessStatsService stats, Ratable.Shared.Catalog.IEntityCatalog catalog, CancellationToken ct)
    {
        if (!await ownerships.ExistsAsync(UserId(user), entityId, ct)) return Results.NotFound();
        // The brief stats belong to the contact plan; without it the owner gets the upgrade messages instead.
        if (!await access.PlanShowsContactAsync(entityId, ct)) return Results.Json(new ErrorBody("plan_required"), statusCode: StatusCodes.Status403Forbidden);
        var entry = (await catalog.GetActiveEntriesAsync([entityId], ct)).FirstOrDefault();
        return entry is null ? Results.NotFound() : Results.Ok(new StatsResponse(await stats.GetAsync(entityId, entry.CategoryId, ct)));
    }

    private static async Task<IResult> Nudges(string entityId, ClaimsPrincipal user, OwnershipRepository ownerships, IBusinessNudgeService service, CancellationToken ct) =>
        await ownerships.ExistsAsync(UserId(user), entityId, ct) ? Results.Ok(new NudgesResponse([.. await service.GetAsync(UserId(user), entityId, ct)])) : Results.NotFound();

    private static async Task<IResult> DismissNudge(string entityId, string trigger, ClaimsPrincipal user, IBusinessNudgeService service, CancellationToken ct) =>
        await service.DismissAsync(UserId(user), entityId, trigger, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> Grant(string entityId, SubscriptionBody body, ClaimsPrincipal user, ContactAdminService service, CancellationToken ct)
    {
        var (ok, error) = await service.GrantAsync(UserId(user), entityId, body.PlanId, body.ValidUntil, ct);
        return ok ? Results.NoContent() : Results.BadRequest(new ErrorBody(error ?? "invalid"));
    }
}
