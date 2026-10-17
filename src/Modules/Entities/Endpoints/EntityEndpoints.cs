using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Entities.Services;
using Ratable.Shared.Auth;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;
using Ratable.Shared.Text;

namespace Ratable.Modules.Entities.Endpoints;

public sealed record SubmitEntityBody(
    string? NameAr, string? NameEn, string? CategoryId, double? Longitude, double? Latitude,
    string? Address, string? City, string? Country, List<string>? AlternateNames, Dictionary<string, string>? Fields);

public sealed record RejectBody(string? Reason);
public sealed record MergeBody(string? IntoEntityId);
public sealed record ErrorBody(string Error, string? Detail = null);
public sealed record SubmittedResponse(string Id, string Status);

/// <summary>What anyone may see about an active entity. It carries nothing about who submitted it.</summary>
public sealed record PublicEntity(
    string Id, string CategoryId, LocalizedText Name, List<string> AlternateNames, Dictionary<string, string> Slugs,
    double? Longitude, double? Latitude, string? Address, string? City, string? Country,
    Dictionary<string, string> ExternalIds, List<string> SameAs, Dictionary<string, string> Fields,
    LocalizedText? SummaryText, DateTime UpdatedAt);

public sealed record QueueCandidate(string EntityId, LocalizedText Name, double NameScore, double? DistanceMeters, string Status);
public sealed record QueueItem(
    string Id, string CategoryId, LocalizedText Name, List<string> AlternateNames, double? Longitude, double? Latitude,
    string? Address, string? City, Dictionary<string, string> Fields, DateTime SubmittedAt,
    string Suggestion, string? SuggestedDuplicateOfId, double? Confidence, string AiStatus, List<QueueCandidate> Candidates);

public sealed record ConflictDto(string Module, string Code, int Count);

public static class EntityEndpoints
{
    public static IEndpointRouteBuilder MapEntityEndpoints(this IEndpointRouteBuilder app)
    {
        var entities = app.MapGroup("/api/entities").WithTags("Entities");
        entities.MapGet("/search", Search);
        entities.MapGet("/{id}", Get);
        entities.MapPost("", Submit).RequireAuthorization(PolicyNames.SignedIn);

        var queue = app.MapGroup("/api/moderation/entities").WithTags("Moderation").RequireAuthorization(PolicyNames.Moderator);
        queue.MapGet("", ListQueue);
        queue.MapPost("/{id}/approve", Approve);
        queue.MapPost("/{id}/reject", Reject);
        queue.MapPost("/{id}/merge", Merge);
        return app;
    }

    private static async Task<IResult> Submit(SubmitEntityBody body, ClaimsPrincipal user, EntityService service, CancellationToken ct)
    {
        var result = await service.SubmitAsync(user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            new SubmitEntityRequest(body.NameAr, body.NameEn, body.CategoryId, body.Longitude, body.Latitude, body.Address, body.City, body.Country,
                body.AlternateNames, body.Fields), ct);
        return result.Failure switch
        {
            SubmitFailure.None => Results.Created($"/api/entities/{result.Entity!.Id}", new SubmittedResponse(result.Entity.Id, result.Entity.Status.ToString())),
            SubmitFailure.InvalidCategory => Results.BadRequest(new ErrorBody("invalid_category")),
            SubmitFailure.InvalidName => Results.BadRequest(new ErrorBody("invalid_name")),
            SubmitFailure.InvalidLocation => Results.BadRequest(new ErrorBody("invalid_location")),
            SubmitFailure.InvalidField => Results.BadRequest(new ErrorBody("invalid_field", result.Detail)),
            _ => Results.Json(new ErrorBody("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests),
        };
    }

    private static async Task<IResult> Get(string id, EntityService service, CancellationToken ct)
    {
        var (entity, redirected) = await service.ResolveAsync(id, ct);
        if (entity is null) return Results.NotFound();
        return redirected ? Results.Redirect($"/api/entities/{entity.Id}", permanent: true) : Results.Ok(ToPublic(entity));
    }

    private static async Task<IResult> Search(string? q, string? category, int? limit, EntityRepository repository, CancellationToken ct)
    {
        var normalized = ArabicNormalizer.Normalize(q);
        if (normalized.Length == 0) return Results.BadRequest(new ErrorBody("query_required"));
        var found = await repository.SearchTextAsync(normalized, category, Math.Clamp(limit ?? 20, 1, 50), ct);
        return Results.Ok(found.Select(ToPublic));
    }

    private static async Task<IResult> ListQueue(EntityRepository repository, CancellationToken ct)
    {
        var pending = await repository.ListByStatusAsync(EntityStatus.PendingModeration, ct: ct);
        return Results.Ok(pending.Select(ToQueueItem));
    }

    private static async Task<IResult> Approve(string id, ClaimsPrincipal user, EntityService service, CancellationToken ct) =>
        ToResult(await service.ApproveAsync(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!, ct));

    private static async Task<IResult> Reject(string id, RejectBody body, ClaimsPrincipal user, EntityService service, CancellationToken ct) =>
        ToResult(await service.RejectAsync(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!, body.Reason, ct));

    private static async Task<IResult> Merge(string id, MergeBody body, ClaimsPrincipal user, EntityService service, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(body.IntoEntityId)
            ? Results.BadRequest(new ErrorBody("invalid_target"))
            : ToResult(await service.MergeAsync(id, body.IntoEntityId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, ct));

    private static IResult ToResult(ActionResult r) => r.Outcome switch
    {
        ActionOutcome.Ok => Results.Ok(new SubmittedResponse(r.Entity!.Id, r.Entity.Status.ToString())),
        ActionOutcome.NotFound => Results.NotFound(),
        ActionOutcome.WrongState => Results.Conflict(new ErrorBody("wrong_state")),
        ActionOutcome.CategoryMismatch => Results.Conflict(new ErrorBody("category_mismatch")),
        ActionOutcome.Conflict => Results.Conflict(new { error = "merge_conflict", conflicts = r.Conflicts!.Select(c => new ConflictDto(c.Module, c.Code, c.Count)) }),
        _ => Results.BadRequest(new ErrorBody("invalid_request")),
    };

    private static PublicEntity ToPublic(Entity e) => new(
        e.Id, e.CategoryId, e.Name, e.AlternateNames, e.Slugs,
        e.Location?.Coordinates.Longitude, e.Location?.Coordinates.Latitude, e.Address, e.City, e.Country,
        e.ExternalIds, e.SameAs, e.Fields, e.Summary?.Text, e.UpdatedAt);

    private static QueueItem ToQueueItem(Entity e)
    {
        var check = e.DuplicateCheck ?? new DuplicateCheck();
        return new QueueItem(e.Id, e.CategoryId, e.Name, e.AlternateNames, e.Location?.Coordinates.Longitude, e.Location?.Coordinates.Latitude,
            e.Address, e.City, e.Fields, e.CreatedAt, check.Suggestion.ToString(), check.SuggestedDuplicateOfId, check.Confidence, check.AiStatus,
            [.. check.Candidates.Select(c => new QueueCandidate(c.EntityId, c.Name, c.NameScore, c.DistanceMeters, c.Status.ToString()))]);
    }
}
