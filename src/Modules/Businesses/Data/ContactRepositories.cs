using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Businesses.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Businesses.Data;

public sealed class SubscriptionRepository(IMongoDatabase database) : MongoRepository<BusinessSubscription>(database, Collections.BusinessSubscriptions)
{
    public Task InsertAsync(BusinessSubscription s, CancellationToken ct = default) => Collection.InsertOneAsync(s, cancellationToken: ct);

    public Task ReplaceAsync(BusinessSubscription s, CancellationToken ct = default) => Collection.ReplaceOneAsync(x => x.Id == s.Id, s, cancellationToken: ct);

    /// <summary>The active, unexpired subscriptions of the given places.</summary>
    public async Task<List<BusinessSubscription>> ListActiveAsync(IReadOnlyCollection<string> entityIds, DateTime now, CancellationToken ct = default) =>
        entityIds.Count == 0 ? [] : await Collection.Find(s => entityIds.Contains(s.EntityId) && s.Status == SubscriptionStatus.Active && (s.ValidUntil == null || s.ValidUntil > now)).ToListAsync(ct);

    public async Task<List<BusinessSubscription>> ListForEntityAsync(string entityId, CancellationToken ct = default) =>
        ObjectId.TryParse(entityId, out _) ? await Collection.Find(s => s.EntityId == entityId && s.Status == SubscriptionStatus.Active).ToListAsync(ct) : [];
}

public sealed class ContactRequestRepository(IMongoDatabase database) : MongoRepository<ContactRequest>(database, Collections.ContactRequests)
{
    public Task InsertAsync(ContactRequest r, CancellationToken ct = default) => Collection.InsertOneAsync(r, cancellationToken: ct);

    public async Task<ContactRequest?> GetAsync(string id, CancellationToken ct = default) =>
        ObjectId.TryParse(id, out _) ? await Collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(ContactRequest r, CancellationToken ct = default) => Collection.ReplaceOneAsync(x => x.Id == r.Id, r, cancellationToken: ct);

    public Task<long> CountByReaderSinceAsync(string readerId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.ReaderId == readerId && r.CreatedAt >= since, cancellationToken: ct);

    public Task<long> CountByEntitySinceAsync(string entityId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.EntityId == entityId && r.CreatedAt >= since, cancellationToken: ct);

    public Task<bool> HasOpenAsync(string readerId, string entityId, DateTime now, CancellationToken ct = default) =>
        Collection.Find(r => r.ReaderId == readerId && r.EntityId == entityId && r.Status == RequestStatus.Open && r.ExpiresAt > now).AnyAsync(ct);

    public async Task<List<ContactRequest>> ListByReaderAsync(string readerId, CancellationToken ct = default) =>
        await Collection.Find(r => r.ReaderId == readerId).SortByDescending(r => r.CreatedAt).Limit(50).ToListAsync(ct);

    public async Task<List<ContactRequest>> ListByEntityAsync(string entityId, CancellationToken ct = default) =>
        ObjectId.TryParse(entityId, out _) ? await Collection.Find(r => r.EntityId == entityId).SortByDescending(r => r.CreatedAt).Limit(100).ToListAsync(ct) : [];

    /// <summary>Requests still waiting for an answer.</summary>
    public Task<long> CountOpenAsync(string entityId, DateTime now, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.EntityId == entityId && r.Status == RequestStatus.Open && r.ExpiresAt > now, cancellationToken: ct);

    /// <summary>Requests that closed without an answer since the given moment.</summary>
    public Task<long> CountClosedUnansweredSinceAsync(string entityId, DateTime since, DateTime now, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.EntityId == entityId && r.RepliedAt == null && r.ExpiresAt <= now && r.ExpiresAt >= since, cancellationToken: ct);

    public Task<long> CountSinceAsync(string entityId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.EntityId == entityId && r.CreatedAt >= since, cancellationToken: ct);

    /// <summary>Marks every open request whose time is up as closed. Returns how many.</summary>
    public async Task<long> CloseExpiredAsync(DateTime now, CancellationToken ct = default) =>
        (await Collection.UpdateManyAsync(r => r.Status == RequestStatus.Open && r.ExpiresAt <= now,
            Builders<ContactRequest>.Update.Set(r => r.Status, RequestStatus.Closed).Set(r => r.ClosedAt, now), cancellationToken: ct)).ModifiedCount;
}

public sealed class NudgeRepository(IMongoDatabase database) : MongoRepository<NudgeInstance>(database, Collections.BusinessNudges)
{
    public Task InsertAsync(NudgeInstance n, CancellationToken ct = default) => Collection.InsertOneAsync(n, cancellationToken: ct);

    public async Task<NudgeInstance?> LatestAsync(string entityId, string trigger, CancellationToken ct = default) =>
        await Collection.Find(n => n.EntityId == entityId && n.Trigger == trigger).SortByDescending(n => n.CreatedAt).FirstOrDefaultAsync(ct);

    public Task ReplaceAsync(NudgeInstance n, CancellationToken ct = default) => Collection.ReplaceOneAsync(x => x.Id == n.Id, n, cancellationToken: ct);
}

public sealed class ContactIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var subs = database.GetCollection<BusinessSubscription>(Collections.BusinessSubscriptions);
        await subs.Indexes.CreateOneAsync(new CreateIndexModel<BusinessSubscription>(Builders<BusinessSubscription>.IndexKeys.Ascending(s => s.EntityId).Ascending(s => s.Status), new CreateIndexOptions { Name = "entity_status" }), cancellationToken: ct);

        var requests = database.GetCollection<ContactRequest>(Collections.ContactRequests);
        var rk = Builders<ContactRequest>.IndexKeys;
        await requests.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ContactRequest>(rk.Ascending(r => r.EntityId).Descending(r => r.CreatedAt), new CreateIndexOptions { Name = "entity_created" }),
            new CreateIndexModel<ContactRequest>(rk.Ascending(r => r.ReaderId).Descending(r => r.CreatedAt), new CreateIndexOptions { Name = "reader_created" }),
            new CreateIndexModel<ContactRequest>(rk.Ascending(r => r.Status).Ascending(r => r.ExpiresAt), new CreateIndexOptions { Name = "status_expires" }),
        ], ct);

        var nudges = database.GetCollection<NudgeInstance>(Collections.BusinessNudges);
        await nudges.Indexes.CreateOneAsync(new CreateIndexModel<NudgeInstance>(Builders<NudgeInstance>.IndexKeys.Ascending(n => n.EntityId).Ascending(n => n.Trigger).Descending(n => n.CreatedAt), new CreateIndexOptions { Name = "entity_trigger" }), cancellationToken: ct);
    }
}
