using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ratable.Modules.Entities.Endpoints;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Entities.Import;

public static class ImportEndpoints
{
    public const string SampleCsv = """
        category,name_ar,name_en,alt_names,address,city,country,lat,lng,google_place_id,phone,whatsapp,website,external_ids,fields
        compounds,مدينتي,Madinaty,Madinati|مدينتى,طريق السويس,القاهرة,Egypt,30.1075,31.6403,,,,https://www.madinaty.example,wikidata:Q12345,
        restaurants-cafes,,Zooba Zamalek,زوبا الزمالك,26 July St,Cairo,Egypt,30.0626,31.2197,,+20 2 2735 0000,,https://zooba.example,,cuisine:Egyptian street food|priceBand:$$
        service-providers,أحمد السباك,Ahmed Plumber,,Maadi,Cairo,Egypt,29.9602,31.2569,,+20 100 123 4567,+201001234567,,,trade:Plumber|serviceArea:Maadi
        """;

    public const string SampleJson = """
        [
          {
            "category": "compounds", "nameAr": "مدينتي", "nameEn": "Madinaty", "altNames": ["Madinati", "مدينتى"],
            "address": "طريق السويس", "city": "القاهرة", "country": "Egypt", "lat": 30.1075, "lng": 31.6403,
            "website": "https://www.madinaty.example", "externalIds": { "wikidata": "Q12345" }
          },
          {
            "category": "restaurants-cafes", "nameEn": "Zooba Zamalek", "altNames": ["زوبا الزمالك"],
            "city": "Cairo", "country": "Egypt", "lat": 30.0626, "lng": 31.2197, "phone": "+20 2 2735 0000",
            "fields": { "cuisine": "Egyptian street food", "priceBand": "$$" }
          }
        ]
        """;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/import").WithTags("Admin").RequireAuthorization(PolicyNames.Admin);
        group.MapPost("/entities", Run).Produces<ImportReport>().Produces<ErrorBody>(400).Produces<ErrorBody>(413);
        group.MapGet("/sample", (string? format) => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? Results.Text(SampleJson, "application/json") : Results.Text(SampleCsv, "text/csv"));
        return app;
    }

    /// <summary>
    /// Admin only. The body is the file text. dryRun defaults to true: nothing is written until it is asked for with dryRun=false, so the report can be read first. Every real
    /// run is written to the audit log.
    /// </summary>
    private static async Task<IResult> Run(
        HttpRequest request, ClaimsPrincipal user, SeedImporter importer, IOptions<EntitiesOptions> options, string? format, string? batch, bool? dryRun, CancellationToken ct)
    {
        var max = options.Value.ImportMaxBytes;
        if (request.ContentLength > max) return Results.Json(new ErrorBody("file_too_large"), statusCode: StatusCodes.Status413PayloadTooLarge);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return Results.Json(new ErrorBody("file_too_large"), statusCode: StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, read);
        }

        List<ImportRow> rows;
        try
        {
            rows = ImportParser.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), format);
        }
        catch (ImportFormatException ex)
        {
            return Results.BadRequest(new ErrorBody(ex.Message));
        }
        if (rows.Count == 0) return Results.BadRequest(new ErrorBody("empty_file"));
        if (rows.Count > options.Value.ImportMaxRows) return Results.BadRequest(new ErrorBody("too_many_rows", options.Value.ImportMaxRows.ToString()));

        return Results.Ok(await importer.RunAsync(rows, dryRun ?? true, batch, user.FindFirstValue(ClaimTypes.NameIdentifier)!, ct));
    }
}
