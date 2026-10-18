using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Endpoints;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Modules.Reviews.Services;
using Ratable.Shared.Catalog;
using Ratable.Shared.Data;
using Ratable.Shared.Text;

namespace Ratable.Modules.Reviews.Lists;

public sealed record ListSummary(string Id, Dictionary<string, string> Slug, LocalizedText Title, string CategoryId, LocalizedText? Area);

/// <summary>A list with its places, ranked only by verified score. <see cref="UpdatedAt"/> is the real date of the latest change to any place or review in it.</summary>
public sealed record ListView(
    string Id, Dictionary<string, string> Slug, LocalizedText Title, string CategoryId, LocalizedText? Area, int MinReviews, int Limit, DateTime? UpdatedAt, List<PlaceCard> Places);

public static class ListEndpoints
{
    public static IEndpointRouteBuilder MapListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/collections").WithTags("Collections");
        group.MapGet("", async (ListRepository lists, CancellationToken ct) =>
                Results.Ok((await lists.ListActiveAsync(ct)).Select(l => new ListSummary(l.Id, Slugs(l), l.Title, l.CategoryId, l.Area))))
            .Produces<List<ListSummary>>();
        group.MapGet("/{id}", Get).Produces<ListView>().Produces(404);
        return app;
    }

    private static Dictionary<string, string> Slugs(ListDefinition l) => new() { ["ar"] = l.Slug.Ar, ["en"] = l.Slug.En };

    private static bool InArea(CatalogEntry e, LocalizedText? area)
    {
        if (area is null || (area.Ar.Length == 0 && area.En.Length == 0)) return true;
        var haystack = ArabicNormalizer.Normalize($"{e.City} {e.Address}");
        return new[] { area.Ar, area.En }.Where(a => a.Length > 0).Any(a => haystack.Contains(ArabicNormalizer.Normalize(a), StringComparison.Ordinal));
    }

    private static async Task<IResult> Get(
        string id, string? lang, double? lat, double? lng, ListRepository lists, ReviewRepository repository, IEntityCatalog catalog, IOptions<VerdictOptions> verdictOptions, CancellationToken ct)
    {
        var list = await lists.GetAsync(id, ct);
        if (list is null || await catalog.GetCategoryAsync(list.CategoryId, ct) is null) return Results.NotFound();

        var entries = (await catalog.ListActiveByCategoryAsync(list.CategoryId, 500, ct)).Where(e => InArea(e, list.Area)).ToList();
        var reviews = (await repository.ListScoringForEntitiesAsync([.. entries.Select(e => e.Id)], ct)).ToLookup(r => r.EntityId);
        var ranked = PlaceRanking.Rank(entries, reviews, verdictOptions.Value, lang ?? "ar", lat is { } a && lng is { } b ? (a, b) : null)
            .Where(p => p.Overall.HasValue && p.VerifiedReviews >= list.MinReviews) // only places with a verified score; never on promise
            .Take(Math.Clamp(list.Limit, 1, 50)).ToList();

        return Results.Ok(new ListView(list.Id, Slugs(list), list.Title, list.CategoryId, list.Area, list.MinReviews, list.Limit,
            ranked.Count == 0 ? null : ranked.Max(p => p.LastModified), ranked));
    }
}
