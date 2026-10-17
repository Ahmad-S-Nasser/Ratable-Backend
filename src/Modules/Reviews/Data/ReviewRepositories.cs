using MongoDB.Driver;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Reviews.Data;

public sealed class ReviewRepository(IMongoDatabase database) : MongoRepository<Review>(database, Collections.Reviews)
{
    /// <summary>Throws a MongoWriteException (DuplicateKey) when the user already reviewed this entity.</summary>
    public Task InsertAsync(Review review, CancellationToken ct = default) => Collection.InsertOneAsync(review, cancellationToken: ct);

    public async Task<Review?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct);

    public async Task<Review?> GetByUserAndEntityAsync(string userId, string entityId, CancellationToken ct = default) =>
        await Collection.Find(r => r.UserId == userId && r.EntityId == entityId).FirstOrDefaultAsync(ct);

    public Task ReplaceAsync(Review review, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(r => r.Id == review.Id, review, cancellationToken: ct);

    /// <summary>Published reviews of an entity, newest first. Scores use only Verified ones; callers filter on level.</summary>
    public async Task<List<Review>> ListPublishedAsync(string entityId, int limit = 50, CancellationToken ct = default) =>
        await Collection.Find(r => r.EntityId == entityId && r.Status == ReviewStatus.Published)
            .SortByDescending(r => r.CreatedAt).Limit(limit).ToListAsync(ct);
}

/// <summary>Revisions are append-only: this repository can insert and read, nothing else.</summary>
public sealed class ReviewRevisionRepository(IMongoDatabase database) : MongoRepository<ReviewRevision>(database, Collections.ReviewRevisions)
{
    public Task AppendAsync(ReviewRevision revision, CancellationToken ct = default) => Collection.InsertOneAsync(revision, cancellationToken: ct);

    public async Task<List<ReviewRevision>> ListAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(r => r.ReviewId == reviewId).SortBy(r => r.RevisionNumber).ToListAsync(ct);
}
