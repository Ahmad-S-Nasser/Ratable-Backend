using System.Reflection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Matching;
using Ratable.Shared.Data;
using Ratable.Shared.Location;
using Ratable.Shared.Text;

namespace Ratable.Modules.Entities.Services;

/// <summary>An area of the seeded list: a city or a district with a centre point (2dsphere indexed) and a radius.</summary>
public sealed class Area
{
    [BsonId] public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public string Country { get; set; } = "";
    public string Kind { get; set; } = "city";
    public GeoJsonPoint<GeoJson2DGeographicCoordinates> Location { get; set; } = null!;
    public double RadiusKm { get; set; } = 10;
    public string? GooglePlaceId { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>The normalized names, made by the Arabic normalizer, so a search finds "الشيخ زايد" and "الشيخ زايد" however it is spelt.</summary>
    public string SearchTerms { get; set; } = "";
}

public sealed class AreaRepository(IMongoDatabase database) : MongoRepository<Area>(database, Collections.Areas)
{
    public async Task<List<Area>> ListAsync(string? country, CancellationToken ct = default) =>
        await Collection.Find(a => a.Active && (country == null || a.Country == country)).SortBy(a => a.Id).ToListAsync(ct);

    public async Task<Area?> GetAsync(string id, CancellationToken ct = default) => await Collection.Find(a => a.Id == id && a.Active).FirstOrDefaultAsync(ct);

    public async Task<List<Area>> SearchAsync(string normalized, int limit, CancellationToken ct = default) =>
        await Collection.Find(a => a.Active && a.SearchTerms.Contains(normalized)).SortBy(a => a.Id).Limit(limit).ToListAsync(ct);

    public async Task<Area?> NearestAsync(double latitude, double longitude, CancellationToken ct = default) =>
        await Collection.Find(Builders<Area>.Filter.And(Builders<Area>.Filter.Eq(a => a.Active, true),
            Builders<Area>.Filter.Near(a => a.Location, GeoJson.Point(GeoJson.Geographic(longitude, latitude))))).Limit(1).FirstOrDefaultAsync(ct);

    public Task<bool> ExistsAsync(string id, CancellationToken ct = default) => Collection.Find(a => a.Id == id).AnyAsync(ct);

    public async Task<bool> InsertIfMissingAsync(Area area, CancellationToken ct = default)
    {
        try
        {
            await Collection.InsertOneAsync(area, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }
}

public sealed class AreasIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var c = database.GetCollection<Area>(Collections.Areas);
        await c.Indexes.CreateOneAsync(new CreateIndexModel<Area>(Builders<Area>.IndexKeys.Geo2DSphere(a => a.Location), new CreateIndexOptions { Name = "location_2dsphere" }), cancellationToken: ct);
    }
}

/// <summary>Loads the areas from the embedded Areas/areas.json (configuration, not code). Only missing areas are inserted, so later edits survive.</summary>
public sealed class AreaSeeder(AreaRepository areas, ILogger<AreaSeeder> logger) : ISeeder
{
    public static IEnumerable<Area> LoadEmbedded()
    {
        MongoConventions.Register();
        var assembly = typeof(AreaSeeder).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("areas.json"));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        foreach (var element in BsonSerializer.Deserialize<BsonArray>(reader.ReadToEnd()))
        {
            var area = BsonSerializer.Deserialize<Area>(element.AsBsonDocument);
            area.SearchTerms = string.Join(' ', new[] { area.Name.Ar, area.Name.En, area.Id.Replace('-', ' ') }.Select(ArabicNormalizer.Normalize));
            yield return area;
        }
    }

    public async Task SeedAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        foreach (var area in LoadEmbedded())
        {
            if (dryRun)
            {
                logger.LogInformation("Dry run: area {Area} {Action}", area.Id, await areas.ExistsAsync(area.Id, cancellationToken) ? "exists, skipped" : "would be inserted");
                continue;
            }
            logger.LogInformation("Area {Area} {Action}", area.Id, await areas.InsertIfMissingAsync(area, cancellationToken) ? "inserted" : "already present");
        }
    }
}

/// <summary>
/// <see cref="ILocationProvider"/> backed by the seeded areas and the entities' own coordinates. A Google-backed implementation can replace it
/// behind the same interface. It never stores a location it is given.
/// </summary>
public sealed class SeededLocationProvider(AreaRepository areas, EntityRepository entities) : ILocationProvider
{
    private static AreaInfo ToInfo(Area a) => new(a.Id, a.Name, a.Country, a.Kind, a.Location.Coordinates.Latitude, a.Location.Coordinates.Longitude, a.RadiusKm, a.GooglePlaceId);

    public async Task<IReadOnlyList<AreaInfo>> SearchAreasAsync(string query, int limit, CancellationToken ct = default)
    {
        var normalized = ArabicNormalizer.Normalize(query);
        if (normalized.Length == 0) return [];
        return [.. (await areas.SearchAsync(normalized, Math.Clamp(limit, 1, 25), ct)).Select(ToInfo)];
    }

    public async Task<IReadOnlyList<AreaInfo>> ListAreasAsync(string? country, CancellationToken ct = default) =>
        [.. (await areas.ListAsync(string.IsNullOrWhiteSpace(country) ? null : country.ToUpperInvariant(), ct)).Select(ToInfo)];

    public async Task<AreaInfo?> GetAreaAsync(string id, CancellationToken ct = default) => await areas.GetAsync(id, ct) is { } a ? ToInfo(a) : null;

    public async Task<AreaInfo?> GeocodeAsync(string text, CancellationToken ct = default) => (await SearchAreasAsync(text, 1, ct)).FirstOrDefault();

    public async Task<AreaInfo?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var nearest = await areas.NearestAsync(latitude, longitude, ct);
        if (nearest is null) return null;
        var distance = DistanceMeters(latitude, longitude, nearest.Location.Coordinates.Latitude, nearest.Location.Coordinates.Longitude);
        // Inside the area, or just outside its edge; further than that the point is not in any area we know.
        return distance <= nearest.RadiusKm * 1000 * 1.5 ? ToInfo(nearest) : null;
    }

    public async Task<PlaceLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken ct = default)
    {
        var e = await entities.GetAsync(placeId, ct);
        return e is { Status: Models.EntityStatus.Active }
            ? new PlaceLocation(e.Id, e.CategoryId, e.Name, e.City, e.Address, e.Location?.Coordinates.Latitude, e.Location?.Coordinates.Longitude, e.GooglePlaceId)
            : null;
    }

    public double DistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2) => Geo.DistanceMeters(longitude1, latitude1, longitude2, latitude2);
}
