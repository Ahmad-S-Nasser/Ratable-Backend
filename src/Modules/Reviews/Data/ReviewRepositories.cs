using MongoDB.Driver;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Reviews.Data;

public sealed class ReviewRepository(IMongoDatabase database) : MongoRepository<Review>(database, Collections.Reviews)
{
    /// <summary>Throws a MongoWriteException (DuplicateKey) when the user already reviewed this entity.</summary>
    public Task InsertAsync(Review review, CancellationToken ct = default) => Collection.InsertOneAsync(review, cancellationToken: ct);

    public async Task<Review?> GetAsync(string id, CancellationToken ct = default) =>
        MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct) : null;

    public async Task<Review?> GetByUserAndEntityAsync(string userId, string entityId, CancellationToken ct = default) =>
        await Collection.Find(r => r.UserId == userId && r.EntityId == entityId).FirstOrDefaultAsync(ct);

    public Task ReplaceAsync(Review review, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(r => r.Id == review.Id, review, cancellationToken: ct);

    /// <summary>
    /// Replaces the review only if nobody else changed it since <paramref name="expectedRevision"/> was read.
    /// Returns false when someone did, so two edits at once cannot overwrite each other.
    /// </summary>
    public async Task<bool> TryReplaceAsync(Review review, int expectedRevision, CancellationToken ct = default)
    {
        var result = await Collection.ReplaceOneAsync(r => r.Id == review.Id && r.RevisionNumber == expectedRevision, review, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    /// <summary>Published reviews of an entity, newest first, for the public list.</summary>
    public async Task<List<Review>> ListPublishedAsync(string entityId, int limit = 50, int skip = 0, CancellationToken ct = default) =>
        await Collection.Find(r => r.EntityId == entityId && r.Status == ReviewStatus.Published)
            .SortByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(skip).Limit(limit).ToListAsync(ct);

    /// <summary>Only the fields the score needs, for the reviews that may count (Published and Verified).</summary>
    public async Task<List<Review>> ListScoringAsync(string entityId, CancellationToken ct = default) =>
        await Collection.Find(r => r.EntityId == entityId && r.Status == ReviewStatus.Published && r.Verification.Level == VerificationLevel.Verified)
            .Project<Review>(Builders<Review>.Projection.Include(r => r.Answers).Include(r => r.Status).Include(r => r.Verification))
            .ToListAsync(ct);

    public Task<long> CountPublishedAsync(string entityId, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.EntityId == entityId && r.Status == ReviewStatus.Published, cancellationToken: ct);

    public async Task<List<Review>> ListByUserAsync(string userId, string? entityId, CancellationToken ct = default) =>
        await Collection.Find(r => r.UserId == userId && (entityId == null || r.EntityId == entityId)).SortByDescending(r => r.CreatedAt).Limit(100).ToListAsync(ct);
}

/// <summary>Revisions are append-only: this repository can insert and read, nothing else.</summary>
public sealed class ReviewRevisionRepository(IMongoDatabase database) : MongoRepository<ReviewRevision>(database, Collections.ReviewRevisions)
{
    public Task AppendAsync(ReviewRevision revision, CancellationToken ct = default) => Collection.InsertOneAsync(revision, cancellationToken: ct);

    public async Task<List<ReviewRevision>> ListAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(r => r.ReviewId == reviewId).SortBy(r => r.RevisionNumber).ToListAsync(ct);

    public async Task<bool> ExistsAsync(string reviewId, int revisionNumber, CancellationToken ct = default) =>
        await Collection.CountDocumentsAsync(r => r.ReviewId == reviewId && r.RevisionNumber == revisionNumber, cancellationToken: ct) > 0;
}
