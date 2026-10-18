using Ratable.Shared.Authors;

namespace Ratable.Shared.Businesses;

/// <summary>Implemented by the Businesses module: who runs a place. Reviews uses it to refuse a review from the owner (CLAUDE.md, P10); Moderation uses it for disputes.</summary>
public interface IEntityOwnership
{
    Task<bool> IsOwnerAsync(string userId, string entityId, CancellationToken cancellationToken = default);
}

/// <summary>A public reply from the owner under a review. Carries nothing about the reviewer.</summary>
public sealed record OwnerReplyInfo(string Id, string Text, DateTime CreatedAt, DateTime? EditedAt);

/// <summary>Implemented by the Businesses module so Reviews can show the owner's reply under a review without knowing how replies are stored.</summary>
public interface IOwnerReplyDirectory
{
    Task<IReadOnlyDictionary<string, OwnerReplyInfo>> GetAsync(IReadOnlyCollection<string> reviewIds, CancellationToken cancellationToken = default);
}

/// <summary>Implemented by the Identity module: approving a claim makes the account a business owner, and removing the last place takes that back.</summary>
public interface IBusinessRoleGranter
{
    Task SetBusinessOwnerAsync(string userId, bool isOwner, CancellationToken cancellationToken = default);
}

public sealed record MonthlyScore(string Month, double? Score, int Reviews);

/// <summary>Counts and scores for one place, worked out from published and Verified reviews exactly as the public score is. Nothing a business pays for enters here.</summary>
public sealed record BusinessReviewStats(
    int PublishedReviews, int VerifiedReviews, double? Score, double? CategoryAverage, IReadOnlyList<MonthlyScore> Trend);

/// <summary>One review as a business sees it. <see cref="Author"/> is null for an anonymous review, always: a business never learns who wrote it.</summary>
public sealed record BusinessReviewItem(
    string Id, DateTime CreatedAt, double? Overall, string? Text, string Language, bool Verified, bool Anonymous, PublicAuthor? Author, bool UnderReview);

/// <summary>Implemented by the Reviews module for the business dashboard.</summary>
public interface IBusinessReviewSource
{
    Task<BusinessReviewStats> GetStatsAsync(string entityId, string categoryId, int trendMonths, CancellationToken cancellationToken = default);

    /// <summary>Published reviews, newest first.</summary>
    Task<IReadOnlyList<BusinessReviewItem>> ListAsync(string entityId, int limit, CancellationToken cancellationToken = default);

    Task<int> CountCreatedSinceAsync(string entityId, DateTime since, CancellationToken cancellationToken = default);
}
