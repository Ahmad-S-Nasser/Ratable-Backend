using Microsoft.Extensions.Options;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Businesses.Models;
using Ratable.Shared.Businesses;
using Ratable.Shared.Catalog;
using Ratable.Shared.Content;
using Ratable.Shared.Data;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Businesses.Services;

/// <summary>Answers "does this account run this place". Reviews uses it to refuse a review from the owner; Moderation uses it for disputes.</summary>
public sealed class EntityOwnership(OwnershipRepository ownerships) : IEntityOwnership
{
    public Task<bool> IsOwnerAsync(string userId, string entityId, CancellationToken ct = default) => ownerships.ExistsAsync(userId, entityId, ct);
}

/// <summary>Hands the owner's replies to the Reviews module for the public list. A reply carries no reviewer data.</summary>
public sealed class OwnerReplyDirectory(OwnerReplyRepository replies) : IOwnerReplyDirectory
{
    public async Task<IReadOnlyDictionary<string, OwnerReplyInfo>> GetAsync(IReadOnlyCollection<string> reviewIds, CancellationToken ct = default) =>
        (await replies.ListVisibleForReviewsAsync(reviewIds, ct)).ToDictionary(r => r.ReviewId, r => new OwnerReplyInfo(r.Id, r.Text, r.CreatedAt, r.EditedAt));
}

public sealed class ClaimQueueCounter(ClaimRepository claims) : IQueueCounter
{
    public string Name => "claims";

    public Task<long> CountAsync(CancellationToken ct = default) => claims.CountPendingAsync(ct);
}

public enum ReplyFailure { None, ReviewNotFound, NotOwner, InvalidText, AlreadyReplied, NotFound }

public sealed record ReplyResult(ReplyFailure Failure, OwnerReply? Reply = null)
{
    public bool Ok => Failure == ReplyFailure.None;
}

/// <summary>
/// The owner's public reply under a review. Only the account that runs the review's place may reply, once per review. A reply can be edited and removed by its
/// owner. It is shown with an owner label and never says who the reviewer is.
/// </summary>
public sealed class OwnerReplyService(
    OwnerReplyRepository replies,
    OwnershipRepository ownerships,
    IEnumerable<IContentDirectory> directories,
    IOptions<BusinessesOptions> options,
    TimeProvider time)
{
    public async Task<ReplyResult> CreateAsync(string userId, string reviewId, string? text, CancellationToken ct = default)
    {
        var review = await directories.First(d => d.Kind == ContentKind.Review).GetAsync(reviewId, ct);
        if (review is not { IsPublic: true } || review.EntityId is null) return new(ReplyFailure.ReviewNotFound);
        if (!await ownerships.ExistsAsync(userId, review.EntityId, ct)) return new(ReplyFailure.NotOwner);
        var clean = Validate(text);
        if (clean is null) return new(ReplyFailure.InvalidText);

        var reply = new OwnerReply { ReviewId = review.Id, EntityId = review.EntityId, UserId = userId, Text = clean, CreatedAt = time.GetUtcNow().UtcDateTime };
        if (await replies.TryInsertAsync(reply, ct)) return new(ReplyFailure.None, reply);

        // A removed reply may be written again; a visible one may not.
        var existing = await replies.GetForReviewAsync(reviewId, ct);
        if (existing is not { Deleted: true }) return new(ReplyFailure.AlreadyReplied);
        existing.Deleted = false;
        existing.Text = clean;
        existing.UserId = userId;
        existing.EditedAt = reply.CreatedAt;
        await replies.ReplaceAsync(existing, ct);
        return new(ReplyFailure.None, existing);
    }

    public async Task<ReplyResult> EditAsync(string userId, string reviewId, string? text, CancellationToken ct = default)
    {
        var existing = await OwnReplyAsync(userId, reviewId, ct);
        if (existing is null) return new(ReplyFailure.NotFound);
        var clean = Validate(text);
        if (clean is null) return new(ReplyFailure.InvalidText);
        existing.Text = clean;
        existing.EditedAt = time.GetUtcNow().UtcDateTime;
        await replies.ReplaceAsync(existing, ct);
        return new(ReplyFailure.None, existing);
    }

    public async Task<ReplyResult> DeleteAsync(string userId, string reviewId, CancellationToken ct = default)
    {
        var existing = await OwnReplyAsync(userId, reviewId, ct);
        if (existing is null) return new(ReplyFailure.NotFound);
        existing.Deleted = true;
        existing.EditedAt = time.GetUtcNow().UtcDateTime;
        await replies.ReplaceAsync(existing, ct);
        return new(ReplyFailure.None, existing);
    }

    /// <summary>The reply, only while the account still runs the place.</summary>
    private async Task<OwnerReply?> OwnReplyAsync(string userId, string reviewId, CancellationToken ct)
    {
        var existing = await replies.GetForReviewAsync(reviewId, ct);
        if (existing is null or { Deleted: true } || !await ownerships.ExistsAsync(userId, existing.EntityId, ct)) return null;
        return existing;
    }

    private string? Validate(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) || t.Length < options.Value.MinReplyLength || t.Length > options.Value.MaxReplyLength ? null : t;
    }
}

