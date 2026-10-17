using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;
using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Moderation;

public sealed class DisputeAndAdminTests
{
    private sealed record World(IdentityApiFactory Api, IdentityApiFactory.SignedIn Admin, IdentityApiFactory.SignedIn Author, IdentityApiFactory.SignedIn Business, string EntityId, string ReviewId);

    private static async Task<World> Setup()
    {
        var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var business = await api.SignInAsync("+201555555555");
        await Send(api, HttpMethod.Put, $"/api/admin/users/{IdOf(business)}/roles", admin.Cookie, new { roles = new[] { "BusinessOwner" } });
        var entity = await SeedEntity(api);
        var review = await CreateReview(api, author.Cookie!, entity.Id, new { answers = Answers(("security", 1)), text = "The management ignores every complaint" });
        return new World(api, admin, author, business, entity.Id, review);
    }

    private static Task<(HttpResponseMessage Response, JsonElement Json)> Raise(World w, string? cookie, string? reason = "This review is about a different compound, see attached lease") =>
        Send(w.Api, HttpMethod.Post, $"/api/reviews/{w.ReviewId}/disputes", cookie, new { reason });

    private static async Task<JsonElement> PublicReview(World w) => (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json.EnumerateArray().Single();

    // ---- disputes ----

    [MongoFact]
    public async Task A_disputed_review_stays_visible_with_an_under_review_label()
    {
        var w = await Setup();
        await using var _ = w.Api;
        Assert.False((await PublicReview(w)).GetProperty("underReview").GetBoolean());

        var (response, _) = await Raise(w, w.Business.Cookie);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var review = await PublicReview(w);
        Assert.True(review.GetProperty("underReview").GetBoolean());
        Assert.Equal("The management ignores every complaint", review.GetProperty("text").GetString());   // nothing is hidden
        Assert.Equal(1, (await Send(w.Api, HttpMethod.Get, "/api/moderation/disputes", w.Admin.Cookie)).Json.GetArrayLength());
        Assert.Single(await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "dispute.raised")).ToListAsync());
    }

    [MongoFact]
    public async Task Only_a_business_owner_can_raise_a_dispute_with_a_real_reason_once_and_not_against_its_own_review()
    {
        var w = await Setup();
        await using var _ = w.Api;

        Assert.Equal(HttpStatusCode.Unauthorized, (await Raise(w, null)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Raise(w, w.Author.Cookie)).Response.StatusCode);                 // a reader is not a business
        Assert.Equal(HttpStatusCode.BadRequest, (await Raise(w, w.Business.Cookie, "no")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Raise(w, w.Business.Cookie, null)).Response.StatusCode);
        Assert.False((await PublicReview(w)).GetProperty("underReview").GetBoolean());

        Assert.Equal(HttpStatusCode.Created, (await Raise(w, w.Business.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Raise(w, w.Business.Cookie)).Response.StatusCode);                  // one open dispute at a time
        await Send(w.Api, HttpMethod.Put, $"/api/admin/users/{IdOf(w.Author)}/roles", w.Admin.Cookie, new { roles = new[] { "BusinessOwner" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await Raise(w, w.Author.Cookie)).Response.StatusCode);                  // not its own review
    }

    [MongoFact]
    public async Task A_dispute_cannot_be_raised_against_a_review_that_is_not_public()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.PendingModeration);

        Assert.Equal(HttpStatusCode.NotFound, (await Raise(w, w.Business.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Post, $"/api/reviews/{NewId()}/disputes", w.Business.Cookie, new { reason = "A perfectly good reason" })).Response.StatusCode);
    }

    [MongoFact]
    public async Task Dismissing_a_dispute_removes_the_label_and_the_review_stays()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Raise(w, w.Business.Cookie);
        var id = (await Send(w.Api, HttpMethod.Get, "/api/moderation/disputes", w.Admin.Cookie)).Json[0].GetProperty("id").GetString();

        var (response, _) = await Send(w.Api, HttpMethod.Post, $"/api/moderation/disputes/{id}/dismiss", w.Admin.Cookie, new { reason = "The review describes this compound" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var review = await PublicReview(w);
        Assert.False(review.GetProperty("underReview").GetBoolean());
        Assert.Empty((await Send(w.Api, HttpMethod.Get, "/api/moderation/disputes", w.Admin.Cookie)).Json.EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/disputes/{id}/dismiss", w.Admin.Cookie, new { })).Response.StatusCode);
        Assert.Single(await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "dispute.dismissed")).ToListAsync());
    }

    [MongoFact]
    public async Task Upholding_a_dispute_needs_a_reason_hides_the_review_and_tells_the_author()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Raise(w, w.Business.Cookie);
        var id = (await Send(w.Api, HttpMethod.Get, "/api/moderation/disputes", w.Admin.Cookie)).Json[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/disputes/{id}/uphold", w.Admin.Cookie, new { })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/disputes/{id}/uphold", w.Admin.Cookie, new { reason = "The reviewer never lived here" })).Response.StatusCode);

        var review = await ReviewOf(w.Api, w.ReviewId);
        Assert.Equal(ReviewStatus.Hidden, review.Status);
        Assert.False(review.UnderReview);
        Assert.Equal("The reviewer never lived here", review.ModerationReason);
        var mine = (await Send(w.Api, HttpMethod.Get, "/api/me/reviews", w.Author.Cookie)).Json.EnumerateArray().Single();
        Assert.Equal("The reviewer never lived here", mine.GetProperty("moderationReason").GetString());
        Assert.Empty((await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json.EnumerateArray());
    }

    [MongoFact]
    public async Task A_business_cannot_delete_or_hide_a_review_by_any_route()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var business = w.Business.Cookie;

        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Delete, $"/api/reviews/{w.ReviewId}", business)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Put, $"/api/reviews/{w.ReviewId}/anonymity", business, new { isAnonymous = true })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, "/api/moderation/cases", business)).Response.StatusCode);
        Assert.Equal(ReviewStatus.Published, (await ReviewOf(w.Api, w.ReviewId)).Status);
    }

    // ---- the audit log ----

    [MongoFact]
    public async Task The_audit_log_is_for_admins_and_can_be_filtered_and_paged_newest_first()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Raise(w, w.Business.Cookie);
        var disputeId = (await Send(w.Api, HttpMethod.Get, "/api/moderation/disputes", w.Admin.Cookie)).Json[0].GetProperty("id").GetString();
        w.Api.Time.Advance(TimeSpan.FromMinutes(5));
        await Send(w.Api, HttpMethod.Post, $"/api/moderation/disputes/{disputeId}/dismiss", w.Admin.Cookie, new { reason = "Fine" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Api, HttpMethod.Get, "/api/admin/audit")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, "/api/admin/audit", w.Author.Cookie)).Response.StatusCode);

        var (_, all) = await Send(w.Api, HttpMethod.Get, "/api/admin/audit", w.Admin.Cookie);
        var (_, disputes) = await Send(w.Api, HttpMethod.Get, "/api/admin/audit?action=dispute.", w.Admin.Cookie);
        var (_, byActor) = await Send(w.Api, HttpMethod.Get, $"/api/admin/audit?actor={IdOf(w.Business)}", w.Admin.Cookie);
        var (_, paged) = await Send(w.Api, HttpMethod.Get, "/api/admin/audit?pageSize=1&page=2", w.Admin.Cookie);

        var actions = all.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal("dispute.dismissed", actions[0]);                                  // newest first
        Assert.Contains("dispute.raised", actions);
        Assert.Equal(["dispute.dismissed", "dispute.raised"], disputes.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()));
        Assert.Equal(["dispute.raised"], byActor.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()));
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(2, paged.GetProperty("page").GetInt32());
        Assert.Equal(all.GetProperty("total").GetInt64(), paged.GetProperty("total").GetInt64());
    }

    // ---- users ----

    [MongoFact]
    public async Task The_admin_can_list_accounts_by_role_and_never_sees_a_phone_number()
    {
        var w = await Setup();
        await using var _ = w.Api;

        var (_, all) = await Send(w.Api, HttpMethod.Get, "/api/admin/users", w.Admin.Cookie);
        var (_, businesses) = await Send(w.Api, HttpMethod.Get, "/api/admin/users?role=BusinessOwner", w.Admin.Cookie);

        Assert.Equal(3, all.GetProperty("total").GetInt64());
        Assert.Equal([IdOf(w.Business)], businesses.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetString()));
        var text = all.GetRawText();
        Assert.DoesNotContain("1111111111", text);
        Assert.DoesNotContain("phoneHash", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Get, "/api/admin/users?role=Emperor", w.Admin.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, "/api/admin/users", w.Business.Cookie)).Response.StatusCode);
    }

    [MongoFact]
    public async Task Suspending_an_account_signs_it_out_everywhere_stops_it_signing_in_and_is_logged()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var id = IdOf(w.Author);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Post, $"/api/admin/users/{id}/suspend", w.Admin.Cookie, new { })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Post, $"/api/admin/users/{id}/suspend", w.Admin.Cookie, new { reason = "Repeated abuse of the review form" })).Response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Api, HttpMethod.Get, "/api/me", w.Author.Cookie)).Response.StatusCode);
        w.Api.Time.Advance(TimeSpan.FromMinutes(5));
        using var http = w.Api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = "+201111111111" });
        var again = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = "+201111111111", code = w.Api.Sender.LastCode("+201111111111") });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        var suspended = (await Send(w.Api, HttpMethod.Get, "/api/admin/users?status=Suspended", w.Admin.Cookie)).Json.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(id, suspended.GetProperty("id").GetString());
        var log = await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "user.suspended")).SingleAsync();
        Assert.Equal("Repeated abuse of the review form", log["reason"].AsString);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Post, $"/api/admin/users/{id}/unsuspend", w.Admin.Cookie, new { reason = "Appeal accepted" })).Response.StatusCode);
        Assert.Equal(UserStatus.Active, (await w.Api.Services.GetRequiredService<UserRepository>().ListByIdsAsync([id])).Single().Status);
        Assert.Single(await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "user.unsuspended")).ToListAsync());
    }

    [MongoFact]
    public async Task An_admin_cannot_be_suspended_and_cannot_suspend_themselves_or_a_stranger_who_does_not_exist()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var adminId = IdOf(w.Admin);
        var reason = new { reason = "Because I said so, for a good reason" };

        Assert.Equal("cannot_suspend_yourself", (await Send(w.Api, HttpMethod.Post, $"/api/admin/users/{adminId}/suspend", w.Admin.Cookie, reason)).Json.GetProperty("error").GetString());
        await Send(w.Api, HttpMethod.Put, $"/api/admin/users/{IdOf(w.Business)}/roles", w.Admin.Cookie, new { roles = new[] { "Admin" } });
        Assert.Equal("cannot_suspend_an_admin", (await Send(w.Api, HttpMethod.Post, $"/api/admin/users/{IdOf(w.Business)}/suspend", w.Admin.Cookie, reason)).Json.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Post, $"/api/admin/users/{NewId()}/suspend", w.Admin.Cookie, reason)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Post, $"/api/admin/users/{adminId}/suspend", w.Author.Cookie, reason)).Response.StatusCode);
    }
}
