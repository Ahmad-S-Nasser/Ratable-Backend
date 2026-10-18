using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Shared.Authors;
using Ratable.Shared.Businesses;

namespace Ratable.Modules.Reviews.Data;

/// <summary>
/// What the business dashboard may know about reviews. The score and the trend use the same rule as the public score (published and Verified only), so the
/// dashboard and the page agree. The author of a review comes only from <see cref="IAuthorPresenter"/>: an anonymous review has no author here (rule 1).
/// </summary>
public sealed class BusinessReviewSource(ReviewRepository reviews, IAuthorPresenter authors, TimeProvider time) : IBusinessReviewSource
{
    public async Task<BusinessReviewStats> GetStatsAsync(string entityId, string categoryId, int trendMonths, CancellationToken ct = default)
    {
        var score = ScoreCalculator.ForEntity(await reviews.ListScoringAsync(entityId, ct));
        var published = (int)await reviews.CountPublishedAsync(entityId, ct);

        // The average of the scores of the category's places that have a score.
        var perPlace = (await reviews.ListScoringForCategoryAsync(categoryId, ct))
            .GroupBy(r => r.EntityId)
            .Select(g => ScoreCalculator.ForEntity(g).Overall)
            .Where(o => o.HasValue)
            .Select(o => o!.Value)
            .ToList();
        double? categoryAverage = perPlace.Count == 0 ? null : Math.Round(perPlace.Average(), 2);

        var now = time.GetUtcNow().UtcDateTime;
        var first = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(Math.Max(1, trendMonths) - 1));
        var dated = await reviews.ListScoringWithDatesAsync(entityId, first, ct);
        var trend = Enumerable.Range(0, Math.Max(1, trendMonths)).Select(i =>
        {
            var start = first.AddMonths(i);
            var inMonth = dated.Where(r => r.CreatedAt >= start && r.CreatedAt < start.AddMonths(1)).Select(r => ScoreCalculator.ReviewOverall(r.Answers)).Where(o => o.HasValue).Select(o => o!.Value).ToList();
            return new MonthlyScore(start.ToString("yyyy-MM"), inMonth.Count == 0 ? null : Math.Round(inMonth.Average(), 2), inMonth.Count);
        }).ToList();

        return new BusinessReviewStats(published, score.VerifiedReviewCount, score.Overall, categoryAverage, trend);
    }

    public async Task<IReadOnlyList<BusinessReviewItem>> ListAsync(string entityId, int limit, CancellationToken ct = default)
    {
        var found = await reviews.ListPublishedAsync(entityId, Math.Clamp(limit, 1, 200), 0, null, false, ct);
        var presented = await authors.PresentAsync([.. found.Select(r => new AuthorSlot(r.Id, r.UserId, r.IsAnonymous))], ct);
        return [.. found.Select(r => new BusinessReviewItem(
            r.Id, r.CreatedAt, ScoreCalculator.ReviewOverall(r.Answers), r.Text, r.Language, r.Verification.Level == VerificationLevel.Verified,
            presented[r.Id] is null, presented[r.Id], r.UnderReview))];
    }

    public async Task<int> CountCreatedSinceAsync(string entityId, DateTime since, CancellationToken ct = default) =>
        (int)await reviews.CountPublishedSinceAsync(entityId, since, ct);
}
