using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Businesses.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Businesses.Data;

public sealed class ClaimRepository(IMongoDatabase database) : MongoRepository<BusinessClaim>(database, Collections.BusinessClaims)
{
    public Task InsertAsync(BusinessClaim claim, CancellationToken ct = default) => Collection.InsertOneAsync(claim, cancellationToken: ct);

    public async Task<BusinessClaim?> GetAsync(string id, CancellationToken ct = default) =>
        ObjectId.TryParse(id, out _) ? await Collection.Find(c => c.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(BusinessClaim claim, CancellationToken ct = default) => Collection.ReplaceOneAsync(c => c.Id == claim.Id, claim, cancellationToken: ct);

    public async Task<List<BusinessClaim>> ListByUserAsync(string userId, CancellationToken ct = default) =>
        await Collection.Find(c => c.UserId == userId).SortByDescending(c => c.CreatedAt).Limit(50).ToListAsync(ct);

    public Task<long> CountOpenAsync(string userId, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(c => c.UserId == userId && (c.Status == ClaimStatus.Draft || c.Status == ClaimStatus.Pending), cancellationToken: ct);

    public Task<long> CountSinceAsync(string userId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(c => c.UserId == userId && c.CreatedAt >= since, cancellationToken: ct);

    public Task<bool> HasOpenForEntityAsync(string userId, string entityId, CancellationToken ct = default) =>
        Collection.Find(c => c.UserId == userId && c.EntityId == entityId && (c.Status == ClaimStatus.Draft || c.Status == ClaimStatus.Pending)).AnyAsync(ct);

    public async Task<List<BusinessClaim>> ListPendingAsync(int limit, CancellationToken ct = default) =>
        await Collection.Find(c => c.Status == ClaimStatus.Pending).SortBy(c => c.CreatedAt).Limit(limit).ToListAsync(ct);

    public Task<long> CountPendingAsync(CancellationToken ct = default) => Collection.CountDocumentsAsync(c => c.Status == ClaimStatus.Pending, cancellationToken: ct);
}

public sealed class OwnershipRepository(IMongoDatabase database) : MongoRepository<BusinessOwnership>(database, Collections.BusinessOwnerships)
{
    /// <summary>False when the place already has an owner (the unique index on the place).</summary>
    public async Task<bool> TryInsertAsync(BusinessOwnership ownership, CancellationToken ct = default)
    {
        try
        {
            await Collection.InsertOneAsync(ownership, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<BusinessOwnership?> GetForEntityAsync(string entityId, CancellationToken ct = default) =>
        ObjectId.TryParse(entityId, out _) ? await Collection.Find(o => o.EntityId == entityId).FirstOrDefaultAsync(ct) : null;

    public Task<bool> ExistsAsync(string userId, string entityId, CancellationToken ct = default) =>
        ObjectId.TryParse(userId, out _) && ObjectId.TryParse(entityId, out _)
            ? Collection.Find(o => o.UserId == userId && o.EntityId == entityId).AnyAsync(ct)
            : Task.FromResult(false);

    public async Task<List<BusinessOwnership>> ListByUserAsync(string userId, CancellationToken ct = default) =>
        ObjectId.TryParse(userId, out _) ? await Collection.Find(o => o.UserId == userId).SortBy(o => o.ApprovedAt).ToListAsync(ct) : [];

    public Task<long> CountByUserAsync(string userId, CancellationToken ct = default) => Collection.CountDocumentsAsync(o => o.UserId == userId, cancellationToken: ct);

    public Task MarkSeenAsync(string entityId, DateTime at, CancellationToken ct = default) =>
        Collection.UpdateOneAsync(o => o.EntityId == entityId, Builders<BusinessOwnership>.Update.Set(o => o.LastSeenAt, at), cancellationToken: ct);

    public Task DeleteForEntityAsync(string entityId, CancellationToken ct = default) => Collection.DeleteOneAsync(o => o.EntityId == entityId, ct);
}

public sealed class OwnerReplyRepository(IMongoDatabase database) : MongoRepository<OwnerReply>(database, Collections.OwnerReplies)
{
    public async Task<bool> TryInsertAsync(OwnerReply reply, CancellationToken ct = default)
    {
        try
        {
            await Collection.InsertOneAsync(reply, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<OwnerReply?> GetForReviewAsync(string reviewId, CancellationToken ct = default) =>
        ObjectId.TryParse(reviewId, out _) ? await Collection.Find(r => r.ReviewId == reviewId).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(OwnerReply reply, CancellationToken ct = default) => Collection.ReplaceOneAsync(r => r.Id == reply.Id, reply, cancellationToken: ct);

    public async Task<List<OwnerReply>> ListVisibleForReviewsAsync(IReadOnlyCollection<string> reviewIds, CancellationToken ct = default) =>
        reviewIds.Count == 0 ? [] : await Collection.Find(r => reviewIds.Contains(r.ReviewId) && !r.Deleted).ToListAsync(ct);

    public async Task<HashSet<string>> ListRepliedReviewIdsAsync(string entityId, CancellationToken ct = default) =>
        [.. await Collection.Find(r => r.EntityId == entityId && !r.Deleted).Project(r => r.ReviewId).ToListAsync(ct)];
}

public sealed class BusinessesIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var claims = database.GetCollection<BusinessClaim>(Collections.BusinessClaims);
        var ck = Builders<BusinessClaim>.IndexKeys;
        await claims.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BusinessClaim>(ck.Ascending(c => c.UserId).Descending(c => c.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
            new CreateIndexModel<BusinessClaim>(ck.Ascending(c => c.Status).Ascending(c => c.CreatedAt), new CreateIndexOptions { Name = "status_created" }),
        ], ct);

        var owners = database.GetCollection<BusinessOwnership>(Collections.BusinessOwnerships);
        var ok = Builders<BusinessOwnership>.IndexKeys;
        await owners.Indexes.CreateManyAsync(
        [
            // One account runs a place; the database refuses a second owner.
            new CreateIndexModel<BusinessOwnership>(ok.Ascending(o => o.EntityId), new CreateIndexOptions { Name = "entity_unique", Unique = true }),
            new CreateIndexModel<BusinessOwnership>(ok.Ascending(o => o.UserId), new CreateIndexOptions { Name = "user" }),
        ], ct);

        var replies = database.GetCollection<OwnerReply>(Collections.OwnerReplies);
        var rk = Builders<OwnerReply>.IndexKeys;
        await replies.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<OwnerReply>(rk.Ascending(r => r.ReviewId), new CreateIndexOptions { Name = "review_unique", Unique = true }),
            new CreateIndexModel<OwnerReply>(rk.Ascending(r => r.EntityId), new CreateIndexOptions { Name = "entity" }),
        ], ct);
    }
}
