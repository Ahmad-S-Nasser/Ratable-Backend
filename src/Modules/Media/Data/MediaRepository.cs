using MongoDB.Driver;
using Ratable.Modules.Media.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Media.Data;

public sealed class MediaRepository(IMongoDatabase database) : MongoRepository<MediaItem>(database, Collections.MediaItems)
{
    public Task InsertAsync(MediaItem item, CancellationToken ct = default) => Collection.InsertOneAsync(item, cancellationToken: ct);

    public async Task<MediaItem?> GetAsync(string id, CancellationToken ct = default) =>
        MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Collection.Find(m => m.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(MediaItem item, CancellationToken ct = default)
    {
        item.UpdatedAt = DateTime.UtcNow;
        return Collection.ReplaceOneAsync(m => m.Id == item.Id, item, cancellationToken: ct);
    }

    public async Task<List<MediaItem>> ListForReviewAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(m => m.ReviewId == reviewId).SortBy(m => m.CreatedAt).ToListAsync(ct);

    public async Task<List<MediaItem>> ListForUserAsync(string userId, string? reviewId, CancellationToken ct = default) =>
        await Collection.Find(m => m.UserId == userId && (reviewId == null || m.ReviewId == reviewId)).SortByDescending(m => m.CreatedAt).Limit(100).ToListAsync(ct);

    public async Task<List<MediaItem>> ListPublishedForReviewsAsync(IReadOnlyCollection<string> reviewIds, CancellationToken ct = default) =>
        await Collection.Find(m => reviewIds.Contains(m.ReviewId) && m.Status == MediaStatus.Published).SortBy(m => m.CreatedAt).ToListAsync(ct);

    public async Task<List<MediaItem>> ListPendingAsync(int limit, CancellationToken ct = default) =>
        await Collection.Find(m => m.Status == MediaStatus.PendingModeration).SortBy(m => m.CreatedAt).Limit(limit).ToListAsync(ct);

    public Task<long> CountPendingAsync(CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(m => m.Status == MediaStatus.PendingModeration, cancellationToken: ct);

    /// <summary>Counts the author's items of a kind that still count against the limit (not rejected, failed or removed).</summary>
    public Task<long> CountActiveAsync(string reviewId, MediaKind kind, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(m => m.ReviewId == reviewId && m.Kind == kind && m.Status != MediaStatus.Rejected && m.Status != MediaStatus.Failed, cancellationToken: ct);

    public Task<long> CountCreatedByUserSinceAsync(string userId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(m => m.UserId == userId && m.CreatedAt >= since, cancellationToken: ct);

    /// <summary>Takes one uploaded item for processing. Only one worker gets a given item.</summary>
    public async Task<MediaItem?> ClaimNextUploadedAsync(CancellationToken ct = default) =>
        await Collection.FindOneAndUpdateAsync(
            m => m.Status == MediaStatus.Uploaded && (m.NextAttemptAt == null || m.NextAttemptAt <= DateTime.UtcNow),
            Builders<MediaItem>.Update.Set(m => m.Status, MediaStatus.Processing).Set(m => m.UpdatedAt, DateTime.UtcNow),
            new FindOneAndUpdateOptions<MediaItem> { Sort = Builders<MediaItem>.Sort.Ascending(m => m.CreatedAt), ReturnDocument = ReturnDocument.After }, ct);

    /// <summary>Puts items that were being processed when the service stopped back in line.</summary>
    public Task RequeueStuckAsync(DateTime olderThan, CancellationToken ct = default) =>
        Collection.UpdateManyAsync(m => m.Status == MediaStatus.Processing && m.UpdatedAt < olderThan,
            Builders<MediaItem>.Update.Set(m => m.Status, MediaStatus.Uploaded), cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct = default) => Collection.DeleteOneAsync(m => m.Id == id, ct);

    public async Task<List<MediaItem>> ListPublishedLatestVideosAsync(int limit, int skip, CancellationToken ct = default) =>
        await Collection.Find(m => m.Kind == MediaKind.Video && m.Status == MediaStatus.Published).SortByDescending(m => m.DecidedAt).Skip(skip).Limit(limit).ToListAsync(ct);
}

public sealed class MediaIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var c = database.GetCollection<MediaItem>(Collections.MediaItems);
        var k = Builders<MediaItem>.IndexKeys;
        await c.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<MediaItem>(k.Ascending(m => m.ReviewId), new CreateIndexOptions { Name = "review" }),
            new CreateIndexModel<MediaItem>(k.Ascending(m => m.UserId).Descending(m => m.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
            new CreateIndexModel<MediaItem>(k.Ascending(m => m.Status).Ascending(m => m.CreatedAt), new CreateIndexOptions { Name = "queue" }),
            new CreateIndexModel<MediaItem>(k.Ascending(m => m.Kind).Ascending(m => m.Status).Descending(m => m.DecidedAt), new CreateIndexOptions { Name = "feed" }),
        ], ct);
    }
}
