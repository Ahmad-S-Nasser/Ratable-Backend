using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Identity.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Identity.Data;

public sealed class IdentityIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var users = database.GetCollection<User>(Collections.Users);
        var k = Builders<User>.IndexKeys;
        await users.Indexes.CreateManyAsync(
        [
            // One account per phone number. Partial, because accounts without a phone are allowed.
            new CreateIndexModel<User>(k.Ascending(u => u.PhoneHash),
                new CreateIndexOptions<User>
                {
                    Name = "phone_hash_unique",
                    Unique = true,
                    PartialFilterExpression = Builders<User>.Filter.Type(u => u.PhoneHash, BsonType.String),
                }),
            // One account per social login. Accounts with no social login are not in this index.
            new CreateIndexModel<User>(k.Ascending("logins.provider").Ascending("logins.subject"),
                new CreateIndexOptions<User>
                {
                    Name = "login_unique",
                    Unique = true,
                    PartialFilterExpression = new BsonDocumentFilterDefinition<User>(
                        new BsonDocument("logins.provider", new BsonDocument("$type", "string"))),
                }),
        ], ct);

        var otp = database.GetCollection<OtpChallenge>(Collections.OtpChallenges);
        var o = Builders<OtpChallenge>.IndexKeys;
        await otp.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<OtpChallenge>(o.Ascending(c => c.PhoneHash).Descending(c => c.CreatedAt), new CreateIndexOptions { Name = "phone_created" }),
            new CreateIndexModel<OtpChallenge>(o.Ascending(c => c.IpHash).Descending(c => c.CreatedAt), new CreateIndexOptions { Name = "ip_created" }),
            // Old challenges are removed a day after they expire; they are only needed for rate limits.
            new CreateIndexModel<OtpChallenge>(o.Ascending(c => c.ExpiresAt), new CreateIndexOptions { Name = "expires_ttl", ExpireAfter = TimeSpan.FromDays(1) }),
        ], ct);

        var sessions = database.GetCollection<Session>(Collections.Sessions);
        var s = Builders<Session>.IndexKeys;
        await sessions.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Session>(s.Ascending(x => x.TokenHash), new CreateIndexOptions { Name = "token_hash_unique", Unique = true }),
            new CreateIndexModel<Session>(s.Ascending(x => x.UserId), new CreateIndexOptions { Name = "user" }),
            new CreateIndexModel<Session>(s.Ascending(x => x.ExpiresAt), new CreateIndexOptions { Name = "expires_ttl", ExpireAfter = TimeSpan.Zero }),
        ], ct);
    }
}
