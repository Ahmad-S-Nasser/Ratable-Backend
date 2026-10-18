using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Modules.Reviews.Services;
using Ratable.Shared.Catalog;
using Ratable.Shared.Data;
using Ratable.Shared.Location;

namespace Ratable.Modules.Reviews.Endpoints;

public sealed record NearbyArea(string Id, LocalizedText Name);

/// <summary>The rated places inside the chosen distance. The point the search started from is not echoed back and is never stored.</summary>
public sealed record NearbyList(List<PlaceCard> Places, int Total, int RadiusKm, NearbyArea? Area);

public static class NearbyEndpoints
{
    /// <summary>The distances a visitor can choose, in kilometres (CLAUDE.md rule 17).</summary>
    public static readonly int[] Radii = [1, 3, 5, 10, 25];

    public static IEndpointRouteBuilder MapNearbyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/places", Nearby).WithTags("Location").Produces<NearbyList>().Produces<ErrorBody>(400).Produces(404);
        return app;
    }

    /// <summary>
    /// GET /api/places?lat&amp;lng&amp;radiusKm&amp;area&amp;category. Starts from the visitor's location (header or query, used for this answer only) or from a
    /// chosen area. Results stay inside the distance and are ranked by verified score first, then by distance; nothing paid changes the order.
    /// </summary>
    private static async Task<IResult> Nearby(
        double? lat, double? lng, int? radiusKm, string? area, string? category, string? lang, int? limit, bool? ratedOnly, HttpRequest http,
        ReviewRepository repository, IEntityCatalog catalog, ILocationProvider locations, IOptions<VerdictOptions> verdictOptions, CancellationToken ct)
    {
        var radius = radiusKm ?? 5;
        if (!Radii.Contains(radius)) return Results.BadRequest(new ErrorBody("invalid_radius", string.Join(',', Radii)));

        var origin = VisitorLocation.From(http, lat, lng);
        AreaInfo? chosen = null;
        if (area is not null)
        {
            chosen = await locations.GetAreaAsync(area, ct);
            if (chosen is null) return Results.NotFound();
        }
        // No device location: use the middle of the chosen area. Neither: there is nothing to measure from.
        origin ??= chosen is null ? null : (chosen.Latitude, chosen.Longitude);
        if (origin is null) return Results.BadRequest(new ErrorBody("location_required"));
        if (category is not null && await catalog.GetCategoryAsync(category, ct) is null) return Results.NotFound();

        var entries = await catalog.ListNearAsync(origin.Value.Lat, origin.Value.Lng, radius * 1000.0, category, 200, ct);
        var reviews = (await repository.ListScoringForEntitiesAsync([.. entries.Select(e => e.Id)], ct)).ToLookup(r => r.EntityId);
        var ranked = PlaceRanking.RankNearby(entries, reviews, verdictOptions.Value, lang ?? "ar", origin.Value, radius * 1000.0);
        if (ratedOnly ?? true) ranked = [.. ranked.Where(p => p.Overall.HasValue)];

        return Results.Ok(new NearbyList([.. ranked.Take(Math.Clamp(limit ?? 20, 1, 50))], ranked.Count, radius, chosen is null ? null : new NearbyArea(chosen.Id, chosen.Name)));
    }
}
