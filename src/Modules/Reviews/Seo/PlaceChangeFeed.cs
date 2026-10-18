using Ratable.Modules.Reviews.Data;
using Ratable.Shared.Catalog;

namespace Ratable.Modules.Reviews.Seo;

public sealed record ChangedPlace(string Id, string CategoryId, IReadOnlyDictionary<string, string> Slugs);

/// <summary>The places whose public page changed since a moment: the place itself was edited, or its published reviews changed.</summary>
public sealed class PlaceChangeFeed(ReviewRepository reviews, IEntityCatalog catalog)
{
    public async Task<IReadOnlyList<ChangedPlace>> GetChangedAsync(DateTime since, int limit = 1000, CancellationToken ct = default)
    {
        var fromEntities = await catalog.ListUpdatedSinceAsync(since, limit, ct);
        var reviewIds = await reviews.ListEntityIdsChangedSinceAsync(since, limit, ct);
        var known = fromEntities.Select(e => e.Id).ToHashSet();
        var fromReviews = await catalog.GetActiveEntriesAsync([.. reviewIds.Where(id => !known.Contains(id))], ct);
        return [.. fromEntities.Concat(fromReviews).Select(e => new ChangedPlace(e.Id, e.CategoryId, e.Slugs))];
    }

    /// <summary>The address of a place page, as the web app builds it: /{lang}/{category}/{id}-{slug}. The id never changes; the slug falls back to the other language.</summary>
    public static string PagePath(string lang, ChangedPlace place)
    {
        var other = lang == "ar" ? "en" : "ar";
        var slug = place.Slugs.TryGetValue(lang, out var s) && s.Length > 0 ? s
            : place.Slugs.TryGetValue(other, out var o) && o.Length > 0 ? o : "place";
        return $"/{lang}/{place.CategoryId}/{place.Id}-{slug}";
    }
}
