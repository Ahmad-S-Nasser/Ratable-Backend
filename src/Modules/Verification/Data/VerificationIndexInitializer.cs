using MongoDB.Driver;
using Ratable.Modules.Verification.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Verification.Data;

public sealed class VerificationIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var c = database.GetCollection<VerificationRecord>(Collections.Verifications);
        var k = Builders<VerificationRecord>.IndexKeys;
        await c.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<VerificationRecord>(k.Ascending(v => v.ReviewId), new CreateIndexOptions { Name = "review" }),
            new CreateIndexModel<VerificationRecord>(k.Ascending(v => v.UserId).Descending(v => v.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
            new CreateIndexModel<VerificationRecord>(k.Ascending("retention.expiresAt"), new CreateIndexOptions { Name = "retention_expiry" }),
            new CreateIndexModel<VerificationRecord>(k.Ascending(v => v.Result).Ascending(v => v.CreatedAt), new CreateIndexOptions { Name = "queue" }),
            // Finding the same proof used again.
            new CreateIndexModel<VerificationRecord>(k.Ascending(v => v.ProofHash), new CreateIndexOptions { Name = "proof_hash" }),
        ], ct);

        var consents = database.GetCollection<ConsentRecord>(Collections.Consents);
        await consents.Indexes.CreateOneAsync(
            new CreateIndexModel<ConsentRecord>(Builders<ConsentRecord>.IndexKeys.Ascending(x => x.VerificationId), new CreateIndexOptions { Name = "verification" }),
            cancellationToken: ct);
    }
}
