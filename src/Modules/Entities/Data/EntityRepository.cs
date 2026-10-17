using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Data;

public sealed class EntityRepository(IMongoDatabase database) : MongoRepository<Entity>(database, Collections.Entities)
{
    public Task InsertAsync(Entity entity, CancellationToken ct = default) => Collection.InsertOneAsync(entity, cancellationToken: ct);

    public async Task<Entity?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(e => e.Id == id).FirstOrDefaultAsync(ct);

    public Task UpdateAsync(Entity entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        return Collection.ReplaceOneAsync(e => e.Id == entity.Id, entity, cancellationToken: ct);
    }

    public async Task<List<Entity>> ListByCategoryAsync(string categoryId, int limit = 50, CancellationToken ct = default) =>
        await Collection.Find(e => e.CategoryId == categoryId && e.Status == EntityStatus.Active).Limit(limit).ToListAsync(ct);

    /// <summary>Active entities within <paramref name="maxMeters"/> of a point, nearest first (the caller re-ranks by score).</summary>
    public async Task<List<Entity>> NearAsync(double longitude, double latitude, double maxMeters, int limit = 50, CancellationToken ct = default)
    {
        var point = GeoJson.Point(GeoJson.Geographic(longitude, latitude));
        var filter = Builders<Entity>.Filter.And(
            Builders<Entity>.Filter.Eq(e => e.Status, EntityStatus.Active),
            Builders<Entity>.Filter.Near(e => e.Location, point, maxDistance: maxMeters));
        return await Collection.Find(filter).Limit(limit).ToListAsync(ct);
    }

    /// <summary>MongoDB text search over names, alternate names and search terms.</summary>
    public async Task<List<Entity>> SearchTextAsync(string text, int limit = 20, CancellationToken ct = default) =>
        await Collection.Find(Builders<Entity>.Filter.Text(text, new TextSearchOptions { CaseSensitive = false, DiacriticSensitive = false }))
            .Limit(limit).ToListAsync(ct);
}
