using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Ratable.Shared.Data;

namespace Ratable.Modules.Identity.PhoneVerification;

/// <summary>How a phone number is proven: the person sends a WhatsApp message (no per-message cost), or we send them a code (small per-message cost).</summary>
public enum VerifyRoute { Reverse, Code }

/// <summary>What kind of number it is, from the number-type check. Virtual and temporary numbers are blocked (or asked for extra proof).</summary>
public enum NumberType { Unknown, Mobile, Landline, Virtual, Temporary }

/// <summary>
/// One attempt to verify a phone number. The code is never stored, only its keyed hash. A code works once (it is consumed atomically), expires after
/// ten minutes and has a limited number of wrong tries. The phone number itself is kept only as a hash here.
/// </summary>
public sealed class PhoneVerification
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    public string PhoneHash { get; set; } = "";

    /// <summary>The encrypted number, kept only until the verification finishes (it becomes the account's number if it has none). Never returned by an API.</summary>
    public string PhoneEncrypted { get; set; } = "";

    public VerifyRoute Route { get; set; }
    public string CodeHash { get; set; } = "";
    public int Attempts { get; set; }
    public string? DeviceHash { get; set; }
    public string? IpHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public sealed class PhoneVerificationRepository(IMongoDatabase database) : MongoRepository<PhoneVerification>(database, Collections.PhoneVerifications)
{
    public Task InsertAsync(PhoneVerification v, CancellationToken ct = default) => Collection.InsertOneAsync(v, cancellationToken: ct);

    public async Task<PhoneVerification?> GetLatestOpenAsync(string userId, VerifyRoute route, DateTime now, CancellationToken ct = default) =>
        await Collection.Find(v => v.UserId == userId && v.Route == route && v.ConsumedAt == null && v.ExpiresAt > now).SortByDescending(v => v.CreatedAt).FirstOrDefaultAsync(ct);

    public async Task<PhoneVerification?> GetLatestAsync(string userId, CancellationToken ct = default) =>
        await Collection.Find(v => v.UserId == userId).SortByDescending(v => v.CreatedAt).FirstOrDefaultAsync(ct);

    public async Task<PhoneVerification?> FindOpenByCodeAsync(string codeHash, VerifyRoute route, DateTime now, CancellationToken ct = default) =>
        await Collection.Find(v => v.CodeHash == codeHash && v.Route == route && v.ConsumedAt == null && v.ExpiresAt > now).FirstOrDefaultAsync(ct);

    /// <summary>Uses the code up. Only one caller wins, however many arrive at once, so a code can never verify twice.</summary>
    public async Task<PhoneVerification?> ConsumeAsync(string id, DateTime now, CancellationToken ct = default) =>
        await Collection.FindOneAndUpdateAsync(v => v.Id == id && v.ConsumedAt == null && v.ExpiresAt > now, Builders<PhoneVerification>.Update.Set(v => v.ConsumedAt, now),
            new FindOneAndUpdateOptions<PhoneVerification> { ReturnDocument = ReturnDocument.After }, ct);

    public Task AddAttemptAsync(string id, CancellationToken ct = default) =>
        Collection.UpdateOneAsync(v => v.Id == id, Builders<PhoneVerification>.Update.Inc(v => v.Attempts, 1), cancellationToken: ct);

    public Task<long> CountForPhoneSinceAsync(string phoneHash, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(v => v.PhoneHash == phoneHash && v.CreatedAt >= since, cancellationToken: ct);

    public Task<long> CountForDeviceSinceAsync(string deviceHash, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(v => v.DeviceHash == deviceHash && v.CreatedAt >= since, cancellationToken: ct);

    public Task<long> CountForIpSinceAsync(string ipHash, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(v => v.IpHash == ipHash && v.CreatedAt >= since, cancellationToken: ct);

    /// <summary>Removes the encrypted number once the verification is finished or has expired; only the hash stays.</summary>
    public Task ClearPhoneAsync(string id, CancellationToken ct = default) =>
        Collection.UpdateOneAsync(v => v.Id == id, Builders<PhoneVerification>.Update.Set(v => v.PhoneEncrypted, ""), cancellationToken: ct);
}

public sealed class PhoneVerificationIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var c = database.GetCollection<PhoneVerification>(Collections.PhoneVerifications);
        var k = Builders<PhoneVerification>.IndexKeys;
        await c.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<PhoneVerification>(k.Ascending(v => v.CodeHash), new CreateIndexOptions { Name = "code_hash" }),
            new CreateIndexModel<PhoneVerification>(k.Ascending(v => v.UserId).Descending(v => v.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
            new CreateIndexModel<PhoneVerification>(k.Ascending(v => v.PhoneHash).Descending(v => v.CreatedAt), new CreateIndexOptions { Name = "phone_created" }),
            new CreateIndexModel<PhoneVerification>(k.Ascending(v => v.DeviceHash).Descending(v => v.CreatedAt), new CreateIndexOptions { Name = "device_created" }),
            new CreateIndexModel<PhoneVerification>(k.Ascending(v => v.IpHash).Descending(v => v.CreatedAt), new CreateIndexOptions { Name = "ip_created" }),
        ], ct);
    }
}
