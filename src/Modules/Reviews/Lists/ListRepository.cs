using System.Reflection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Ratable.Shared.Data;

namespace Ratable.Modules.Reviews.Lists;

public sealed class ListRepository(IMongoDatabase database) : MongoRepository<ListDefinition>(database, Collections.CollectionLists)
{
    public async Task<List<ListDefinition>> ListActiveAsync(CancellationToken ct = default) =>
        await Collection.Find(l => l.Active).SortBy(l => l.Id).ToListAsync(ct);

    public async Task<ListDefinition?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(l => l.Id == id && l.Active).FirstOrDefaultAsync(ct);

    public async Task<bool> InsertIfMissingAsync(ListDefinition list, CancellationToken ct = default)
    {
        var result = await Collection.UpdateOneAsync(l => l.Id == list.Id, Builders<ListDefinition>.Update.SetOnInsert(l => l.Title, list.Title)
            .SetOnInsert(l => l.Slug, list.Slug).SetOnInsert(l => l.CategoryId, list.CategoryId).SetOnInsert(l => l.Area, list.Area)
            .SetOnInsert(l => l.MinReviews, list.MinReviews).SetOnInsert(l => l.Limit, list.Limit).SetOnInsert(l => l.Active, list.Active)
            .SetOnInsert(l => l.CreatedAt, list.CreatedAt), new UpdateOptions { IsUpsert = true }, ct);
        return result.UpsertedId is not null;
    }
}

/// <summary>Loads the lists from the embedded Lists/*.json files. Only lists that are missing are inserted, so edits made later are never overwritten.</summary>
public sealed class ListSeeder(ListRepository lists, ILogger<ListSeeder> logger) : ISeeder
{
    public static IEnumerable<ListDefinition> LoadEmbedded()
    {
        MongoConventions.Register();
        var assembly = typeof(ListSeeder).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Lists.") && n.EndsWith(".json")).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return BsonSerializer.Deserialize<ListDefinition>(BsonDocument.Parse(reader.ReadToEnd()));
        }
    }

    public async Task SeedAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        foreach (var list in LoadEmbedded())
        {
            if (dryRun)
            {
                logger.LogInformation("Dry run: list {List} would be inserted if missing", list.Id);
                continue;
            }
            var inserted = await lists.InsertIfMissingAsync(list, cancellationToken);
            logger.LogInformation("List {List} {Action}", list.Id, inserted ? "inserted" : "already present");
        }
    }
}
