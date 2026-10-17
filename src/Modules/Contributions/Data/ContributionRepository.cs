using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Contributions.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Contributions.Data;

/// <summary>The ledger can be appended to and read, nothing else. There is deliberately no update or delete.</summary>
public sealed class ContributionRepository(IMongoDatabase database) : MongoRepository<ContributionEvent>(database, Collections.ContributionEvents)
{
    public Task AppendAsync(ContributionEvent entry, CancellationToken ct = default) => Collection.InsertOneAsync(entry, cancellationToken: ct);

    public async Task<ContributionEvent?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(e => e.Id == id).FirstOrDefaultAsync(ct);

    /// <summary>Appends a negative entry that cancels <paramref name="original"/>. A second reversal of the same entry throws DuplicateKey.</summary>
    public Task ReverseAsync(ContributionEvent original, string reason, CancellationToken ct = default) =>
        AppendAsync(new ContributionEvent
        {
            UserId = original.UserId,
            Action = original.Action,
            EntityId = original.EntityId,
            Points = -original.Points,
            Reason = reason,
            Reference = original.Reference,
            ReversesEventId = original.Id,
        }, ct);

    public async Task<int> TotalPointsAsync(string userId, CancellationToken ct = default)
    {
        var result = await Collection.Aggregate()
            .Match(e => e.UserId == userId)
            .Group(new BsonDocument { { "_id", BsonNull.Value }, { "total", new BsonDocument("$sum", "$points") } })
            .FirstOrDefaultAsync(ct);
        return result is null ? 0 : result["total"].ToInt32();
    }

    public async Task<List<ContributionEvent>> ListForUserAsync(string userId, CancellationToken ct = default) =>
        await Collection.Find(e => e.UserId == userId).SortByDescending(e => e.CreatedAt).ToListAsync(ct);
}

public sealed class ContributionsIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var c = database.GetCollection<ContributionEvent>(Collections.ContributionEvents);
        var k = Builders<ContributionEvent>.IndexKeys;
        await c.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.UserId).Descending(e => e.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.UserId).Ascending(e => e.Action).Ascending(e => e.EntityId), new CreateIndexOptions { Name = "user_action_entity" }),
            // An entry can be reversed only once.
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.ReversesEventId),
                new CreateIndexOptions<ContributionEvent>
                {
                    Name = "reverses_unique",
                    Unique = true,
                    PartialFilterExpression = Builders<ContributionEvent>.Filter.Type(e => e.ReversesEventId, BsonType.ObjectId),
                }),
        ], ct);
    }
}
