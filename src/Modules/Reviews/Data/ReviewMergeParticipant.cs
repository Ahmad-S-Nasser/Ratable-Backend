using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;

namespace Ratable.Modules.Reviews.Data;

/// <summary>
/// Moves the reviews of a merged-away entity onto the surviving one. One review per account per entity (rule 4) still holds after the move,
/// so a merge is refused while the same account has reviewed both; a moderator resolves that by hand first.
/// </summary>
public sealed class ReviewMergeParticipant(IMongoDatabase database) : IEntityMergeParticipant
{
    private readonly IMongoCollection<Review> _reviews = database.GetCollection<Review>(Collections.Reviews);

    public async Task<IReadOnlyList<MergeConflict>> FindConflictsAsync(string sourceEntityId, string targetEntityId, CancellationToken ct = default)
    {
        var sourceUsers = await _reviews.Distinct(r => r.UserId, r => r.EntityId == sourceEntityId, cancellationToken: ct).ToListAsync(ct);
        if (sourceUsers.Count == 0) return [];
        var both = await _reviews.CountDocumentsAsync(r => r.EntityId == targetEntityId && sourceUsers.Contains(r.UserId), cancellationToken: ct);
        return both == 0 ? [] : [new MergeConflict("Reviews", "same_account_reviewed_both", (int)both)];
    }

    public async Task MoveAsync(string sourceEntityId, string targetEntityId, CancellationToken ct = default) =>
        await _reviews.UpdateManyAsync(r => r.EntityId == sourceEntityId,
            Builders<Review>.Update.Set(r => r.EntityId, targetEntityId), cancellationToken: ct);
}
