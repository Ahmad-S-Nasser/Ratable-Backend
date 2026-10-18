using Ratable.Shared.Data;

namespace Ratable.Shared.Location;

/// <summary>An area people can filter by: a city or a district, with a centre and a rough radius.</summary>
public sealed record AreaInfo(string Id, LocalizedText Name, string Country, string Kind, double Latitude, double Longitude, double RadiusKm, string? GooglePlaceId);

/// <summary>A place (a business, a compound ...) as location data: where it is, and the optional Google place id.</summary>
public sealed record PlaceLocation(string Id, string CategoryId, LocalizedText Name, string? City, string? Address, double? Latitude, double? Longitude, string? GooglePlaceId);

/// <summary>
/// Everything the product needs from a map service, behind one interface (CLAUDE.md rule 17): search areas, geocode, reverse geocode, place details and
/// distance. Today it is backed by a seeded list of Egyptian and Gulf areas in MongoDB; a Google Places, Geocoding and Maps implementation can be
/// added later behind the same interface with no change to callers or screens (following Google's terms for attribution and caching then).
/// Locations passed in are used to answer the call and are never stored.
/// </summary>
public interface ILocationProvider
{
    Task<IReadOnlyList<AreaInfo>> SearchAreasAsync(string query, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AreaInfo>> ListAreasAsync(string? country, CancellationToken cancellationToken = default);

    Task<AreaInfo?> GetAreaAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>The area a piece of text names (for example "New Cairo"), or null.</summary>
    Task<AreaInfo?> GeocodeAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>The area a point is in (or the nearest one when it is close to its edge), or null when it is nowhere near a known area.</summary>
    Task<AreaInfo?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default);

    Task<PlaceLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken cancellationToken = default);

    double DistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2);
}
