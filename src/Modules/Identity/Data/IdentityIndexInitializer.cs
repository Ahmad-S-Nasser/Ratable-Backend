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
    }
}
