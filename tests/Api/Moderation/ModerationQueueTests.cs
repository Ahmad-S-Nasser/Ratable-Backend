using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Moderation.Services;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Moderation;

/// <summary>The moderator queue and the decisions: who may use it, what they see, what a decision does, what is logged.</summary>
public sealed class ModerationQueueTests
{
    private sealed record World(IdentityApiFactory Api, IdentityApiFactory.SignedIn Admin, IdentityApiFactory.SignedIn Author, string EntityId, string ReviewId);

    private static async Task<World> Setup(PrescreenAnswer? answer = null, bool anonymous = false, string text = "The lifts are slow in the evening and nobody fixes them")
    {
        var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var author = await api.SignInAsync("+201111111111");
        await SetProfile(api, author.Cookie!, "Hidden Person", "https://cdn.example.com/hidden.png");
        var entity = await SeedEntity(api);
        var review = await NewReview(api, author, entity.Id, answer ?? Clean(), text, anonymous: anonymous);
        return new World(api, admin, author, entity.Id, review);
    }

    private static Task<(HttpResponseMessage Response, JsonElement Json)> Queue(World w, string? cookie = null, string query = "") =>
        Send(w.Api, HttpMethod.Get, "/api/moderation/cases" + query, cookie ?? w.Admin.Cookie);

    private static async Task<string> CaseIdOf(World w) => (await CaseFor(w.Api, w.ReviewId)).Id;

    // ---- who may use it ----

