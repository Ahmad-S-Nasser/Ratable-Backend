using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Comments.Models;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Audit;
using Ratable.Shared.Data;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Anonymity;

public sealed class ReportAndLookupTests
{
    private const string AdminPhone = "+201000000001";

    private static IdentityApiFactory Api(Dictionary<string, string>? extra = null, Action<IServiceCollection>? configure = null)
    {
        var settings = new Dictionary<string, string> { ["Identity:BootstrapAdminPhone"] = AdminPhone };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new IdentityApiFactory(settings, configure: configure);
    }

    private static object Report(string type, string? id, string reason = "abuse", string? details = null) => new { targetType = type, targetId = id, reason, details };

    private static async Task<string> PublishedComment(IdentityApiFactory api, string cookie, string reviewId, bool anonymous)
    {
        var (_, c) = await Send(api, HttpMethod.Post, $"/api/reviews/{reviewId}/comments", cookie, new { text = "a comment", isAnonymous = anonymous });
        var id = c.GetProperty("id").GetString()!;
        var repo = api.Services.GetRequiredService<CommentRepository>();
        var comment = (await repo.GetAsync(id))!;
        comment.Status = CommentStatus.Published;
        await repo.ReplaceAsync(comment);
        return id;
    }

    // ---- reports ----

    [MongoFact]
    public async Task A_reader_can_report_a_public_review_or_comment_once()
    {
        await using var api = Api();
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var reader = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var review = await CreateReview(api, author.Cookie!, entity.Id);
        var comment = await PublishedComment(api, author.Cookie!, review, anonymous: true);

        var (onReview, json) = await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", review, "personal_data", "has a phone number"));
        var (onComment, _) = await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("comment", comment, "spam"));
        var (again, error) = await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", review, "abuse"));

