namespace Ratable.Modules.Entities.Matching;

public static class Geo
{
    private const double EarthRadiusMeters = 6_371_000;

    /// <summary>Great-circle distance (haversine) in meters between two longitude/latitude points.</summary>
    public static double DistanceMeters(double lon1, double lat1, double lon2, double lat2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
