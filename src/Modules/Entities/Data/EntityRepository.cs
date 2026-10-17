using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Data;

public sealed class EntityRepository(IMongoDatabase database) : MongoRepository<Entity>(database, Collections.Entities)
{
    public Task InsertAsync(Entity entity, CancellationToken ct = default) => Collection.InsertOneAsync(entity, cancellationToken: ct);

    public async Task<Entity?> GetAsync(string id, CancellationToken ct = default) =>
        MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Collection.Find(e => e.Id == id).FirstOrDefaultAsync(ct) : null;

    /// <summary>Replaces the whole document. Sets UpdatedAt (the sitemap lastmod).</summary>
    public Task ReplaceAsync(Entity entity, DateTime now, CancellationToken ct = default)
    {
        entity.UpdatedAt = now;
        return Collection.ReplaceOneAsync(e => e.Id == entity.Id, entity, cancellationToken: ct);
    }

    public async Task<List<Entity>> ListByCategoryAsync(string categoryId, int limit = 50, CancellationToken ct = default) =>
        await Collection.Find(e => e.CategoryId == categoryId && e.Status == EntityStatus.Active).Limit(limit).ToListAsync(ct);

    /// <summary>The moderator queue, oldest first.</summary>
    public async Task<List<Entity>> ListByStatusAsync(EntityStatus status, int limit = 100, CancellationToken ct = default) =>
        await Collection.Find(e => e.Status == status).SortBy(e => e.CreatedAt).Limit(limit).ToListAsync(ct);

    public Task<long> CountSubmittedSinceAsync(string userId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(e => e.SubmittedByUserId == userId && e.CreatedAt >= since, cancellationToken: ct);

    /// <summary>Active entities within <paramref name="maxMeters"/> of a point, nearest first (the caller re-ranks by score).</summary>
    public async Task<List<Entity>> NearAsync(double longitude, double latitude, double maxMeters, int limit = 50, CancellationToken ct = default)
    {
        var point = GeoJson.Point(GeoJson.Geographic(longitude, latitude));
        var filter = Builders<Entity>.Filter.And(
            Builders<Entity>.Filter.Eq(e => e.Status, EntityStatus.Active),
            Builders<Entity>.Filter.Near(e => e.Location, point, maxDistance: maxMeters));
        return await Collection.Find(filter).Limit(limit).ToListAsync(ct);
    }

    /// <summary>
    /// Places that could be the same as a new submission: in the same category, active or waiting, and either close by or sharing a word
    /// of the (normalized) name. The caller scores them; this only narrows the pool.
    /// </summary>
    public async Task<List<Entity>> CandidatePoolAsync(string categoryId, double longitude, double latitude, double radiusMeters,
        string normalizedSearchText, int limit = 100, CancellationToken ct = default)
    {
        var f = Builders<Entity>.Filter;
        var live = f.And(f.Eq(e => e.CategoryId, categoryId), f.In(e => e.Status, [EntityStatus.Active, EntityStatus.PendingModeration]));

        var near = await Collection.Find(f.And(live,
            f.Near(e => e.Location, GeoJson.Point(GeoJson.Geographic(longitude, latitude)), maxDistance: radiusMeters))).Limit(limit).ToListAsync(ct);

        var byName = string.IsNullOrWhiteSpace(normalizedSearchText)
            ? []
            : await Collection.Find(f.And(live, f.Text(normalizedSearchText))).Limit(limit).ToListAsync(ct);

        return [.. near.Concat(byName).GroupBy(e => e.Id).Select(g => g.First())];
    }

    /// <summary>MongoDB text search over the normalized names. Only public (Active) places.</summary>
    public async Task<List<Entity>> SearchTextAsync(string text, string? categoryId = null, int limit = 20, CancellationToken ct = default)
    {
        var f = Builders<Entity>.Filter;
        var filter = f.And(f.Text(text), f.Eq(e => e.Status, EntityStatus.Active));
        if (categoryId is not null) filter = f.And(filter, f.Eq(e => e.CategoryId, categoryId));
        return await Collection.Find(filter).Limit(limit).ToListAsync(ct);
    }
}
