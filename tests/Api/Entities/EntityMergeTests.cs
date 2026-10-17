using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Entities.Services;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Verification.Data;
using Ratable.Modules.Verification.Models;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Entities;

/// <summary>How a merge works, step by step, and what stops one. See EntityService.MergeAsync.</summary>
public sealed class EntityMergeTests
{
    private const string AdminPhone = "+201000000001";

    private static IdentityApiFactory Api() => new(new() { ["Identity:BootstrapAdminPhone"] = AdminPhone }, fakeAi: true);

    private static async Task<Entity> Seed(IdentityApiFactory api, string nameAr, string nameEn, string category = "schools-nurseries",
        EntityStatus status = EntityStatus.Active, Action<Entity>? configure = null)
    {
        var entity = new Entity
        {
            CategoryId = category,
            Name = new LocalizedText(nameAr, nameEn),
            Status = status,
            Location = GeoJson.Point(GeoJson.Geographic(31.64, 30.10)),
        };
        configure?.Invoke(entity);
        entity.SearchTerms = EntityService.BuildSearchTerms(entity);
        await api.Services.GetRequiredService<EntityRepository>().InsertAsync(entity);
        return entity;
    }

    private static async Task<HttpResponseMessage> Merge(IdentityApiFactory api, string cookie, string sourceId, object body)
    {
        using var http = api.CreateClient();
        return await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Post, $"/api/moderation/entities/{sourceId}/merge", cookie, body));
    }

    private static async Task<IdentityApiFactory.SignedIn> Admin(IdentityApiFactory api) => await api.SignInAsync(AdminPhone);

    private static async Task<Review> SeedReview(IdentityApiFactory api, string entityId, string userId)
    {
        var review = new Review { EntityId = entityId, UserId = userId, CategoryId = "schools-nurseries", Status = ReviewStatus.Published, Text = "ok" };
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(review);
        return review;
    }

    // ---- the merge itself ----

    [MongoFact]
    public async Task Merging_folds_the_duplicate_into_the_target_and_keeps_what_is_worth_keeping()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var target = await Seed(api, "أكاديمية الشروق", "Shorouk Academy", configure: e =>
        {
            e.ExternalIds = new() { ["wikidata"] = "Q1" };
            e.SameAs = ["https://example.com/a"];
            e.Summary = new EntitySummary { Text = new LocalizedText("ملخص", "Summary") };
            e.City = "Cairo";
        });
        var source = await Seed(api, "اكاديميه الشروق", "Shorouk Acad.", status: EntityStatus.PendingModeration, configure: e =>
        {
            e.AlternateNames = ["Al Shorouk Academy"];
            e.ExternalIds = new() { ["wikidata"] = "Q999", ["albab"] = "A7" };
            e.SameAs = ["https://example.com/a", "https://example.com/b"];
            e.GooglePlaceId = "place-123";
            e.Address = "Madinaty";
            e.City = "Elsewhere";
        });

        var response = await Merge(api, admin.Cookie!, source.Id, new { intoEntityId = target.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var repo = api.Services.GetRequiredService<EntityRepository>();
        var t = (await repo.GetAsync(target.Id))!;
        var s = (await repo.GetAsync(source.Id))!;

        // names: the variant spelling of the target's own name is not repeated; the new ones are kept
        Assert.Equal(["Shorouk Acad.", "Al Shorouk Academy"], t.AlternateNames);
        Assert.Equal("Q1", t.ExternalIds["wikidata"]);       // the surviving entity wins a clash
        Assert.Equal("A7", t.ExternalIds["albab"]);          // and gains what it lacked
        Assert.Equal(["https://example.com/a", "https://example.com/b"], t.SameAs);
        Assert.Equal("place-123", t.GooglePlaceId);          // moved without breaking the unique place id
        Assert.Null(s.GooglePlaceId);
        Assert.Equal("Cairo", t.City);                       // existing values are not overwritten ...
        Assert.Equal("Madinaty", t.Address);                 // ... gaps are filled
        Assert.Null(t.Summary);                              // the summary no longer covers all the reviews
        Assert.Contains("shorouk acad.", t.SearchTerms); // the merged-in name is searchable
        Assert.Equal(EntityStatus.Merged, s.Status);
        Assert.Equal(target.Id, s.MergedIntoId);
        Assert.InRange((t.UpdatedAt - api.Time.GetUtcNow().UtcDateTime).Duration().TotalMilliseconds, 0, 1); // lastmod for the sitemap
    }

    [MongoFact]
    public async Task After_a_merge_the_old_id_redirects_and_the_merged_in_name_finds_the_target()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var target = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var source = await Seed(api, "مدرسة الشروق الجديدة", "New Shorouk School");
        await Merge(api, admin.Cookie!, source.Id, new { intoEntityId = target.Id });
        using var http = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var old = await http.GetAsync($"/api/entities/{source.Id}");

        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal($"/api/entities/{target.Id}", old.Headers.Location!.OriginalString);
        var found = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/entities/search?q=مدرسه الشروق الجديده"));
        Assert.Equal([target.Id], found.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
    }

    [MongoFact]
    public async Task A_chain_of_merges_redirects_straight_to_the_final_entity()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var a = await Seed(api, "مدرسة ألف", "School A");
        var b = await Seed(api, "مدرسة باء", "School B");
        var c = await Seed(api, "مدرسة جيم", "School C");
        await Merge(api, admin.Cookie!, a.Id, new { intoEntityId = b.Id });
        await Merge(api, admin.Cookie!, b.Id, new { intoEntityId = c.Id });
        using var http = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await http.GetAsync($"/api/entities/{a.Id}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/api/entities/{c.Id}", response.Headers.Location!.OriginalString);
    }

    [MongoFact]
    public async Task Reviews_and_verifications_move_to_the_target()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var target = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var source = await Seed(api, "اكاديميه الشروق", "Shorouk Acad.");
        var r1 = await SeedReview(api, source.Id, ObjectId.GenerateNewId().ToString());
        var r2 = await SeedReview(api, source.Id, ObjectId.GenerateNewId().ToString());
        var onTarget = await SeedReview(api, target.Id, ObjectId.GenerateNewId().ToString());
        var proof = new VerificationRecord { UserId = r1.UserId, ReviewId = r1.Id, EntityId = source.Id, Type = VerificationType.Proof };
        await api.Services.GetRequiredService<VerificationRepository>().InsertAsync(proof);

        var response = await Merge(api, admin.Cookie!, source.Id, new { intoEntityId = target.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reviews = api.Services.GetRequiredService<ReviewRepository>();
        foreach (var id in new[] { r1.Id, r2.Id, onTarget.Id })
            Assert.Equal(target.Id, (await reviews.GetAsync(id))!.EntityId);
        Assert.Equal(3, (await reviews.ListPublishedAsync(target.Id)).Count);
        Assert.Empty(await reviews.ListPublishedAsync(source.Id));
        Assert.Equal(target.Id, (await api.Services.GetRequiredService<VerificationRepository>().GetAsync(proof.Id))!.EntityId);
    }

    // ---- what stops a merge ----

    [MongoFact]
    public async Task A_merge_is_refused_when_one_account_reviewed_both_and_nothing_is_changed()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var target = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var source = await Seed(api, "اكاديميه الشروق", "Shorouk Acad.");
        var sameUser = ObjectId.GenerateNewId().ToString();
        var onSource = await SeedReview(api, source.Id, sameUser);
        await SeedReview(api, target.Id, sameUser);
        var other = await SeedReview(api, source.Id, ObjectId.GenerateNewId().ToString());

        var response = await Merge(api, admin.Cookie!, source.Id, new { intoEntityId = target.Id });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("merge_conflict", body.GetProperty("error").GetString());
        var conflict = body.GetProperty("conflicts").EnumerateArray().Single();
        Assert.Equal("Reviews", conflict.GetProperty("module").GetString());
        Assert.Equal("same_account_reviewed_both", conflict.GetProperty("code").GetString());
        Assert.Equal(1, conflict.GetProperty("count").GetInt32());
        Assert.DoesNotContain(sameUser, body.GetRawText()); // counts only, never who

        var reviews = api.Services.GetRequiredService<ReviewRepository>();
        Assert.Equal(source.Id, (await reviews.GetAsync(onSource.Id))!.EntityId);
        Assert.Equal(source.Id, (await reviews.GetAsync(other.Id))!.EntityId);
        Assert.Equal(EntityStatus.Active, (await api.Services.GetRequiredService<EntityRepository>().GetAsync(source.Id))!.Status);
    }

    [MongoFact]
    public async Task Bad_merges_are_refused()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var school = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var otherSchool = await Seed(api, "مدرسة النور", "Nour School");
        var hotel = await Seed(api, "فندق الشروق", "Shorouk Hotel", category: "hotels-resorts");
        var pendingTarget = await Seed(api, "مدرسة جديدة", "New School", status: EntityStatus.PendingModeration);
        var rejected = await Seed(api, "مدرسة مرفوضة", "Rejected School", status: EntityStatus.Rejected);
        var missing = ObjectId.GenerateNewId().ToString();

        Assert.Equal(HttpStatusCode.BadRequest, (await Merge(api, admin.Cookie!, school.Id, new { intoEntityId = school.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Merge(api, admin.Cookie!, school.Id, new { intoEntityId = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Merge(api, admin.Cookie!, school.Id, new { intoEntityId = missing })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Merge(api, admin.Cookie!, missing, new { intoEntityId = school.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Merge(api, admin.Cookie!, school.Id, new { intoEntityId = hotel.Id })).StatusCode);     // another category
        Assert.Equal(HttpStatusCode.Conflict, (await Merge(api, admin.Cookie!, school.Id, new { intoEntityId = pendingTarget.Id })).StatusCode); // target not public yet
        Assert.Equal(HttpStatusCode.Conflict, (await Merge(api, admin.Cookie!, rejected.Id, new { intoEntityId = otherSchool.Id })).StatusCode); // source already rejected
        var repo = api.Services.GetRequiredService<EntityRepository>();
        Assert.Equal(EntityStatus.Active, (await repo.GetAsync(school.Id))!.Status);
    }

    [MongoFact]
    public async Task A_merged_entity_cannot_be_merged_again()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var a = await Seed(api, "مدرسة ألف", "School A");
        var b = await Seed(api, "مدرسة باء", "School B");
        var c = await Seed(api, "مدرسة جيم", "School C");
        await Merge(api, admin.Cookie!, a.Id, new { intoEntityId = b.Id });

        Assert.Equal(HttpStatusCode.Conflict, (await Merge(api, admin.Cookie!, a.Id, new { intoEntityId = c.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Merge(api, admin.Cookie!, c.Id, new { intoEntityId = a.Id })).StatusCode);
    }

    [MongoFact]
    public async Task Only_moderators_can_merge()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var a = await Seed(api, "مدرسة ألف", "School A");
        var b = await Seed(api, "مدرسة باء", "School B");
        using var http = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync($"/api/moderation/entities/{a.Id}/merge", new { intoEntityId = b.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Merge(api, user.Cookie!, a.Id, new { intoEntityId = b.Id })).StatusCode);
        Assert.Equal(EntityStatus.Active, (await api.Services.GetRequiredService<EntityRepository>().GetAsync(a.Id))!.Status);
    }

    // ---- safe to repeat ----

    [MongoFact]
    public async Task A_merge_that_stopped_half_way_can_simply_be_run_again()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var target = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var source = await Seed(api, "اكاديميه الشروق", "Shorouk Acad.");
        var review = await SeedReview(api, source.Id, ObjectId.GenerateNewId().ToString());
        // Step 2 ran (the reviews moved) but the process stopped before the source was marked Merged.
        await new ReviewMergeParticipant(api.Database).MoveAsync(source.Id, target.Id);

        var response = await Merge(api, admin.Cookie!, source.Id, new { intoEntityId = target.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(target.Id, (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(review.Id))!.EntityId);
        Assert.Equal(EntityStatus.Merged, (await api.Services.GetRequiredService<EntityRepository>().GetAsync(source.Id))!.Status);
    }

    [MongoFact]
    public async Task A_merge_is_written_to_the_audit_log()
    {
        await using var api = Api();
        var admin = await Admin(api);
        var target = await Seed(api, "أكاديمية الشروق", "Shorouk Academy");
        var source = await Seed(api, "اكاديميه الشروق", "Shorouk Acad.");

        await Merge(api, admin.Cookie!, source.Id, new { intoEntityId = target.Id });

        var entry = await api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("entity.merged", entry["action"].AsString);
        Assert.Equal(source.Id, entry["targetId"].AsString);
        Assert.Contains(target.Id, entry["reason"].AsString);
    }
}
