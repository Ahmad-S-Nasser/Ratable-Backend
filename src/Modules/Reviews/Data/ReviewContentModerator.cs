using MongoDB.Driver;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Reviews.Data;

/// <summary>How the Moderation module publishes, hides, labels and reads reviews. It changes only the moderation fields, never the content.</summary>
public sealed class ReviewContentModerator(ReviewRepository reviews, ReviewRevisionRepository revisions, TimeProvider time) : IContentModerator
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
        return await reviews.TryReplaceAsync(review, review.RevisionNumber, ct); // false if the author edited meanwhile: the new version has its own case
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
