using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Contributions.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Contributions.Data;

/// <summary>The ledger can be appended to and read, nothing else. There is deliberately no update or delete of an entry.</summary>
public sealed class ContributionRepository(IMongoDatabase database) : MongoRepository<ContributionEvent>(database, Collections.ContributionEvents)
{
    private readonly IMongoCollection<RankSeen> _seen = database.GetCollection<RankSeen>(Collections.RankSeen);

    public Task AppendAsync(ContributionEvent entry, CancellationToken ct = default) => Collection.InsertOneAsync(entry, cancellationToken: ct);

    /// <summary>Appends an earning entry. False when the same action for the same reference was already paid (nothing is written).</summary>
    public async Task<bool> TryAppendEarnAsync(ContributionEvent entry, CancellationToken ct = default)
    {
        try
        {
            await Collection.InsertOneAsync(entry, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<ContributionEvent?> GetAsync(string id, CancellationToken ct = default) =>
        ObjectId.TryParse(id, out _) ? await Collection.Find(e => e.Id == id).FirstOrDefaultAsync(ct) : null;

    /// <summary>Appends a negative entry that cancels <paramref name="original"/>. A second reversal of the same entry is ignored (returns false).</summary>
    public async Task<bool> ReverseAsync(ContributionEvent original, string reason, CancellationToken ct = default)
    {
        try
        {
            await AppendAsync(new ContributionEvent
            {
                UserId = original.UserId,
                Action = original.Action,
                EntityId = original.EntityId,
                Points = -original.Points,
                Reason = reason,
                Reference = original.Reference,
                RootReference = original.RootReference,
                ReversesEventId = original.Id,
            }, ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<int> TotalPointsAsync(string userId, CancellationToken ct = default)
    {
        var result = await Collection.Aggregate()
            .Match(e => e.UserId == userId)
            .Group(new BsonDocument { { "_id", BsonNull.Value }, { "total", new BsonDocument("$sum", "$points") } })
            .FirstOrDefaultAsync(ct);
        return result is null ? 0 : result["total"].ToInt32();
    }

    public async Task<List<ContributionEvent>> ListForUserAsync(string userId, CancellationToken ct = default) =>
        await Collection.Find(e => e.UserId == userId).SortByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).ToListAsync(ct);

    /// <summary>The earning entries (not reversals) of a reference or of a review that have not been cancelled yet.</summary>
    public async Task<List<ContributionEvent>> ListLiveByReferenceAsync(string? reference, string? root, CancellationToken ct = default)
    {
        var earned = await Collection.Find(e => e.ReversesEventId == null && (reference != null ? e.Reference == reference : e.RootReference == root)).ToListAsync(ct);
        if (earned.Count == 0) return earned;
        var ids = earned.Select(e => e.Id).ToList();
        var reversed = (await Collection.Find(e => e.ReversesEventId != null && ids.Contains(e.ReversesEventId)).Project(e => e.ReversesEventId).ToListAsync(ct)).ToHashSet();
        return [.. earned.Where(e => !reversed.Contains(e.Id))];
    }

    /// <summary>Points earned today for one kind of action, net of reversals. Used for the daily cap.</summary>
    public async Task<int> NetPointsSinceAsync(string userId, string action, DateTime since, CancellationToken ct = default)
    {
        var entries = await Collection.Find(e => e.UserId == userId && e.Action == action && e.CreatedAt >= since).Project(e => e.Points).ToListAsync(ct);
        return Math.Max(0, entries.Sum());
    }

    public async Task<string?> GetSeenRankAsync(string userId, CancellationToken ct = default) =>
        (await _seen.Find(s => s.UserId == userId).FirstOrDefaultAsync(ct))?.RankId;

    public Task SetSeenRankAsync(string userId, string rankId, CancellationToken ct = default) =>
        _seen.ReplaceOneAsync(s => s.UserId == userId, new RankSeen { UserId = userId, RankId = rankId }, new ReplaceOptions { IsUpsert = true }, ct);
}

public sealed class ContributionsIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var c = database.GetCollection<ContributionEvent>(Collections.ContributionEvents);
        var k = Builders<ContributionEvent>.IndexKeys;
        // Earning entries have positive points; a reversal has negative points and is left out (a partial index cannot test for a missing field).
        var earning = Builders<ContributionEvent>.Filter.Gt(e => e.Points, 0);
        await c.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.UserId).Descending(e => e.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.UserId).Ascending(e => e.Action).Ascending(e => e.EntityId), new CreateIndexOptions { Name = "user_action_entity" }),
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.RootReference), new CreateIndexOptions { Name = "root" }),
            // An entry can be reversed only once.
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.ReversesEventId),
                new CreateIndexOptions<ContributionEvent>
                {
                    Name = "reverses_unique",
                    Unique = true,
                    PartialFilterExpression = Builders<ContributionEvent>.Filter.Type(e => e.ReversesEventId, BsonType.ObjectId),
                }),
            // The same action for the same reference is paid once, whatever happens (a republish, a retry).
            new CreateIndexModel<ContributionEvent>(k.Ascending(e => e.UserId).Ascending(e => e.Action).Ascending(e => e.Reference),
                new CreateIndexOptions<ContributionEvent> { Name = "earn_once", Unique = true, PartialFilterExpression = earning }),
        ], ct);
    }
}
