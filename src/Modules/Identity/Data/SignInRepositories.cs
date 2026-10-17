using MongoDB.Driver;
using Ratable.Modules.Identity.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Identity.Data;

public sealed class OtpChallengeRepository(IMongoDatabase database) : MongoRepository<OtpChallenge>(database, Collections.OtpChallenges)
{
    public Task InsertAsync(OtpChallenge challenge, CancellationToken ct = default) => Collection.InsertOneAsync(challenge, cancellationToken: ct);

    public async Task<OtpChallenge?> GetLatestOpenAsync(string phoneHash, CancellationToken ct = default) =>
        await Collection.Find(c => c.PhoneHash == phoneHash && c.ConsumedAt == null)
            .SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);

    public async Task<OtpChallenge?> GetLatestAsync(string phoneHash, CancellationToken ct = default) =>
        await Collection.Find(c => c.PhoneHash == phoneHash).SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);

    public Task<long> CountForPhoneSinceAsync(string phoneHash, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(c => c.PhoneHash == phoneHash && c.CreatedAt >= since, cancellationToken: ct);

    public Task<long> CountForIpSinceAsync(string ipHash, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(c => c.IpHash == ipHash && c.CreatedAt >= since, cancellationToken: ct);

    /// <summary>Counts one wrong or right attempt. Returns null when the challenge is used up, consumed or has no attempts left.</summary>
    public async Task<OtpChallenge?> TryCountAttemptAsync(string id, int maxAttempts, CancellationToken ct = default) =>
        await Collection.FindOneAndUpdateAsync(
            c => c.Id == id && c.ConsumedAt == null && c.Attempts < maxAttempts,
            Builders<OtpChallenge>.Update.Inc(c => c.Attempts, 1),
            new FindOneAndUpdateOptions<OtpChallenge> { ReturnDocument = ReturnDocument.After },
            ct);

    /// <summary>Marks the challenge used. Exactly one caller wins, so a code works once even under concurrent requests.</summary>
    public async Task<bool> TryConsumeAsync(string id, DateTime now, CancellationToken ct = default)
    {
        var result = await Collection.UpdateOneAsync(
            c => c.Id == id && c.ConsumedAt == null,
            Builders<OtpChallenge>.Update.Set(c => c.ConsumedAt, now), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
}

public sealed class SessionRepository(IMongoDatabase database) : MongoRepository<Session>(database, Collections.Sessions)
{
    public Task InsertAsync(Session session, CancellationToken ct = default) => Collection.InsertOneAsync(session, cancellationToken: ct);

    public async Task<Session?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
        await Collection.Find(s => s.TokenHash == tokenHash).FirstOrDefaultAsync(ct);

    public Task RevokeAsync(string tokenHash, DateTime now, CancellationToken ct = default) =>
        Collection.UpdateOneAsync(s => s.TokenHash == tokenHash && s.RevokedAt == null,
            Builders<Session>.Update.Set(s => s.RevokedAt, now), cancellationToken: ct);

    public Task RevokeAllForUserAsync(string userId, DateTime now, CancellationToken ct = default) =>
        Collection.UpdateManyAsync(s => s.UserId == userId && s.RevokedAt == null,
            Builders<Session>.Update.Set(s => s.RevokedAt, now), cancellationToken: ct);
}
