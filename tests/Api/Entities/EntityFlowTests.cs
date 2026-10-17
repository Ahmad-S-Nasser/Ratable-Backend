using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Entities.Services;
using Ratable.Shared.Ai;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Entities;

/// <summary>The add-an-entity flow through the HTTP API on a real database, with a fake AI client. Cairo coordinates: Madinaty is about 31.64, 30.10.</summary>
public sealed class EntityFlowTests
{
    private const string AdminPhone = "+201000000001";
    private const double Lon = 31.64, Lat = 30.10;

    private static IdentityApiFactory Api(Dictionary<string, string>? extra = null)
    {
        var settings = new Dictionary<string, string> { ["Identity:BootstrapAdminPhone"] = AdminPhone };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new IdentityApiFactory(settings, fakeAi: true);
    }

    private static object Body(string? nameAr = "أكاديمية الشروق", string? nameEn = null, double lon = Lon, double lat = Lat, string category = "schools-nurseries",
        Dictionary<string, string>? fields = null) =>
        new { nameAr, nameEn, categoryId = category, longitude = lon, latitude = lat, city = "Cairo", fields };

    private static async Task<(HttpResponseMessage Response, JsonElement Json)> Post(IdentityApiFactory api, string cookie, string url, object? body = null)
    {
        using var http = api.CreateClient();
        var response = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Post, url, cookie, body ?? new { }));
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.Length > 0 && text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static async Task<JsonElement> GetJson(IdentityApiFactory api, string url, string? cookie = null)
    {
        using var http = api.CreateClient();
        var request = cookie is null ? new HttpRequestMessage(HttpMethod.Get, url) : IdentityApiFactory.WithCookie(HttpMethod.Get, url, cookie);
        var response = await http.SendAsync(request);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<Entity> Seed(IdentityApiFactory api, string nameAr, string nameEn, double lon = Lon, double lat = Lat,
        string category = "schools-nurseries", EntityStatus status = EntityStatus.Active, Action<Entity>? configure = null)
    {
        var entity = new Entity
        {
            CategoryId = category,
            Name = new Ratable.Shared.Data.LocalizedText(nameAr, nameEn),
            Status = status,
            Location = GeoJson.Point(GeoJson.Geographic(lon, lat)),
            City = "Cairo",
        };
        entity.SearchTerms = EntityService.BuildSearchTerms(entity);
        configure?.Invoke(entity);
        if (entity.SearchTerms.Length == 0) entity.SearchTerms = EntityService.BuildSearchTerms(entity);
        await api.Services.GetRequiredService<EntityRepository>().InsertAsync(entity);
        return entity;
    }

    // ---- submitting ----

    [MongoFact]
    public async Task A_signed_in_user_submits_an_entity_and_it_waits_for_a_moderator()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");

        var (response, json) = await Post(api, user.Cookie!, "/api/entities", Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("PendingModeration", json.GetProperty("status").GetString());
        var id = json.GetProperty("id").GetString()!;
        var stored = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(id))!;
        Assert.Equal(EntityStatus.PendingModeration, stored.Status);
        Assert.Equal("اكاديميه الشروق", stored.SearchTerms);
        Assert.NotNull(stored.SubmittedByUserId);
        // not public: not by id, not in search
        using var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/entities/{id}")).StatusCode);
        Assert.Equal("[]", (await http.GetStringAsync("/api/entities/search?q=الشروق")));
    }

    [MongoFact]
    public async Task Submitting_needs_a_session()
    {
        await using var api = Api();
        using var http = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/entities", Body())).StatusCode);
    }

    [MongoFact]
    public async Task Bad_submissions_are_refused_with_a_reason()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var cases = new (object Body, string Error)[]
        {
            (Body(category: "spaceships"), "invalid_category"),
            (Body(nameAr: "", nameEn: " "), "invalid_name"),
            (Body(nameAr: new string('ا', 121)), "invalid_name"),
            (Body(nameAr: "اسم\u0007سيء"), "invalid_name"),
            (Body(lon: 200), "invalid_location"),
            (Body(lat: -91), "invalid_location"),
            (Body(fields: new() { ["colour"] = "red" }), "invalid_field"),
            (Body(category: "hotels-resorts", fields: new() { ["stars"] = "five" }), "invalid_field"),
            (new { nameAr = "فندق", categoryId = "hotels-resorts" }, "invalid_location"),
        };

        foreach (var (body, error) in cases)
        {
            var (response, json) = await Post(api, user.Cookie!, "/api/entities", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(error, json.GetProperty("error").GetString());
        }
        Assert.Equal(0, await api.Database.GetCollection<Entity>(Collections.Entities).CountDocumentsAsync(FilterDefinition<Entity>.Empty));
    }

    [MongoFact]
    public async Task Category_specific_fields_are_accepted_when_they_belong_to_the_category()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");

        var (response, json) = await Post(api, user.Cookie!, "/api/entities",
            Body(nameAr: "فندق النيل", category: "hotels-resorts", fields: new() { ["stars"] = "5", ["priceBand"] = "$$$" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(json.GetProperty("id").GetString()!))!;
        Assert.Equal("5", stored.Fields["stars"]);
    }

    [MongoFact]
    public async Task A_user_can_only_submit_so_many_entities_a_day()
    {
        await using var api = Api(new() { ["Entities:MaxSubmissionsPerDay"] = "2" });
        var user = await api.SignInAsync("+201111111111");

        Assert.Equal(HttpStatusCode.Created, (await Post(api, user.Cookie!, "/api/entities", Body("مدرسة النور", lon: 31.1))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(api, user.Cookie!, "/api/entities", Body("مدرسة الأمل", lon: 32.1))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(api, user.Cookie!, "/api/entities", Body("مدرسة الفجر", lon: 33.1))).Response.StatusCode);

        api.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(HttpStatusCode.Created, (await Post(api, user.Cookie!, "/api/entities", Body("مدرسة الفجر", lon: 33.1))).Response.StatusCode);
    }

    // ---- duplicate candidates and the AI judge ----

    [MongoFact]
    public async Task A_spelling_variant_of_an_existing_place_nearby_becomes_a_candidate_and_the_ai_judge_is_asked()
    {
        await using var api = Api();
        var existing = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        api.Ai.Enqueue(new DuplicateJudgement("duplicate", existing.Id, 0.97));
        var user = await api.SignInAsync("+201111111111");

        var (_, json) = await Post(api, user.Cookie!, "/api/entities", Body("اكاديميه الشروق", lon: Lon + 0.002));

        var stored = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(json.GetProperty("id").GetString()!))!;
        var check = stored.DuplicateCheck!;
        var candidate = Assert.Single(check.Candidates);
        Assert.Equal(existing.Id, candidate.EntityId);
        Assert.Equal(1.0, candidate.NameScore);
        Assert.InRange(candidate.DistanceMeters!.Value, 100, 400);
        Assert.Equal(DuplicateSuggestion.Duplicate, check.Suggestion);
        Assert.Equal(existing.Id, check.SuggestedDuplicateOfId);
        Assert.Equal("completed", check.AiStatus);
        Assert.Equal(EntityStatus.PendingModeration, stored.Status); // the AI's opinion never publishes or rejects anything
        var asked = Assert.Single(api.Ai.Requests);
        Assert.Equal("entity_duplicate_judge", asked.Feature);
        Assert.Equal(stored.Id, asked.ItemReference);
    }

    [MongoFact]
    public async Task With_no_candidates_the_ai_is_not_called()
    {
        await using var api = Api();
        await Seed(api, "مدرسة النيل", "Nile School");
        var user = await api.SignInAsync("+201111111111");

        var (_, json) = await Post(api, user.Cookie!, "/api/entities", Body("أكاديمية الشروق"));

        var check = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(json.GetProperty("id").GetString()!))!.DuplicateCheck!;
        Assert.Empty(check.Candidates);
        Assert.Equal(DuplicateSuggestion.New, check.Suggestion);
        Assert.Equal("not_needed", check.AiStatus);
        Assert.Empty(api.Ai.Requests);
    }

    [MongoFact]
    public async Task Different_places_with_similar_names_are_not_candidates()
    {
        await using var api = Api();
        await Seed(api, "سوديك ايست", "Sodic East", category: "compounds");
        await Seed(api, "بالم هيلز", "Palm Hills", category: "compounds");
        var user = await api.SignInAsync("+201111111111");

        var (_, east) = await Post(api, user.Cookie!, "/api/entities", Body("سوديك ويست", "Sodic West", category: "compounds"));
        var (_, parks) = await Post(api, user.Cookie!, "/api/entities", Body("بالم باركس", "Palm Parks", category: "compounds", lon: Lon + 0.001));

        var repo = api.Services.GetRequiredService<EntityRepository>();
        Assert.Empty((await repo.GetAsync(east.GetProperty("id").GetString()!))!.DuplicateCheck!.Candidates);
        Assert.Empty((await repo.GetAsync(parks.GetProperty("id").GetString()!))!.DuplicateCheck!.Candidates);
        Assert.Empty(api.Ai.Requests);
    }

    [MongoFact]
    public async Task The_same_name_far_away_is_another_branch_not_a_candidate()
    {
        await using var api = Api();
        await Seed(api, "كافيه سيلانترو", "Cilantro Cafe", category: "restaurants-cafes", lon: 31.24, lat: 30.04); // Cairo
        var user = await api.SignInAsync("+201111111111");

        var (_, json) = await Post(api, user.Cookie!, "/api/entities", Body("كافيه سيلانترو", "Cilantro Cafe", category: "restaurants-cafes", lon: 29.92, lat: 31.20)); // Alexandria

        Assert.Empty((await api.Services.GetRequiredService<EntityRepository>().GetAsync(json.GetProperty("id").GetString()!))!.DuplicateCheck!.Candidates);
    }

    [MongoFact]
    public async Task A_place_without_a_location_is_a_candidate_only_for_an_identical_name()
    {
        await using var api = Api();
        var exact = await Seed(api, "مدرسة الأمل", "Hope School", configure: e => e.Location = null);
        await Seed(api, "مدرسة الامل الدولية", "Hope International", configure: e => e.Location = null);
        api.Ai.Enqueue(new DuplicateJudgement("new", null, 0.8));
        var user = await api.SignInAsync("+201111111111");

        var (_, json) = await Post(api, user.Cookie!, "/api/entities", Body("مدرسه الأمل"));

        var candidates = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(json.GetProperty("id").GetString()!))!.DuplicateCheck!.Candidates;
        Assert.Equal([exact.Id], candidates.Select(c => c.EntityId));
        Assert.Null(candidates[0].DistanceMeters);
    }

    [MongoFact]
    public async Task A_place_waiting_in_the_queue_also_counts_as_a_candidate_so_double_submissions_are_caught()
    {
        await using var api = Api();
        var first = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var second = await api.SignInAsync("+201222222222");
        var (_, one) = await Post(api, first.Cookie!, "/api/entities", Body("أكاديمية الشروق"));
        api.Ai.Enqueue(new DuplicateJudgement("unsure", null, 0.4));

        var (_, two) = await Post(api, second.Cookie!, "/api/entities", Body("اكاديميه الشروق"));

        var check = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(two.GetProperty("id").GetString()!))!.DuplicateCheck!;
        Assert.Equal(one.GetProperty("id").GetString(), Assert.Single(check.Candidates).EntityId);
        Assert.Equal(EntityStatus.PendingModeration, check.Candidates[0].Status);
        Assert.Equal(DuplicateSuggestion.Unsure, check.Suggestion);
    }

    [MongoFact]
    public async Task When_the_ai_is_capped_or_fails_the_entity_still_goes_to_a_moderator_with_no_suggestion()
    {
        await using var api = Api();
        await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var user = await api.SignInAsync("+201111111111");
        var repo = api.Services.GetRequiredService<EntityRepository>();

        api.Ai.DeferAll = true;
        var (_, deferred) = await Post(api, user.Cookie!, "/api/entities", Body("اكاديميه الشروق"));
        api.Ai.DeferAll = false;
        api.Ai.FailAll = true;
        var (_, failed) = await Post(api, user.Cookie!, "/api/entities", Body("اكاديمية الشروق", lon: Lon + 0.001));

        var d = (await repo.GetAsync(deferred.GetProperty("id").GetString()!))!;
        var f = (await repo.GetAsync(failed.GetProperty("id").GetString()!))!;
        Assert.Equal("deferred", d.DuplicateCheck!.AiStatus);
        Assert.Equal("failed", f.DuplicateCheck!.AiStatus);
        Assert.All(new[] { d, f }, e =>
        {
            Assert.Equal(EntityStatus.PendingModeration, e.Status);
            Assert.Equal(DuplicateSuggestion.None, e.DuplicateCheck!.Suggestion);
            Assert.NotEmpty(e.DuplicateCheck.Candidates); // the moderator still sees the candidates
        });
    }

    [MongoFact]
    public async Task An_answer_that_names_an_entity_that_is_not_a_candidate_is_not_trusted()
    {
        await using var api = Api();
        var existing = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var someoneElse = await Seed(api, "مدرسة بعيدة", "Far School", lon: 25.0, lat: 25.0);
        api.Ai.Enqueue(new DuplicateJudgement("duplicate", someoneElse.Id, 0.99)); // e.g. a name that talked the model into it
        var user = await api.SignInAsync("+201111111111");

        var (_, json) = await Post(api, user.Cookie!, "/api/entities", Body("اكاديميه الشروق"));

        var check = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(json.GetProperty("id").GetString()!))!.DuplicateCheck!;
        Assert.Equal(existing.Id, Assert.Single(check.Candidates).EntityId);
        Assert.Equal(DuplicateSuggestion.Unsure, check.Suggestion);
        Assert.Null(check.SuggestedDuplicateOfId);
    }

    // ---- the moderator queue ----

    [MongoFact]
    public async Task Only_moderators_see_the_queue_and_it_never_shows_who_submitted()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var admin = await api.SignInAsync(AdminPhone);
        var (_, submitted) = await Post(api, user.Cookie!, "/api/entities", Body());
        var userId = user.Body.GetProperty("user").GetProperty("id").GetString()!;
        using var http = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/moderation/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/moderation/entities", user.Cookie!))).StatusCode);

        var response = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/moderation/entities", admin.Cookie!));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(submitted.GetProperty("id").GetString()!, text);
        Assert.DoesNotContain(userId, text);
        Assert.DoesNotContain("submittedBy", text, StringComparison.OrdinalIgnoreCase);
    }

    [MongoFact]
    public async Task A_moderator_approves_and_the_entity_becomes_public_with_slugs_and_is_found_by_any_spelling()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var admin = await api.SignInAsync(AdminPhone);
        var (_, submitted) = await Post(api, user.Cookie!, "/api/entities", Body("أكاديمية الشروق", "Shorouk Academy"));
        var id = submitted.GetProperty("id").GetString()!;

        var (approve, _) = await Post(api, admin.Cookie!, $"/api/moderation/entities/{id}/approve");

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var pub = await GetJson(api, $"/api/entities/{id}");
        Assert.Equal("اكاديميه-الشروق", pub.GetProperty("slugs").GetProperty("ar").GetString());
        Assert.Equal("shorouk-academy", pub.GetProperty("slugs").GetProperty("en").GetString());
        Assert.Equal("أكاديمية الشروق", pub.GetProperty("name").GetProperty("ar").GetString()); // the original is shown, never the normalized copy
        Assert.DoesNotContain("submittedBy", pub.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user.Body.GetProperty("user").GetProperty("id").GetString()!, pub.GetRawText());
        foreach (var query in new[] { "أكاديمية", "اكاديميه", "الشروق", "SHOROUK" })
        {
            var found = await GetJson(api, $"/api/entities/search?q={Uri.EscapeDataString(query)}");
            Assert.Contains(found.EnumerateArray(), e => e.GetProperty("id").GetString() == id);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Post(api, admin.Cookie!, $"/api/moderation/entities/{id}/approve")).Response.StatusCode);
    }

    [MongoFact]
    public async Task Rejecting_needs_a_reason_and_keeps_the_entity_out_of_the_public_pages()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var admin = await api.SignInAsync(AdminPhone);
        var (_, submitted) = await Post(api, user.Cookie!, "/api/entities", Body());
        var id = submitted.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, admin.Cookie!, $"/api/moderation/entities/{id}/reject", new { reason = " " })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(api, admin.Cookie!, $"/api/moderation/entities/{id}/reject", new { reason = "Not a real school" })).Response.StatusCode);

        using var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/entities/{id}")).StatusCode);
        var stored = (await api.Services.GetRequiredService<EntityRepository>().GetAsync(id))!;
        Assert.Equal(EntityStatus.Rejected, stored.Status);
        Assert.Equal("Not a real school", stored.Decision!.Reason);
    }

    [MongoFact]
    public async Task Every_moderator_decision_is_written_to_the_audit_log()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var admin = await api.SignInAsync(AdminPhone);
        var adminId = admin.Body.GetProperty("user").GetProperty("id").GetString()!;
        var (_, a) = await Post(api, user.Cookie!, "/api/entities", Body("مدرسة النور", lon: 31.1));
        var (_, b) = await Post(api, user.Cookie!, "/api/entities", Body("مدرسة الأمل", lon: 32.1));
        await Post(api, admin.Cookie!, $"/api/moderation/entities/{a.GetProperty("id").GetString()}/approve");
        await Post(api, admin.Cookie!, $"/api/moderation/entities/{b.GetProperty("id").GetString()}/reject", new { reason = "Spam" });

        var log = await api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument()).SortBy(d => d["_id"]).ToListAsync();

        Assert.Equal(["entity.approved", "entity.rejected"], log.Select(l => l["action"].AsString));
        Assert.All(log, l => Assert.Equal(adminId, l["actor"].AsString));
        Assert.Equal("Spam", log[1]["reason"].AsString);
    }
}
