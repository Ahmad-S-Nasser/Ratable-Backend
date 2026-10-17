using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Ai.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Ai.Data;

/// <summary>The AI spend log. Append and read only.</summary>
public sealed class AiCallRepository(IMongoDatabase database) : MongoRepository<AiCall>(database, Collections.AiCalls)
{
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(Collections.AiCalls);

    public Task AppendAsync(AiCall call, CancellationToken ct = default) => Collection.InsertOneAsync(call, cancellationToken: ct);

    /// <summary>Money spent in [from, to).</summary>
    public async Task<decimal> SumCostAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var row = await _raw.Aggregate()
            .Match(Range(from, to))
            .Group(new BsonDocument { { "_id", BsonNull.Value }, { "cost", new BsonDocument("$sum", "$costUsd") } })
            .FirstOrDefaultAsync(ct);
        return row is null ? 0m : row["cost"].ToDecimal();
    }

    public async Task<SpendSummary> SummaryAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        string Count(AiCallStatus s) => $"{{ $sum: {{ $cond: [ {{ $eq: ['$status', '{s}'] }}, 1, 0 ] }} }}";
        var group = BsonDocument.Parse($$"""
            { _id: null,
              cost: { $sum: '$costUsd' },
              calls: { $sum: 1 },
              deferred: {{Count(AiCallStatus.Deferred)}},
              failed: {{Count(AiCallStatus.Failed)}},
              cacheHits: {{Count(AiCallStatus.CacheHit)}},
              input: { $sum: '$inputTokens' },
              output: { $sum: '$outputTokens' } }
            """);
        var row = await _raw.Aggregate().Match(Range(from, to)).Group(group).FirstOrDefaultAsync(ct);
        if (row is null) return new SpendSummary(0m, 0, 0, 0, 0, 0, 0);
        return new SpendSummary(row["cost"].ToDecimal(), row["calls"].ToInt32(), row["deferred"].ToInt32(), row["failed"].ToInt32(),
            row["cacheHits"].ToInt32(), row["input"].ToInt64(), row["output"].ToInt64());
    }

    public async Task<List<FeatureSpend>> ByFeatureAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var rows = await _raw.Aggregate()
            .Match(Range(from, to))
            .Group(new BsonDocument { { "_id", "$feature" }, { "cost", new BsonDocument("$sum", "$costUsd") }, { "calls", new BsonDocument("$sum", 1) } })
            .ToListAsync(ct);
        return [.. rows.Select(r => new FeatureSpend(r["_id"].AsString, r["cost"].ToDecimal(), r["calls"].ToInt32())).OrderByDescending(f => f.CostUsd)];
    }

    private static BsonDocument Range(DateTime from, DateTime to) =>
        new("at", new BsonDocument { { "$gte", from }, { "$lt", to } });
}

public sealed class AiCacheRepository(IMongoDatabase database) : MongoRepository<AiCacheEntry>(database, Collections.AiCache)
{
    public async Task<AiCacheEntry?> GetAsync(string key, DateTime now, CancellationToken ct = default) =>
        await Collection.Find(e => e.Key == key && e.ExpiresAt > now).FirstOrDefaultAsync(ct);

    public Task PutAsync(AiCacheEntry entry, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(e => e.Key == entry.Key, entry, new ReplaceOptions { IsUpsert = true }, ct);
}

public sealed class AiIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var calls = database.GetCollection<AiCall>(Collections.AiCalls);
        var k = Builders<AiCall>.IndexKeys;
        await calls.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<AiCall>(k.Descending(c => c.At), new CreateIndexOptions { Name = "at" }),
            new CreateIndexModel<AiCall>(k.Ascending(c => c.Feature).Descending(c => c.At), new CreateIndexOptions { Name = "feature_at" }),
            new CreateIndexModel<AiCall>(k.Ascending(c => c.ItemReference).Descending(c => c.At), new CreateIndexOptions { Name = "item_at" }),
        ], ct);

        var cache = database.GetCollection<AiCacheEntry>(Collections.AiCache);
        await cache.Indexes.CreateOneAsync(
            new CreateIndexModel<AiCacheEntry>(Builders<AiCacheEntry>.IndexKeys.Ascending(e => e.ExpiresAt),
                new CreateIndexOptions { Name = "expires_ttl", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
    }
}
