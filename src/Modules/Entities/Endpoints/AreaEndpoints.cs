using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Shared.Data;
using Ratable.Shared.Location;

namespace Ratable.Modules.Entities.Endpoints;

public sealed record AreaDto(string Id, LocalizedText Name, string Country, string Kind, double Latitude, double Longitude, double RadiusKm);

/// <summary>Areas for the area picker, plus geocode and reverse geocode. A location sent here answers the call and is forgotten.</summary>
public static class AreaEndpoints
{
    private const string Header = "X-Visitor-Location";

    public static IEndpointRouteBuilder MapAreaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Location");
        group.MapGet("/areas", async (string? q, string? country, int? limit, ILocationProvider locations, CancellationToken ct) =>
            Results.Ok((string.IsNullOrWhiteSpace(q) ? await locations.ListAreasAsync(country, ct) : await locations.SearchAreasAsync(q, limit ?? 10, ct)).Select(ToDto)))
            .Produces<List<AreaDto>>();
        group.MapGet("/areas/{id}", async (string id, ILocationProvider locations, CancellationToken ct) =>
            await locations.GetAreaAsync(id, ct) is { } a ? Results.Ok(ToDto(a)) : Results.NotFound())
            .Produces<AreaDto>().Produces(404);
        group.MapGet("/geocode", async (string? text, ILocationProvider locations, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(text) ? Results.BadRequest(new ErrorBody("text_required"))
                : await locations.GeocodeAsync(text, ct) is { } a ? Results.Ok(ToDto(a)) : Results.NotFound())
            .Produces<AreaDto>().Produces<ErrorBody>(400).Produces(404);
        group.MapGet("/reverse-geocode", async (double? lat, double? lng, HttpRequest http, ILocationProvider locations, CancellationToken ct) =>
            {
                var at = From(http, lat, lng);
                if (at is null) return Results.BadRequest(new ErrorBody("location_required"));
                return await locations.ReverseGeocodeAsync(at.Value.Lat, at.Value.Lng, ct) is { } a ? Results.Ok(ToDto(a)) : Results.NotFound();
            })
            .Produces<AreaDto>().Produces<ErrorBody>(400).Produces(404);
        return app;
    }

    private static AreaDto ToDto(AreaInfo a) => new(a.Id, a.Name, a.Country, a.Kind, a.Latitude, a.Longitude, a.RadiusKm);

    /// <summary>The location from the header (not written to access logs) or, for tools, the query.</summary>
    private static (double Lat, double Lng)? From(HttpRequest request, double? lat, double? lng)
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
