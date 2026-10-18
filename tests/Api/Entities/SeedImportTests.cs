using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Import;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Data;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Entities;

public sealed class SeedImportTests
{
    private const string Header = "category,name_ar,name_en,alt_names,address,city,country,lat,lng,google_place_id,phone,whatsapp,website,external_ids,fields\n";

    private static async Task<(HttpResponseMessage Response, JsonElement Json)> Import(IdentityApiFactory api, string? cookie, string body, string query = "")
    {
        using var http = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/import/entities" + query) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        if (cookie is not null) request.Headers.Add("Cookie", cookie.Split(';')[0]);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode >= 500) throw new InvalidOperationException($"{(int)response.StatusCode} {text}");
        return (response, text.StartsWith('{') ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string Outcomes(JsonElement report) => string.Join(",", report.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("outcome").GetString()));

    private static long Count(IdentityApiFactory api) => api.Database.GetCollection<BsonDocument>(Collections.Entities).CountDocuments(new BsonDocument());

    // ---- reading the file ----

    [Fact]
    public void Csv_is_read_with_quotes_a_bom_semicolons_and_arabic_text()
    {
        var csv = "﻿Category;Name AR;Name_EN;Alt-Names;Lat;Lng;Fields\n" +
                  "compounds;\"مدينتي\";\"Madinaty, Cairo\";Madinati|مدينتى;30.1;31.6;a:b|c:d\n\n" +
                  "restaurants-cafes;;Zooba;;30.0;31.2;\n";
        var rows = ImportParser.Parse(csv, null);
        Assert.Equal(2, rows.Count);
        Assert.Equal("مدينتي", rows[0].NameAr);
        Assert.Equal("Madinaty, Cairo", rows[0].NameEn);
        Assert.Equal(["Madinati", "مدينتى"], rows[0].AltNames);
        Assert.Equal(30.1, rows[0].Latitude);
        Assert.Equal("d", rows[0].Fields["c"]);
        Assert.Null(rows[1].NameAr);
    }

    [Fact]
    public void Json_is_read_and_the_sample_files_parse_to_the_same_places()
    {
        var json = ImportParser.Parse(ImportEndpoints.SampleJson, null);
        var csv = ImportParser.Parse(ImportEndpoints.SampleCsv, null);
        Assert.Equal(2, json.Count);
        Assert.Equal(3, csv.Count);
        Assert.Equal(json[0].NameEn, csv[0].NameEn);
        Assert.Equal(json[1].Fields["priceBand"], csv[1].Fields["priceBand"]);
        Assert.Throws<ImportFormatException>(() => ImportParser.Parse("{ \"not\": \"an array\" }", "json"));
        Assert.Throws<ImportFormatException>(() => ImportParser.Parse("[ 1, 2 ]", "json"));
        Assert.Throws<ImportFormatException>(() => ImportParser.Parse("category,name_en\n\"open,quote", "csv"));
    }

    // ---- who may import ----

    [MongoFact]
    public async Task Only_an_admin_can_import_and_a_dry_run_is_the_default()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var reader = await api.SignInAsync("+201111111111");
        var file = Header + "compounds,,Palm Hills,,,Giza,Egypt,30.0,31.0,,,,,,\n";

        Assert.Equal(HttpStatusCode.Unauthorized, (await Import(api, null, file)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Import(api, reader.Cookie, file)).Response.StatusCode);
        var (ok, report) = await Import(api, admin.Cookie, file);   // no dryRun given
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, report.GetProperty("created").GetInt32());
        Assert.Equal(0, Count(api));                              // nothing was written
        Assert.Equal(HttpStatusCode.BadRequest, (await Import(api, admin.Cookie, "")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import(api, admin.Cookie, "[ not json", "?format=json")).Response.StatusCode);
    }

    // ---- what a run does ----

    [MongoFact]
    public async Task A_real_run_creates_active_seeded_places_that_are_public_and_audited()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var file = Header +
                   "compounds,مدينتي,Madinaty,Madinati,Suez Road,Cairo,Egypt,30.1075,31.6403,,,,https://www.madinaty.example,wikidata:Q1,\n" +
                   "restaurants-cafes,,Zooba Zamalek,,26 July St,Cairo,Egypt,30.0626,31.2197,,,,,,cuisine:Street food|priceBand:$$\n";

        var (_, dry) = await Import(api, admin.Cookie, file, "?dryRun=true&batch=pilot");
        Assert.Equal("created,created", Outcomes(dry));
        Assert.Equal(0, Count(api));

        var (_, real) = await Import(api, admin.Cookie, file, "?dryRun=false&batch=pilot");
        Assert.Equal("created,created", Outcomes(real));
        Assert.Equal(2, real.GetProperty("created").GetInt32());
        Assert.Equal(2, Count(api));
        var id = real.GetProperty("items")[0].GetProperty("entityId").GetString();

        var entity = await api.Services.GetRequiredService<EntityRepository>().GetAsync(id!);
        Assert.True(entity!.Seeded);
        Assert.Equal("pilot", entity.SeedBatch);
        Assert.Equal(EntityStatus.Active, entity.Status);
        Assert.False(string.IsNullOrEmpty(entity.Slugs["en"]));
        Assert.Contains("https://www.madinaty.example/", entity.SameAs);   // the address is normalized
        Assert.Equal("Q1", entity.ExternalIds["wikidata"]);

        var (response, publicEntity) = await Send(api, HttpMethod.Get, $"/api/entities/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Madinaty", publicEntity.GetProperty("name").GetProperty("en").GetString());
        var (_, places) = await Send(api, HttpMethod.Get, "/api/categories/restaurants-cafes/places");
        Assert.Equal(1, places.GetProperty("total").GetInt32());
        Assert.Contains("entities.seed_import", (await Send(api, HttpMethod.Get, "/api/admin/audit?limit=10", admin.Cookie)).Json.GetRawText());
    }

    [MongoFact]
    public async Task Running_the_same_file_again_creates_nothing_and_changes_nothing()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var file = Header + "compounds,مدينتي,Madinaty,,Suez Road,Cairo,Egypt,30.1075,31.6403,gp-1,,,,wikidata:Q1,\n" +
                            "compounds,,Palm Hills,,,Giza,Egypt,30.0,31.0,,,,,,\n";
        await Import(api, admin.Cookie, file, "?dryRun=false");
        var (_, again) = await Import(api, admin.Cookie, file, "?dryRun=false");

        Assert.Equal("merged,merged", Outcomes(again));
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.All(again.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("changed").GetBoolean()));
        Assert.Equal(2, Count(api));
    }

    [MongoFact]
    public async Task A_row_that_is_the_same_place_merges_into_it_fills_blanks_and_never_overwrites()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var existing = await SeedEntity(api);   // compounds, Madinaty / مدينتي near 30.10, 31.64
        var repo = api.Services.GetRequiredService<EntityRepository>();
        existing.Address = "The owner's address";
        await repo.ReplaceAsync(existing, DateTime.UtcNow);

        // A spelling variant of the Arabic name, a different address, and new data.
        var file = Header + "compounds,مدينتى,Madinaty,Madinati City,A different address,Cairo,Egypt,30.1001,31.6402,gp-9,+20 2 1234 5678,,,wikidata:Q1,\n";
        var (_, report) = await Import(api, admin.Cookie, file, "?dryRun=false");

        Assert.Equal("merged", Outcomes(report));
        Assert.Equal(existing.Id, report.GetProperty("items")[0].GetProperty("matchedEntityId").GetString());
        Assert.True(report.GetProperty("items")[0].GetProperty("changed").GetBoolean());
        Assert.Equal(1, Count(api));
        var after = (await repo.GetAsync(existing.Id))!;
        Assert.Equal("The owner's address", after.Address);        // not overwritten
        Assert.Equal("Cairo", after.City);                          // a blank, filled
        Assert.Equal("gp-9", after.GooglePlaceId);
        Assert.Equal("Q1", after.ExternalIds["wikidata"]);
        Assert.Contains("Madinati City", after.AlternateNames);
        Assert.Equal("+20 2 1234 5678", after.Contact!.Phone);
        Assert.Equal("import", after.Contact.Source);
    }

    [MongoFact]
    public async Task The_same_google_place_is_the_same_place_even_with_a_different_name()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        await Import(api, admin.Cookie, Header + "compounds,,Old Name,,,Cairo,Egypt,30.0,31.0,gp-5,,,,,\n", "?dryRun=false");
        var (_, report) = await Import(api, admin.Cookie, Header + "compounds,,Completely Different Name,,,Cairo,Egypt,31.5,32.5,gp-5,,,,,\n", "?dryRun=false");
        Assert.Equal("merged", Outcomes(report));
        Assert.Equal("same_key", report.GetProperty("items")[0].GetProperty("reason").GetString());
        Assert.Equal(1, Count(api));
    }

    [MongoFact]
    public async Task Two_rows_of_one_file_for_one_place_make_one_place_in_a_dry_run_and_a_real_run()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var file = Header + "compounds,,Madinaty,,,Cairo,Egypt,30.10,31.64,,,,,,\n" + "compounds,مدينتي,Madinaty,,,Cairo,Egypt,30.1002,31.6401,,,,,,\n";
        var (_, dry) = await Import(api, admin.Cookie, file);
        var (_, real) = await Import(api, admin.Cookie, file, "?dryRun=false");
        Assert.Equal("created,merged", Outcomes(dry));
        Assert.Equal(Outcomes(dry), Outcomes(real));                // the dry run said exactly what the real run did
        Assert.Equal(1, Count(api));
    }

    [MongoFact]
    public async Task A_similar_place_below_the_merge_score_waits_for_a_moderator_with_its_candidates()
    {
        await using var api = NewApi(new() { ["Entities:ImportMergeThreshold"] = "1.01" });   // nothing counts as certain
        var admin = await api.SignInAsync(AdminPhone);
        var existing = await SeedEntity(api);
        var (_, report) = await Import(api, admin.Cookie, Header + "compounds,,Madinaty,,,Cairo,Egypt,30.1001,31.6402,,,,,,\n", "?dryRun=false");

        Assert.Equal("review", Outcomes(report));
        Assert.Equal(1, report.GetProperty("needsReview").GetInt32());
        var id = report.GetProperty("items")[0].GetProperty("entityId").GetString();
        var created = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(id!))!;
        Assert.Equal(EntityStatus.PendingModeration, created.Status);     // not public
        Assert.Equal(existing.Id, created.DuplicateCheck!.Candidates[0].EntityId);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/entities/{id}")).Response.StatusCode);
        Assert.Contains(id, (await Send(api, HttpMethod.Get, "/api/moderation/entities", admin.Cookie)).Json.GetRawText());
    }

    [MongoFact]
    public async Task Different_places_with_a_shared_word_are_both_created()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var file = Header + "compounds,,Sodic East,,,Cairo,Egypt,30.0,31.0,,,,,,\n" + "compounds,,Sodic West,,,Giza,Egypt,30.01,31.01,,,,,,\n" + "compounds,,Sodic East,,,Alexandria,Egypt,31.2,29.9,,,,,,\n";
        var (_, report) = await Import(api, admin.Cookie, file, "?dryRun=false");
        Assert.Equal("created,created,created", Outcomes(report));    // a chain's other branch, far away, is its own place
        Assert.Equal(3, Count(api));
    }

    [MongoFact]
    public async Task Bad_rows_are_skipped_with_a_reason_and_the_good_rows_still_go_in()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var file = Header +
                   "boats,,A Boat,,,Cairo,Egypt,30.0,31.0,,,,,,\n" +
                   "compounds,,,,,Cairo,Egypt,30.0,31.0,,,,,,\n" +
                   "compounds,,No Place,,,Cairo,Egypt,95,31.0,,,,,,\n" +
                   "compounds,,No Lat,,,Cairo,Egypt,,31.0,,,,,,\n" +
                   "compounds,,Bad Field,,,Cairo,Egypt,30.0,31.0,,,,,,unknown_field:x\n" +
                   "compounds,,Good One,,,Cairo,Egypt,30.0,31.0,,,,,,\n";
        var (_, report) = await Import(api, admin.Cookie, file, "?dryRun=false");
        Assert.Equal("skipped,skipped,skipped,skipped,skipped,created", Outcomes(report));
        var reasons = report.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reason").ValueKind == JsonValueKind.Null ? "" : i.GetProperty("reason").GetString()).ToList();
        Assert.Equal(["invalid_category", "invalid_name", "invalid_location", "invalid_location", "invalid_field:unknown_field", ""], reasons);
        Assert.Equal(5, report.GetProperty("skipped").GetInt32());
        Assert.Equal(1, Count(api));
    }

    [MongoFact]
    public async Task Imported_contact_of_a_gated_category_stays_hidden_like_any_other_contact()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var (_, report) = await Import(api, admin.Cookie,
            Header + "service-providers,أحمد السباك,Ahmed Plumber,,Maadi,Cairo,Egypt,29.96,31.25,,+20 100 123 4567,+201001234567,https://plumber.example,,trade:Plumber\n", "?dryRun=false");
        var id = report.GetProperty("items")[0].GetProperty("entityId").GetString();
        var stored = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(id!))!;
        Assert.Equal("+20 100 123 4567", stored.Contact!.Phone);       // kept, so an owner with the plan can show it
        Assert.Empty(stored.SameAs);                                     // the website is a link of the contact, not a public link

        var raw = (await Send(api, HttpMethod.Get, $"/api/entities/{id}")).Json.GetRawText();
        Assert.DoesNotContain("100 123", raw);
        Assert.DoesNotContain("plumber.example", raw);
        Assert.Contains("\"contactLocked\":true", raw);
    }

    [MongoFact]
    public async Task A_file_over_the_size_limit_is_refused()
    {
        await using var api = NewApi(new() { ["Entities:ImportMaxBytes"] = "200" });
        var admin = await api.SignInAsync(AdminPhone);
        var (response, body) = await Import(api, admin.Cookie, Header + string.Concat(Enumerable.Repeat("compounds,,Some Name,,,Cairo,Egypt,30.0,31.0,,,,,,\n", 20)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file_too_large", body.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task The_sample_files_can_be_downloaded_and_imported_as_they_are()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        using var http = api.CreateClient();
        var csvRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/import/sample");
        csvRequest.Headers.Add("Cookie", admin.Cookie!.Split(';')[0]);
        var sample = await (await http.SendAsync(csvRequest)).Content.ReadAsStringAsync();
        var (_, report) = await Import(api, admin.Cookie, sample, "?dryRun=false");
        Assert.Equal("created,created,created", Outcomes(report));
        Assert.Equal(3, Count(api));
    }
}
