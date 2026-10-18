using Ratable.Modules.Reviews.Endpoints;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Shared.Catalog;

namespace Ratable.Modules.Reviews.Services;

/// <summary>
/// The one way places are put in order (CLAUDE.md rules 6, 13 and 17): by verified score first, then by the number of verified reviews, then by name.
/// A place with no verified score comes last. Distance is shown when the visitor shared a location but never changes the order, and nothing paid is an
/// input here. Used by the category list, the collections and the guide pages, so they cannot disagree.
/// </summary>
public static class PlaceRanking
{
    public static List<PlaceCard> Rank(IEnumerable<CatalogEntry> entries, ILookup<string, Review> reviews, VerdictOptions verdictOptions, string lang, (double Lat, double Lng)? from = null)
    {
        var key = lang == "en" ? "en" : "ar";
        return [.. entries
            .Select(e => ToCard(e, reviews[e.Id].ToList(), verdictOptions, from))
            .OrderByDescending(p => p.Overall.HasValue).ThenByDescending(p => p.Overall).ThenByDescending(p => p.VerifiedReviews)
            .ThenBy(p => key == "en" ? p.Name.En ?? p.Name.Ar : p.Name.Ar ?? p.Name.En, StringComparer.CurrentCulture)];
    }

    public static PlaceCard ToCard(CatalogEntry e, List<Review> reviews, VerdictOptions verdictOptions, (double Lat, double Lng)? from)
    {
        var score = ScoreCalculator.ForEntity(reviews);
        var verdict = VerdictCalculator.ForEntity(reviews, score.Overall, score.VerifiedReviewCount, verdictOptions);
        var latest = reviews.Select(x => x.UpdatedAt).DefaultIfEmpty(DateTime.MinValue).Max();
        double? distance = from is { } f && e.Latitude is { } lat && e.Longitude is { } lon ? Math.Round(DistanceMeters(f.Lat, f.Lng, lat, lon)) : null;
        string? price = null;
        e.Fields?.TryGetValue("priceBand", out price);
        return new PlaceCard(
            e.Id, e.Name, new Dictionary<string, string>(e.Slugs), e.City, score.Overall, score.VerifiedReviewCount, e.UpdatedAt > latest ? e.UpdatedAt : latest,
            verdict is null ? null : new VerdictDto(verdict.Kind, verdict.SharePercent, verdict.Votes), string.IsNullOrWhiteSpace(price) ? null : price, distance,
            TopCriteria(score));
    }

    /// <summary>The criteria with a shown score of at least 4: what verified reviewers rate highest. Used as the tags on a card.</summary>
    private static List<string> TopCriteria(EntityScore score) =>
        [.. score.Criteria.Where(c => c.Score >= 4).OrderByDescending(c => c.Score).Take(3).Select(c => c.CriterionId)];

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