        Assert.Equal(HttpStatusCode.Created, onReview.StatusCode);
        Assert.Equal(HttpStatusCode.Created, onComment.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_reported", error.GetProperty("error").GetString());
        var stored = await api.Database.GetCollection<BsonDocument>(Collections.Reports).Find(new BsonDocument()).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, r => Assert.Equal("Open", r["status"].AsString));
        Assert.Contains(json.GetProperty("id").GetString()!, stored.Select(r => r["_id"].AsObjectId.ToString()));
    }

    [MongoFact]
    public async Task A_report_never_hides_anything_by_itself()
    {
        await using var api = Api();
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var reader = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var review = await CreateReview(api, author.Cookie!, entity.Id);

        await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", review));

        Assert.Single((await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews")).Json.EnumerateArray());
    }

    [MongoFact]
    public async Task Bad_reports_are_refused()
    {
        await using var api = Api();
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var reader = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var review = await CreateReview(api, author.Cookie!, entity.Id);
        var pending = await CreateReview(api, reader.Cookie!, (await SeedEntity(api)).Id, state: ReviewStatus.PendingModeration);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, "/api/reports", null, Report("Review", review))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Entity", review))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", review, "because"))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", review, "abuse", new string('x', 501)))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", NewId()))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", "not-an-id"))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Post, "/api/reports", author.Cookie, Report("Review", pending))).Response.StatusCode); // not public
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, "/api/reports", author.Cookie, Report("Review", review))).Response.StatusCode);  // your own
        Assert.Equal(0, await api.Database.GetCollection<BsonDocument>(Collections.Reports).CountDocumentsAsync(new BsonDocument()));
    }

    [MongoFact]
    public async Task A_reader_can_only_file_so_many_reports_a_day()
    {
        await using var api = Api(new() { ["Moderation:MaxReportsPerDay"] = "2" });
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var reader = await api.SignInAsync("+201222222222");
        var reviews = new List<string>();
        for (var i = 0; i < 3; i++) reviews.Add(await CreateReview(api, i == 0 ? author.Cookie! : (await api.SignInAsync($"+20133333333{i}")).Cookie!, (await SeedEntity(api)).Id));

        Assert.Equal(HttpStatusCode.Created, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", reviews[0]))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", reviews[1]))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(api, HttpMethod.Post, "/api/reports", reader.Cookie, Report("Review", reviews[2]))).Response.StatusCode);
    }

    // ---- the audited look-up of an anonymous author ----

    private sealed record Scene(IdentityApiFactory Api, IdentityApiFactory.SignedIn Admin, IdentityApiFactory.SignedIn Author, string AuthorId, string ReviewId, string CommentId);

    private static async Task<Scene> Scene_(Action<IServiceCollection>? configure = null)
    {
        var api = Api(configure: configure);
        var admin = await api.SignInAsync(AdminPhone);
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var author = await api.SignInAsync("+201111111111");
        await SetProfile(api, author.Cookie!, "Hidden Person", "https://cdn.example.com/hidden.png");
        var entity = await SeedEntity(api);
        var review = await CreateReview(api, author.Cookie!, entity.Id, new { answers = Answers(("security", 2)), text = "anon", isAnonymous = true });
        var comment = await PublishedComment(api, author.Cookie!, review, anonymous: true);
        return new Scene(api, admin, author, author.Body.GetProperty("user").GetProperty("id").GetString()!, review, comment);
    }

    private static Task<(HttpResponseMessage Response, JsonElement Json)> Lookup(Scene s, string? cookie, string type, string? id, string? reason) =>
        Send(s.Api, HttpMethod.Post, "/api/admin/anonymous-author-lookups", cookie, new { targetType = type, targetId = id, reason });

    [MongoFact]
    public async Task An_admin_with_a_reason_can_see_who_wrote_an_anonymous_review_or_comment_and_it_is_logged()
    {
        var s = await Scene_();
        await using var _ = s.Api;

        var (response, json) = await Lookup(s, s.Admin.Cookie, "Review", s.ReviewId, "Court order 2026/114 asks who wrote this");
        var (onComment, comment) = await Lookup(s, s.Admin.Cookie, "comment", s.CommentId, "Harassment complaint, ticket 88");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(s.AuthorId, json.GetProperty("userId").GetString());
        Assert.Equal("Hidden Person", json.GetProperty("displayName").GetString());
        Assert.Equal("Active", json.GetProperty("accountStatus").GetString());
        Assert.Equal(HttpStatusCode.OK, onComment.StatusCode);
        Assert.Equal(s.AuthorId, comment.GetProperty("userId").GetString());
        var log = await s.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument()).SortBy(d => d["_id"]).ToListAsync();
        Assert.Equal(["anonymous_author.lookup", "anonymous_author.lookup"], log.Select(l => l["action"].AsString));
        Assert.Equal([s.ReviewId, s.CommentId], log.Select(l => l["targetId"].AsString));
        Assert.Equal(["review", "comment"], log.Select(l => l["targetType"].AsString));
        Assert.Equal("Court order 2026/114 asks who wrote this", log[0]["reason"].AsString);
        Assert.All(log, l => Assert.Equal(s.Admin.Body.GetProperty("user").GetProperty("id").GetString(), l["actor"].AsString));
    }

    [MongoFact]
    public async Task The_audit_entry_and_the_answer_never_hold_a_phone_number_and_the_entry_does_not_hold_the_author()
    {
        var s = await Scene_();
        await using var _ = s.Api;

        var (_, json) = await Lookup(s, s.Admin.Cookie, "Review", s.ReviewId, "Legal request, reference 12345");

        var answer = json.GetRawText();
        Assert.DoesNotContain("phone", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1111111111", answer);
        var entry = (await s.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument()).SingleAsync()).ToJson();
        Assert.DoesNotContain(s.AuthorId, entry);
        Assert.DoesNotContain("Hidden Person", entry);
    }

    [MongoFact]
    public async Task A_look_up_needs_a_real_reason_and_only_an_admin_can_do_it()
    {
        var s = await Scene_();
        await using var _ = s.Api;
        var grant = await Send(s.Api, HttpMethod.Put, $"/api/admin/users/{s.AuthorId}/roles", s.Admin.Cookie, new { roles = new[] { "Moderator" } });
        Assert.Equal(HttpStatusCode.OK, grant.Response.StatusCode);

        foreach (var reason in new string?[] { null, "", "   ", "why", "too short" })
            Assert.Equal("reason_required", (await Lookup(s, s.Admin.Cookie, "Review", s.ReviewId, reason)).Json.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Lookup(s, s.Admin.Cookie, "Review", s.ReviewId, new string('x', 501))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Lookup(s, null, "Review", s.ReviewId, "A perfectly good reason")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Lookup(s, s.Author.Cookie, "Review", s.ReviewId, "A perfectly good reason")).Response.StatusCode); // a moderator is not an admin
        Assert.Equal(0, await s.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).CountDocumentsAsync(new BsonDocument { { "action", "anonymous_author.lookup" } }));
    }

    [MongoFact]
    public async Task A_named_or_missing_item_is_not_looked_up()
    {
        var s = await Scene_();
        await using var _ = s.Api;
        await Send(s.Api, HttpMethod.Put, $"/api/reviews/{s.ReviewId}/anonymity", s.Author.Cookie, new { isAnonymous = false });

        Assert.Equal("not_anonymous", (await Lookup(s, s.Admin.Cookie, "Review", s.ReviewId, "A perfectly good reason")).Json.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(s, s.Admin.Cookie, "Review", NewId(), "A perfectly good reason")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Lookup(s, s.Admin.Cookie, "Entity", s.ReviewId, "A perfectly good reason")).Response.StatusCode);
    }

    [MongoFact]
    public async Task If_the_audit_log_cannot_be_written_nothing_is_revealed()
    {
        var s = await Scene_(services => services.AddSingleton<IAuditLog, BrokenAuditLog>());
        await using var _ = s.Api;

        var (response, _) = await Lookup(s, s.Admin.Cookie, "Review", s.ReviewId, "A perfectly good reason");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(s.AuthorId, text);
        Assert.DoesNotContain("Hidden Person", text);
    }

    private sealed class BrokenAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) => throw new InvalidOperationException("audit store down");
    }
}
