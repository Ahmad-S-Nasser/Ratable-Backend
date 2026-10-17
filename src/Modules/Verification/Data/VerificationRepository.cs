using MongoDB.Driver;
using Ratable.Modules.Verification.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Verification.Data;

public sealed class VerificationRepository(IMongoDatabase database) : MongoRepository<VerificationRecord>(database, Collections.Verifications)
{
    public Task InsertAsync(VerificationRecord record, CancellationToken ct = default) => Collection.InsertOneAsync(record, cancellationToken: ct);

    public async Task<VerificationRecord?> GetAsync(string id, CancellationToken ct = default) =>
        MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Collection.Find(v => v.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(VerificationRecord record, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(v => v.Id == record.Id, record, cancellationToken: ct);

    public async Task<List<VerificationRecord>> ListForReviewAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(v => v.ReviewId == reviewId).SortBy(v => v.CreatedAt).ToListAsync(ct);

    public async Task<List<VerificationRecord>> ListForUserAsync(string userId, string? reviewId, CancellationToken ct = default) =>
        await Collection.Find(v => v.UserId == userId && v.Type == VerificationType.Proof && (reviewId == null || v.ReviewId == reviewId))
            .SortByDescending(v => v.CreatedAt).Limit(100).ToListAsync(ct);

    public Task<long> CountProofsByUserSinceAsync(string userId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(v => v.UserId == userId && v.Type == VerificationType.Proof && v.CreatedAt >= since, cancellationToken: ct);

    /// <summary>Any other record with the same file hash: the same proof used again, on this account or another.</summary>
    public async Task<bool> HashUsedElsewhereAsync(string hash, string exceptId, CancellationToken ct = default) =>
        await Collection.CountDocumentsAsync(v => v.ProofHash == hash && v.Id != exceptId, cancellationToken: ct) > 0;

    /// <summary>Proofs waiting for a person: flagged ones, and approved ones picked for the random audit. Oldest first.</summary>
    public async Task<List<VerificationRecord>> ListQueueAsync(int limit = 100, CancellationToken ct = default) =>
        await Collection.Find(v => v.Type == VerificationType.Proof && (v.Result == VerificationResult.Flagged || v.AuditPending))
            .SortBy(v => v.CreatedAt).Limit(limit).ToListAsync(ct);

    /// <summary>
    /// Stored proof files that must be deleted now: a decided proof (approved or rejected, no audit still to do) whose author chose to delete,
    /// or any file past its expiry date. A proof still waiting for a person is never due.
    /// </summary>
    public async Task<List<VerificationRecord>> ListDueForDeletionAsync(DateTime now, CancellationToken ct = default)
    {
        var f = Builders<VerificationRecord>.Filter;
        var decided = f.And(f.In(v => v.Result, [VerificationResult.Approved, VerificationResult.Rejected]), f.Eq(v => v.AuditPending, false));
        var filter = f.And(
            f.Ne(v => v.StorageKey, null),
            f.Eq(v => v.Retention.DeletedAt, null),
            f.Or(
                f.Lte(v => v.Retention.ExpiresAt, now),
                f.And(f.Eq(v => v.Retention.Mode, RetentionMode.Delete), decided)));
        return await Collection.Find(filter).ToListAsync(ct);
    }

    public async Task<List<VerificationRecord>> ListWithFilesForReviewAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(v => v.ReviewId == reviewId && v.StorageKey != null).ToListAsync(ct);
}

public sealed class ConsentRepository(IMongoDatabase database) : MongoRepository<ConsentRecord>(database, Collections.Consents)
{
    public Task InsertAsync(ConsentRecord consent, CancellationToken ct = default) => Collection.InsertOneAsync(consent, cancellationToken: ct);

    public async Task<List<ConsentRecord>> ListForVerificationAsync(string verificationId, CancellationToken ct = default) =>
        await Collection.Find(c => c.VerificationId == verificationId).ToListAsync(ct);

    public Task WithdrawAsync(string verificationId, DateTime now, CancellationToken ct = default) =>
        Collection.UpdateManyAsync(c => c.VerificationId == verificationId && c.WithdrawnAt == null,
            Builders<ConsentRecord>.Update.Set(c => c.WithdrawnAt, now), cancellationToken: ct);
}
