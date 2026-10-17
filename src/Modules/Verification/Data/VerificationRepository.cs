using MongoDB.Driver;
using Ratable.Modules.Verification.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Verification.Data;

public sealed class VerificationRepository(IMongoDatabase database) : MongoRepository<VerificationRecord>(database, Collections.Verifications)
{
    public Task InsertAsync(VerificationRecord record, CancellationToken ct = default) => Collection.InsertOneAsync(record, cancellationToken: ct);

    public async Task<VerificationRecord?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(v => v.Id == id).FirstOrDefaultAsync(ct);

    public Task ReplaceAsync(VerificationRecord record, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(v => v.Id == record.Id, record, cancellationToken: ct);

    public async Task<List<VerificationRecord>> ListForReviewAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(v => v.ReviewId == reviewId).ToListAsync(ct);

    /// <summary>Stored proof files that must be deleted now: decided with mode Delete, or kept past their expiry.</summary>
    public async Task<List<VerificationRecord>> ListDueForDeletionAsync(DateTime now, CancellationToken ct = default)
    {
        var f = Builders<VerificationRecord>.Filter;
        var decided = f.Nin(v => v.Result, [VerificationResult.Pending, VerificationResult.Deferred]);
        var filter = f.And(
            f.Ne(v => v.StorageKey, null),
            f.Eq(v => v.Retention.DeletedAt, null),
            f.Or(
                f.Lte(v => v.Retention.ExpiresAt, now),
                f.And(f.Eq(v => v.Retention.Mode, RetentionMode.Delete), decided)));
        return await Collection.Find(filter).ToListAsync(ct);
    }
}
