using Microsoft.Extensions.Logging;
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
/// The owner's public reply under a review. Only the account that runs the review's place may reply, once per review. Every reply and every edit goes through
/// moderation before the public sees it (the same pre-screen and queue as reviews). The owner can edit and remove it. It is shown with an owner label and never says
/// who the reviewer is.
/// </summary>
public sealed class OwnerReplyService(
    OwnerReplyRepository replies,
    OwnershipRepository ownerships,
    IEnumerable<IContentDirectory> directories,
    IModerationIntake moderation,
    IOptions<BusinessesOptions> options,
    TimeProvider time,
    ILogger<OwnerReplyService> logger)
{
    public async Task<ReplyResult> CreateAsync(string userId, string reviewId, string? text, CancellationToken ct = default)
    {
        var review = await directories.First(d => d.Kind == ContentKind.Review).GetAsync(reviewId, ct);
        if (review is not { IsPublic: true } || review.EntityId is null) return new(ReplyFailure.ReviewNotFound);
        if (!await ownerships.ExistsAsync(userId, review.EntityId, ct)) return new(ReplyFailure.NotOwner);
        var clean = Validate(text);
        if (clean is null) return new(ReplyFailure.InvalidText);

        var now = time.GetUtcNow().UtcDateTime;
        var reply = new OwnerReply { ReviewId = review.Id, EntityId = review.EntityId, UserId = userId, Text = clean, CreatedAt = now };
        if (await replies.TryInsertAsync(reply, ct)) return new(ReplyFailure.None, await ScreenAsync(reply, ct));

        // A removed reply may be written again (as a new version); a visible one may not.
        var existing = await replies.GetForReviewAsync(reviewId, ct);
        if (existing is not { Deleted: true }) return new(ReplyFailure.AlreadyReplied);
        existing.Deleted = false;
        existing.Text = clean;
        existing.UserId = userId;
        existing.EditedAt = now;
        existing.Revision++;
        existing.Status = ReplyStatus.PendingModeration;
        existing.ModerationReason = null;
        await replies.ReplaceAsync(existing, ct);
        return new(ReplyFailure.None, await ScreenAsync(existing, ct));
    }

    public async Task<ReplyResult> EditAsync(string userId, string reviewId, string? text, CancellationToken ct = default)
    {
        var existing = await OwnReplyAsync(userId, reviewId, ct);
        if (existing is null) return new(ReplyFailure.NotFound);
        var clean = Validate(text);
        if (clean is null) return new(ReplyFailure.InvalidText);
        existing.Text = clean;
        existing.EditedAt = time.GetUtcNow().UtcDateTime;
        existing.Revision++;
        existing.Status = ReplyStatus.PendingModeration; // the new version is read again before anyone sees it
        existing.ModerationReason = null;
        await replies.ReplaceAsync(existing, ct);
        return new(ReplyFailure.None, await ScreenAsync(existing, ct));
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

    /// <summary>
    /// Hands the version to the Moderation module (AI pre-screen and routing). It may publish it at once; the stored reply is read again so the caller sees its real state.
    /// If moderation cannot be reached the reply stays pending and the backfill queues it later.
    /// </summary>
    private async Task<OwnerReply> ScreenAsync(OwnerReply reply, CancellationToken ct)
    {
        try
        {
            await moderation.SubmitAsync(new ModerationSubmission(
                ContentKind.OwnerReply, reply.Id, reply.UserId, reply.EntityId, null, reply.Text, LanguageOf(reply.Text), reply.Revision, false, false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Moderation intake failed for owner reply {ReplyId}; it stays pending and will be queued by the backfill.", reply.Id);
        }
        return await replies.GetAsync(reply.Id, ct) ?? reply;
    }

    internal static string LanguageOf(string text) => text.Any(c => c is >= '\u0600' and <= '\u06FF') ? "ar" : "en";

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

/// <summary>How the Moderation module finds, publishes, hides and reads owner replies. The "author" it knows is the owner account, never the reviewer.</summary>
public sealed class OwnerReplyModerator(OwnerReplyRepository replies) : IContentModerator, IContentDirectory
{
    public ContentKind Kind => ContentKind.OwnerReply;

    public async Task<bool> ApplyAsync(string contentId, ModerationOutcome outcome, string? reason, CancellationToken ct = default)
    {
        var reply = await replies.GetAsync(contentId, ct);
        if (reply is null or { Deleted: true }) return false;
        reply.Status = outcome == ModerationOutcome.Publish ? ReplyStatus.Published : ReplyStatus.Hidden;
        reply.ModerationReason = outcome == ModerationOutcome.Hide ? reason : null;
        await replies.ReplaceAsync(reply, ct);
        return true;
    }

    public Task<bool> SetUnderReviewAsync(string contentId, bool underReview, CancellationToken ct = default) => Task.FromResult(false);

    public async Task<ModerationContentView?> GetViewAsync(string contentId, CancellationToken ct = default)
    {
        var r = await replies.GetAsync(contentId, ct);
        return r is null ? null : new ModerationContentView(
            r.Id, ContentKind.OwnerReply, r.Text, OwnerReplyService.LanguageOf(r.Text), r.Revision, null, r.EntityId, null, r.Status.ToString(), false, false, null, false, r.CreatedAt, r.EditedAt ?? r.CreatedAt);
    }

    public async Task<IReadOnlyList<PendingContent>> ListPendingAsync(int limit, CancellationToken ct = default) =>
        [.. (await replies.ListPendingAsync(limit, ct)).Select(p => new PendingContent(p.Id, p.Revision))];

    async Task<ContentInfo?> IContentDirectory.GetAsync(string id, CancellationToken ct)
    {
        var r = await replies.GetAsync(id, ct);
        return r is null ? null : new ContentInfo(r.Id, r.UserId, false, r.Status == ReplyStatus.Published && !r.Deleted, null, r.EntityId, r.Deleted);
    }
}

public sealed record DashboardPlace(string EntityId, string CategoryId, LocalizedText Name, IReadOnlyDictionary<string, string> Slugs);

public sealed record DashboardKpis(int Reviews, int VerifiedReviews, double? Score, int? ReplyRatePercent);

public sealed record Dashboard(
    DashboardPlace Place, DashboardKpis Kpis, IReadOnlyList<MonthlyScore> Trend, double? CategoryAverage,
    IReadOnlyList<BusinessReviewItem> AwaitingReply, int NewReviews, DateTime LastSeenAt, int RepliesInModeration,
    bool ContactGated, bool PlanShowsContact);

/// <summary>
/// The free dashboard. Every number comes from the same published and Verified reviews as the public score, so the dashboard and the page always agree. A reviewer's
/// identity is never part of it: an anonymous review has no author here, whoever asks.
/// </summary>
public sealed class DashboardService(
    OwnershipRepository ownerships,
    OwnerReplyRepository replies,
    IEntityCatalog catalog,
    IBusinessReviewSource reviews,
    IContactAccess contactAccess,
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
            stats.Trend, stats.CategoryAverage, waiting, fresh, ownership.LastSeenAt, (int)await replies.CountPendingForEntityAsync(entityId, ct),
            (await catalog.GetCategoryAsync(entry.CategoryId, ct))?.ContactGated ?? false, await contactAccess.PlanShowsContactAsync(entityId, ct));
    }

    public async Task<bool> MarkSeenAsync(string userId, string entityId, CancellationToken ct = default)
    {
        if (!await ownerships.ExistsAsync(userId, entityId, ct)) return false;
        await ownerships.MarkSeenAsync(entityId, time.GetUtcNow().UtcDateTime, ct);
        return true;
    }
}