public sealed record DashboardPlace(string EntityId, string CategoryId, LocalizedText Name, IReadOnlyDictionary<string, string> Slugs);

public sealed record DashboardKpis(int Reviews, int VerifiedReviews, double? Score, int? ReplyRatePercent);

public sealed record Dashboard(
    DashboardPlace Place, DashboardKpis Kpis, IReadOnlyList<MonthlyScore> Trend, double? CategoryAverage,
    IReadOnlyList<BusinessReviewItem> AwaitingReply, int NewReviews, DateTime LastSeenAt);

/// <summary>
/// The free dashboard. Every number comes from the same published and Verified reviews as the public score, so the dashboard and the page always agree. A reviewer's
/// identity is never part of it: an anonymous review has no author here, whoever asks.
/// </summary>
public sealed class DashboardService(
    OwnershipRepository ownerships,
    OwnerReplyRepository replies,
    IEntityCatalog catalog,
    IBusinessReviewSource reviews,
    IOptions<BusinessesOptions> options,
    TimeProvider time)
{
    public async Task<List<DashboardPlace>> PlacesAsync(string userId, CancellationToken ct = default)
    {
        var owned = await ownerships.ListByUserAsync(userId, ct);
        var entries = await catalog.GetActiveEntriesAsync([.. owned.Select(o => o.EntityId)], ct);
        return [.. entries.Select(e => new DashboardPlace(e.Id, e.CategoryId, e.Name, e.Slugs))];
    }

    public async Task<Dashboard?> GetAsync(string userId, string entityId, CancellationToken ct = default)
    {
        if (!await ownerships.ExistsAsync(userId, entityId, ct)) return null;
        var entry = (await catalog.GetActiveEntriesAsync([entityId], ct)).FirstOrDefault();
        var ownership = await ownerships.GetForEntityAsync(entityId, ct);
        if (entry is null || ownership is null) return null;

        var stats = await reviews.GetStatsAsync(entityId, entry.CategoryId, options.Value.TrendMonths, ct);
        var list = await reviews.ListAsync(entityId, 200, ct);
        var replied = await replies.ListRepliedReviewIdsAsync(entityId, ct);
        var waiting = list.Where(r => !replied.Contains(r.Id)).Take(50).ToList();
        var rate = list.Count == 0 ? (int?)null : (int)Math.Round(100.0 * list.Count(r => replied.Contains(r.Id)) / list.Count);
        var fresh = await reviews.CountCreatedSinceAsync(entityId, ownership.LastSeenAt, ct);

        return new Dashboard(
            new DashboardPlace(entry.Id, entry.CategoryId, entry.Name, entry.Slugs),
            new DashboardKpis(stats.PublishedReviews, stats.VerifiedReviews, stats.Score, rate),
            stats.Trend, stats.CategoryAverage, waiting, fresh, ownership.LastSeenAt);
    }

    public async Task<bool> MarkSeenAsync(string userId, string entityId, CancellationToken ct = default)
    {
        if (!await ownerships.ExistsAsync(userId, entityId, ct)) return false;
        await ownerships.MarkSeenAsync(entityId, time.GetUtcNow().UtcDateTime, ct);
        return true;
    }
}
