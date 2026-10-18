using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Ratable.Modules.Reviews.Services;

/// <summary>
/// The visitor's device location, used to answer one request and then forgotten (CLAUDE.md rule 17): it is never stored, never logged and never tied to
/// an account. The web app sends it in the X-Visitor-Location header ("lat,lng"), which web servers do not write to their access logs the way they do a
/// query string. The query parameters lat and lng are accepted too (for tests and tools).
/// </summary>
public static class VisitorLocation
{
    public const string Header = "X-Visitor-Location";

    public static (double Lat, double Lng)? From(HttpRequest request, double? lat, double? lng)
    {
        if (request.Headers.TryGetValue(Header, out var raw))
        {
            var parts = raw.ToString().Split(',');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                && a is >= -90 and <= 90 && b is >= -180 and <= 180)
                return (a, b);
        }
        return lat is >= -90 and <= 90 && lng is >= -180 and <= 180 ? (lat.Value, lng.Value) : null;
    }
}