    [MongoFact]
    public async Task Only_moderators_and_admins_can_use_the_queue()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var id = await CaseIdOf(w);
        var other = await w.Api.SignInAsync("+201222222222");
        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, "/api/moderation/overview"), (HttpMethod.Get, "/api/moderation/cases"), (HttpMethod.Post, $"/api/moderation/cases/{id}/publish"),
            (HttpMethod.Post, $"/api/moderation/cases/{id}/hide"), (HttpMethod.Get, "/api/moderation/reports"), (HttpMethod.Get, "/api/moderation/disputes"),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Api, method, url, null, new { reason = "x" })).Response.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, method, url, other.Cookie, new { reason = "x" })).Response.StatusCode);
        }
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(w.Api, w.ReviewId)).Status);
    }

    [MongoFact]
    public async Task A_moderator_who_is_not_an_admin_can_work_the_queue_but_not_the_admin_pages()
    {
        var w = await Setup();
        await using var _ = w.Api;
        w.Api.Time.Advance(TimeSpan.FromMinutes(2));
        var moderator = await w.Api.SignInAsync("+201444444444");
        await Send(w.Api, HttpMethod.Put, $"/api/admin/users/{IdOf(moderator)}/roles", w.Admin.Cookie, new { roles = new[] { "Moderator" } });

        Assert.Equal(HttpStatusCode.OK, (await Queue(w, moderator.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{await CaseIdOf(w)}/publish", moderator.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, "/api/admin/audit", moderator.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, "/api/admin/users", moderator.Cookie)).Response.StatusCode);
    }

    // ---- what a moderator sees ----

    [MongoFact]
    public async Task The_queue_shows_the_content_why_it_is_here_and_what_the_ai_said()
    {
        var w = await Setup(Flagged(spam: 0.8));
        await using var _ = w.Api;

        var (response, json) = await Queue(w);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        var item = json.GetProperty("items")[0];
        Assert.Equal("Review", item.GetProperty("kind").GetString());
        Assert.Equal("Human", item.GetProperty("route").GetString());
        Assert.Equal(["spam"], item.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("completed", item.GetProperty("aiStatus").GetString());
        Assert.True(item.GetProperty("prescreen").GetProperty("spam").GetProperty("flagged").GetBoolean());
        Assert.Equal("fake", item.GetProperty("prescreen").GetProperty("model").GetString());
        Assert.Equal("The lifts are slow in the evening and nobody fixes them", item.GetProperty("content").GetProperty("text").GetString());
        Assert.False(item.GetProperty("authorPhoneVerified").GetBoolean());
    }

    [MongoFact]
    public async Task A_moderator_sees_what_changed_in_an_edited_review()
    {
        var w = await Setup();
        await using var _ = w.Api;
        w.Api.Ai.Enqueue(Clean());
        await Send(w.Api, HttpMethod.Put, $"/api/reviews/{w.ReviewId}", w.Author.Cookie, new { answers = Answers(("security", 1)), text = "Now the lifts are broken completely" });

        var item = (await Queue(w)).Json.GetProperty("items")[0];

        Assert.Equal(2, item.GetProperty("revision").GetInt32());
        Assert.Equal("Now the lifts are broken completely", item.GetProperty("content").GetProperty("text").GetString());
        Assert.Equal("The lifts are slow in the evening and nobody fixes them", item.GetProperty("content").GetProperty("previousText").GetString());
        Assert.Equal(1, (await Queue(w)).Json.GetProperty("total").GetInt32()); // the old version's case is gone from the queue
    }

    [MongoFact]
    public async Task The_queue_never_shows_who_wrote_an_item_not_even_for_a_named_one_and_never_a_phone()
    {
        var w = await Setup(anonymous: true);
        await using var _ = w.Api;
        await Send(w.Api, HttpMethod.Post, $"/api/reviews/{w.ReviewId}/comments", w.Author.Cookie, new { text = "an anonymous comment on my own review", isAnonymous = true });

        var text = (await Queue(w)).Response.Content.ReadAsStringAsync().Result;

        foreach (var secret in new[] { IdOf(w.Author), "Hidden Person", "hidden.png", "1111111111" })
            Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("authorId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", text, StringComparison.OrdinalIgnoreCase);
    }

    [MongoFact]
    public async Task The_queue_can_be_filtered_by_kind_and_paged_oldest_first()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var ids = new List<string> { w.ReviewId };
        for (var i = 0; i < 2; i++)
        {
            w.Api.Time.Advance(TimeSpan.FromMinutes(2));
            var user = await w.Api.SignInAsync($"+20122000000{i}");
            ids.Add(await NewReview(w.Api, user, w.EntityId, Clean(), $"another review number {i} with its own unique words {Guid.NewGuid():N}"));
        }
        w.Api.Time.Advance(TimeSpan.FromMinutes(2));
        await Send(w.Api, HttpMethod.Post, $"/api/reviews/{await PublishFirst(w)}/comments", w.Author.Cookie, new { text = "a comment waiting for a person" });

        var all = (await Queue(w)).Json;
        var reviews = (await Queue(w, query: "?kind=review")).Json;
        var comments = (await Queue(w, query: "?kind=comment")).Json;
        var page = (await Queue(w, query: "?kind=review&skip=1&limit=1")).Json;

        Assert.Equal(4, all.GetProperty("total").GetInt32());
        Assert.Equal(3, reviews.GetProperty("total").GetInt32());
        Assert.Equal(1, comments.GetProperty("total").GetInt32());
        Assert.Equal(ids, reviews.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("contentId").GetString()));
        Assert.Equal(ids[1], page.GetProperty("items")[0].GetProperty("contentId").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Queue(w, query: "?kind=entity")).Response.StatusCode);
    }

    private static async Task<string> PublishFirst(World w)
    {
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Published); // comments are only allowed on public reviews
        return w.ReviewId;
    }

    // ---- decisions ----

    [MongoFact]
    public async Task Publishing_makes_the_review_public_unverified_and_is_logged_with_the_model()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var id = await CaseIdOf(w);

        var (response, json) = await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{id}/publish", w.Admin.Cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Published", json.GetProperty("decision").GetString());
        var review = await ReviewOf(w.Api, w.ReviewId);
        Assert.Equal(ReviewStatus.Published, review.Status);
        Assert.Equal(VerificationLevel.Unverified, review.Verification.Level);
        var listed = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json.EnumerateArray().Single();
        Assert.False(listed.GetProperty("verified").GetBoolean());
        var log = await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "moderation.published")).SingleAsync();
        Assert.Equal(w.ReviewId, log["targetId"].AsString);
        Assert.Equal(IdOf(w.Admin), log["actor"].AsString);
        Assert.Equal("fake", log["model"].AsString);
        var c = await Cases(w.Api).GetAsync(id);
        Assert.Equal(IdOf(w.Admin), c!.DecidedBy);
    }

    [MongoFact]
    public async Task Hiding_needs_a_written_reason_hides_the_review_and_tells_the_author_why()
    {
        var w = await Setup(Flagged(personalData: 0.9));
        await using var _ = w.Api;
        var id = await CaseIdOf(w);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{id}/hide", w.Admin.Cookie, new { reason = "  " })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{id}/hide", w.Admin.Cookie, new { reason = new string('x', 501) })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{id}/hide", w.Admin.Cookie, new { reason = "Contains a phone number" })).Response.StatusCode);

        Assert.Equal(ReviewStatus.Hidden, (await ReviewOf(w.Api, w.ReviewId)).Status);
        Assert.Empty((await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json.EnumerateArray());
        var mine = (await Send(w.Api, HttpMethod.Get, "/api/me/reviews", w.Author.Cookie)).Json.EnumerateArray().Single();
        Assert.Equal("Hidden", mine.GetProperty("status").GetString());
        Assert.Equal("Contains a phone number", mine.GetProperty("moderationReason").GetString());
        var publicText = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Response.Content.ReadAsStringAsync().Result;
        Assert.DoesNotContain("phone number", publicText);
        var log = await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "moderation.hidden")).SingleAsync();
        Assert.Equal("Contains a phone number", log["reason"].AsString);
    }

    [MongoFact]
    public async Task A_decided_case_cannot_be_decided_again_and_an_unknown_one_is_not_found()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var id = await CaseIdOf(w);
        await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{id}/publish", w.Admin.Cookie);

        Assert.Equal(HttpStatusCode.Conflict, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{id}/hide", w.Admin.Cookie, new { reason = "changed my mind" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{NewId()}/publish", w.Admin.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Post, "/api/moderation/cases/not-an-id/publish", w.Admin.Cookie)).Response.StatusCode);
        Assert.Equal(ReviewStatus.Published, (await ReviewOf(w.Api, w.ReviewId)).Status);
        Assert.Equal(0, (await Queue(w)).Json.GetProperty("total").GetInt32());
    }

    [MongoFact]
    public async Task A_hidden_review_the_author_edits_comes_back_to_the_queue_as_a_new_version()
    {
        var w = await Setup(Flagged(abuse: 0.9));
        await using var _ = w.Api;
        await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{await CaseIdOf(w)}/hide", w.Admin.Cookie, new { reason = "Abusive" });
        w.Api.Ai.Enqueue(Clean());

        var (_, json) = await Send(w.Api, HttpMethod.Put, $"/api/reviews/{w.ReviewId}", w.Author.Cookie, new { answers = Answers(("security", 2)), text = "The lifts are slow, I would like them fixed" });

        Assert.Equal("PendingModeration", json.GetProperty("status").GetString());
        Assert.Equal(1, (await Queue(w)).Json.GetProperty("total").GetInt32());
        Assert.Equal(2, (await Queue(w)).Json.GetProperty("items")[0].GetProperty("revision").GetInt32());
    }

    [MongoFact]
    public async Task A_case_for_a_version_that_was_edited_meanwhile_cannot_be_decided()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var oldCase = await CaseIdOf(w);
        w.Api.Ai.Enqueue(Clean());
        await Send(w.Api, HttpMethod.Put, $"/api/reviews/{w.ReviewId}", w.Author.Cookie, new { answers = Answers(("security", 1)), text = "A different text altogether now" });

        var result = await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{oldCase}/publish", w.Admin.Cookie);

        Assert.Equal(HttpStatusCode.Conflict, result.Response.StatusCode);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(w.Api, w.ReviewId)).Status); // the new version still has to be read
    }

    [MongoFact]
    public async Task Comments_are_decided_in_the_same_queue()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Published);
        w.Api.Time.Advance(TimeSpan.FromMinutes(2));
        var other = await w.Api.SignInAsync("+201222222222");
        w.Api.Ai.Enqueue(Flagged(abuse: 0.9));
        var (_, c) = await Send(w.Api, HttpMethod.Post, $"/api/reviews/{w.ReviewId}/comments", other.Cookie, new { text = "you are all idiots here" });
        var commentId = c.GetProperty("id").GetString()!;
        var caseId = (await CaseFor(w.Api, commentId)).Id;

        await Send(w.Api, HttpMethod.Post, $"/api/moderation/cases/{caseId}/hide", w.Admin.Cookie, new { reason = "Insults" });

        Assert.Empty((await Send(w.Api, HttpMethod.Get, $"/api/reviews/{w.ReviewId}/comments")).Json.EnumerateArray());
        var mine = (await Send(w.Api, HttpMethod.Get, "/api/me/comments", other.Cookie)).Json.EnumerateArray().Single();
        Assert.Equal("Hidden", mine.GetProperty("status").GetString());
    }

    // ---- reports ----

    [MongoFact]
    public async Task Reports_are_grouped_per_item_and_dismissing_closes_them_all()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Published);
        var reporters = new List<IdentityApiFactory.SignedIn>();
        for (var i = 0; i < 3; i++)
        {
            w.Api.Time.Advance(TimeSpan.FromMinutes(2));
            var r = await w.Api.SignInAsync($"+20133000000{i}");
            reporters.Add(r);
            await Send(w.Api, HttpMethod.Post, "/api/reports", r.Cookie, new { targetType = "Review", targetId = w.ReviewId, reason = i == 0 ? "spam" : "abuse", details = i == 1 ? "looks fake" : null });
        }

        var (_, groups) = await Send(w.Api, HttpMethod.Get, "/api/moderation/reports", w.Admin.Cookie);

        var group = groups.EnumerateArray().Single();
        Assert.Equal(3, group.GetProperty("reportCount").GetInt32());
        Assert.Equal(w.ReviewId, group.GetProperty("targetId").GetString());
        Assert.Equal(["abuse", "spam"], group.GetProperty("reasons").EnumerateArray().Select(r => r.GetProperty("reason").GetString()).Order());
        Assert.Contains("looks fake", group.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Contains("lifts are slow", group.GetProperty("content").GetProperty("text").GetString());
        var text = groups.GetRawText();
        foreach (var r in reporters) Assert.DoesNotContain(IdOf(r), text); // reporters are not named either

        var resolveId = group.GetProperty("resolveId").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/reports/{resolveId}/resolve", w.Admin.Cookie, new { action = "dismiss" })).Response.StatusCode);

        Assert.Empty((await Send(w.Api, HttpMethod.Get, "/api/moderation/reports", w.Admin.Cookie)).Json.EnumerateArray());
        Assert.Equal(ReviewStatus.Published, (await ReviewOf(w.Api, w.ReviewId)).Status);
        Assert.Equal(3, await w.Api.Database.GetCollection<Report>(Collections.Reports).CountDocumentsAsync(r => r.Status == ReportStatus.Dismissed));
        Assert.Single(await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "report.dismissed")).ToListAsync());
    }

    [MongoFact]
    public async Task Upholding_reports_needs_a_reason_hides_the_item_and_closes_every_report_on_it()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Published);
        for (var i = 0; i < 2; i++)
        {
            w.Api.Time.Advance(TimeSpan.FromMinutes(2));
            var r = await w.Api.SignInAsync($"+20133000000{i}");
            await Send(w.Api, HttpMethod.Post, "/api/reports", r.Cookie, new { targetType = "Review", targetId = w.ReviewId, reason = "fake" });
        }
        var resolveId = (await Send(w.Api, HttpMethod.Get, "/api/moderation/reports", w.Admin.Cookie)).Json[0].GetProperty("resolveId").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/reports/{resolveId}/resolve", w.Admin.Cookie, new { action = "hide" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/reports/{resolveId}/resolve", w.Admin.Cookie, new { action = "burn", reason = "x" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/reports/{resolveId}/resolve", w.Admin.Cookie, new { action = "hide", reason = "Fake review by the owner" })).Response.StatusCode);

        var review = await ReviewOf(w.Api, w.ReviewId);
        Assert.Equal(ReviewStatus.Hidden, review.Status);
        Assert.Equal("Fake review by the owner", review.ModerationReason);
        Assert.Equal(2, await w.Api.Database.GetCollection<Report>(Collections.Reports).CountDocumentsAsync(r => r.Status == ReportStatus.Upheld));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/reports/{resolveId}/resolve", w.Admin.Cookie, new { action = "dismiss" })).Response.StatusCode);
    }

    // ---- overview ----

    [MongoFact]
    public async Task The_overview_counts_every_queue_and_how_much_was_decided_by_rules_or_people()
    {
        var w = await Setup();
        await using var _ = w.Api;
        w.Api.Time.Advance(TimeSpan.FromMinutes(2));
        var verified = await w.Api.SignInAsync("+201222222222");
        await VerifyPhone(w.Api, verified);
        await NewReview(w.Api, verified, w.EntityId, Clean(), "a second review that the rules publish by themselves");
        await SeedEntity(w.Api, status: Ratable.Modules.Entities.Models.EntityStatus.PendingModeration);

        var (_, overview) = await Send(w.Api, HttpMethod.Get, "/api/moderation/overview", w.Admin.Cookie);

        var queues = overview.GetProperty("queues").EnumerateArray().ToDictionary(q => q.GetProperty("name").GetString()!, q => q.GetProperty("count").GetInt64());
        Assert.Equal(1, queues["reviews"]);
        Assert.Equal(0, queues["comments"]);
        Assert.Equal(0, queues["reports"]);
        Assert.Equal(0, queues["disputes"]);
        Assert.Equal(1, queues["entities"]);
        Assert.Equal(0, queues["proofs"]);
        Assert.Equal(1, overview.GetProperty("decidedAutomatically30Days").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, overview.GetProperty("oldestOpenCaseAt").ValueKind);

    }
}
