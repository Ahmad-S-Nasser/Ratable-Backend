using MongoDB.Driver;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Reviews.Data;

public sealed class ReviewsIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var reviews = database.GetCollection<Review>(Collections.Reviews);
        var k = Builders<Review>.IndexKeys;
        await reviews.Indexes.CreateManyAsync(
        [
            // One review per account per entity (rule 4). Deleted reviews stay in this index, so a user edits or restores
            // the existing review instead of posting a second one.
            new CreateIndexModel<Review>(k.Ascending(r => r.EntityId).Ascending(r => r.UserId),
                new CreateIndexOptions { Name = "one_review_per_user_per_entity", Unique = true }),
            new CreateIndexModel<Review>(
                k.Ascending(r => r.EntityId).Ascending(r => r.Status).Ascending("verification.level").Descending(r => r.CreatedAt),
                new CreateIndexOptions { Name = "entity_status_level_created" }),
            new CreateIndexModel<Review>(k.Ascending(r => r.UserId).Descending(r => r.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
        ], ct);

        var revisions = database.GetCollection<ReviewRevision>(Collections.ReviewRevisions);
        await revisions.Indexes.CreateOneAsync(
            new CreateIndexModel<ReviewRevision>(
                Builders<ReviewRevision>.IndexKeys.Ascending(r => r.ReviewId).Ascending(r => r.RevisionNumber),
                new CreateIndexOptions { Name = "review_revision_number", Unique = true }), cancellationToken: ct);
    }
}
