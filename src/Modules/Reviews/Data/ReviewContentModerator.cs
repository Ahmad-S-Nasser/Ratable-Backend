using MongoDB.Driver;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;
using Ratable.Shared.Rewards;

namespace Ratable.Modules.Reviews.Data;

/// <summary>How the Moderation module publishes, hides, labels and reads reviews. It changes only the moderation fields, never the content.</summary>
public sealed class ReviewContentModerator(
    ReviewRepository reviews, ReviewRevisionRepository revisions, TimeProvider time, IEnumerable<IContributionRecorder> recorders) : IContentModerator
{
    public ContentKind Kind => ContentKind.Review;

    public async Task<bool> ApplyAsync(string contentId, ModerationOutcome outcome, string? reason, CancellationToken ct = default)
    {
        var review = await reviews.GetAsync(contentId, ct);
        if (review is null) return false;

        review.Status = outcome == ModerationOutcome.Publish ? ReviewStatus.Published : ReviewStatus.Hidden;
        review.ModerationReason = outcome == ModerationOutcome.Hide ? reason : null;
        review.ModeratedAt = time.GetUtcNow().UtcDateTime;
        if (outcome == ModerationOutcome.Hide) review.UnderReview = false;
        var changed = await reviews.TryReplaceAsync(review, review.RevisionNumber, ct); // false if the author edited meanwhile: the new version has its own case
        if (changed) await UpdatePointsAsync(review, outcome, ct);
        return changed;
    }

    /// <summary>
    /// Points are earned when a review is published and cancelled when it is hidden (a duplicate or a paid review, or one that breaks a rule). What the review
    /// says, good or bad, plays no part: only that real effort was published.
    /// </summary>
    private async Task UpdatePointsAsync(Review review, ModerationOutcome outcome, CancellationToken ct)
    {
        foreach (var recorder in recorders)
        {
            if (outcome == ModerationOutcome.Hide)
            {
                await recorder.ReverseRootAsync(review.Id, "review_hidden", ct);
                continue;
            }
            await recorder.RecordAsync(review.UserId, ContributionActions.WrittenReview, review.Id, review.Id, review.EntityId, review.Text?.Trim().Length ?? 0, ct);
            if (await reviews.CountOtherPublishedAsync(review.EntityId, review.Id, ct) == 0)
                await recorder.RecordAsync(review.UserId, ContributionActions.FirstReviewOfPlace, $"first:{review.EntityId}", review.Id, review.EntityId, cancellationToken: ct);
        }
    }

    public async Task<bool> SetUnderReviewAsync(string contentId, bool underReview, CancellationToken ct = default)
    {
        var review = await reviews.GetAsync(contentId, ct);
        if (review is null) return false;
        review.UnderReview = underReview;
        return await reviews.TryReplaceAsync(review, review.RevisionNumber, ct);
    }

    public async Task<ModerationContentView?> GetViewAsync(string contentId, CancellationToken ct = default)
    {
        var review = await reviews.GetAsync(contentId, ct);
        if (review is null) return null;
        var previous = review.RevisionNumber > 1 ? (await revisions.ListAsync(review.Id, ct)).LastOrDefault() : null;
        return new ModerationContentView(
            review.Id, ContentKind.Review, review.Text, review.Language, review.RevisionNumber, previous?.Text, review.EntityId, review.CategoryId,
            review.Status.ToString(), review.Verification.Level == VerificationLevel.Verified, review.UnderReview,
            ScoreCalculator.ReviewOverall(review.Answers), review.IsAnonymous, review.CreatedAt, review.UpdatedAt);
    }

    public Task<IReadOnlyList<PendingContent>> ListPendingAsync(int limit, CancellationToken ct = default) => reviews.ListPendingAsync(limit, ct);
}
