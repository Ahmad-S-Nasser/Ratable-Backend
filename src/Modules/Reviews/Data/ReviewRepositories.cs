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
        await ListPublishedAsync(entityId, limit, skip, null, false, ct);

    /// <summary>Published reviews, optionally only Verified ones, newest first (default), or by their own overall score highest or lowest first.</summary>
    public async Task<List<Review>> ListPublishedAsync(string entityId, int limit, int skip, string? sort, bool verifiedOnly, CancellationToken ct = default)
    {
        var find = Collection.Find(r => r.EntityId == entityId && r.Status == ReviewStatus.Published && (!verifiedOnly || r.Verification.Level == VerificationLevel.Verified));
        if (sort is "highest" or "lowest")
        {
            // The overall of a review is the average of its answers, worked out here because it is not stored.
            var all = await find.ToListAsync(ct);
            var ordered = sort == "highest"
                ? all.OrderByDescending(r => Scoring.ScoreCalculator.ReviewOverall(r.Answers) ?? 0).ThenByDescending(r => r.CreatedAt)
                : all.OrderBy(r => Scoring.ScoreCalculator.ReviewOverall(r.Answers) ?? 0).ThenByDescending(r => r.CreatedAt);
            return [.. ordered.Skip(skip).Take(limit)];
        }
        return await find.SortByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(skip).Limit(limit).ToListAsync(ct);
    }

    /// <summary>Only the fields the score needs, for the reviews that may count (Published and Verified).</summary>
    public async Task<List<Review>> ListScoringAsync(string entityId, CancellationToken ct = default) =>
        await Collection.Find(r => r.EntityId == entityId && r.Status == ReviewStatus.Published && r.Verification.Level == VerificationLevel.Verified)
            .Project<Review>(Builders<Review>.Projection.Include(r => r.Answers).Include(r => r.Status).Include(r => r.Verification).Include(r => r.Verdict))
            .ToListAsync(ct);

    /// <summary>The answers of every counting review (Published and Verified) of several entities at once, for listings.</summary>
    public async Task<List<Review>> ListScoringForEntitiesAsync(IReadOnlyCollection<string> entityIds, CancellationToken ct = default) =>
        await Collection.Find(r => entityIds.Contains(r.EntityId) && r.Status == ReviewStatus.Published && r.Verification.Level == VerificationLevel.Verified)
            .Project<Review>(Builders<Review>.Projection.Include(r => r.EntityId).Include(r => r.Answers).Include(r => r.Status).Include(r => r.Verification).Include(r => r.Verdict).Include(r => r.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>The entities that have at least one counting review, in id order, with the time of their latest review change. For the sitemap.</summary>
    public async Task<List<(string EntityId, DateTime LastChange)>> ListVerifiedEntitiesAsync(int skip, int limit, CancellationToken ct = default)
    {
        var groups = await Collection.Aggregate()
            .Match(r => r.Status == ReviewStatus.Published && r.Verification.Level == VerificationLevel.Verified)
            .Group(r => r.EntityId, g => new { EntityId = g.Key, LastChange = g.Max(r => r.UpdatedAt) })
            .SortBy(g => g.EntityId).Skip(skip).Limit(limit)
            .ToListAsync(ct);
        return [.. groups.Select(g => (g.EntityId, g.LastChange))];
    }

    /// <summary>The entities whose reviews changed since a moment (published, edited, hidden, or verification changed).</summary>
    public async Task<List<string>> ListEntityIdsChangedSinceAsync(DateTime since, int limit, CancellationToken ct = default)
    {
        var ids = await Collection.Find(r => r.UpdatedAt > since).Project(r => r.EntityId).ToListAsync(ct);
        return [.. ids.Distinct().Take(limit)];
    }

    public Task<long> CountPublishedAsync(string entityId, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.EntityId == entityId && r.Status == ReviewStatus.Published, cancellationToken: ct);

    /// <summary>Reviews waiting for moderation, oldest first. Only what is needed to make sure each has a case in the queue.</summary>
    public async Task<IReadOnlyList<Ratable.Shared.Moderation.PendingContent>> ListPendingAsync(int limit, CancellationToken ct = default) =>
        [.. (await Collection.Find(r => r.Status == ReviewStatus.PendingModeration).SortBy(r => r.UpdatedAt).Limit(limit)
            .Project(r => new { r.Id, r.RevisionNumber }).ToListAsync(ct)).Select(x => new Ratable.Shared.Moderation.PendingContent(x.Id, x.RevisionNumber))];

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
