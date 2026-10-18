using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.Businesses.BusinessKit;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Businesses;

public sealed class ReplyAndDashboardTests
{
    private sealed record World(IdentityApiFactory Api, IdentityApiFactory.SignedIn Owner, IdentityApiFactory.SignedIn Reader, IdentityApiFactory.SignedIn Other, string EntityId, string ReviewId);

    private static async Task<World> Setup(bool anonymous = false, VerificationLevel level = VerificationLevel.Verified)
    {
        var api = NewApi();
        var entity = await SeedEntity(api);
        var owner = await Business(api);
        await MakeOwner(api, owner, entity.Id);
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var reader = await api.SignInAsync("+201111111111");
        await SetProfile(api, reader.Cookie!, "Mona Hassan");
        var other = await Business(api, "+201555555559");
        var review = await CreateReview(api, reader.Cookie!, entity.Id, new { answers = Answers(("security", 4)), text = "Quiet and clean, parking is hard", isAnonymous = anonymous });
        await SetReviewState(api, review, ReviewStatus.Published, level);
        return new World(api, owner, reader, other, entity.Id, review);
    }

    private static Task<(HttpResponseMessage Response, JsonElement Json)> Reply(World w, string? cookie, string? text = "Thank you, we are adding parking spaces") =>
        Send(w.Api, HttpMethod.Post, $"/api/reviews/{w.ReviewId}/owner-reply", cookie, new { text });

    // ---- replies ----

    [MongoFact]
    public async Task Only_the_owner_of_the_place_can_reply_once_and_it_shows_under_the_review()
    {
        var w = await Setup();
        await using var _ = w.Api;

        Assert.Equal(HttpStatusCode.Unauthorized, (await Reply(w, null)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reply(w, w.Reader.Cookie)).Response.StatusCode);   // a reader is not a business
        await Send(w.Api, HttpMethod.Put, $"/api/admin/users/{IdOf(w.Other)}/roles", (await w.Api.SignInAsync(AdminPhone)).Cookie, new { roles = new[] { "BusinessOwner" } });
        var (notMine, body) = await Reply(w, w.Other.Cookie);                                            // a business, but not this place
        Assert.Equal(HttpStatusCode.Forbidden, notMine.StatusCode);
        Assert.Equal("not_owner", body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Reply(w, w.Owner.Cookie, " ")).Response.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await Reply(w, w.Owner.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Reply(w, w.Owner.Cookie)).Response.StatusCode);

        var shown = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json[0];
        Assert.Equal("Thank you, we are adding parking spaces", shown.GetProperty("ownerReply").GetProperty("text").GetString());
    }

    [MongoFact]
    public async Task The_owner_can_edit_and_remove_a_reply_and_write_a_new_one()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Reply(w, w.Owner.Cookie);

        var (edited, json) = await Send(w.Api, HttpMethod.Put, $"/api/reviews/{w.ReviewId}/owner-reply", w.Owner.Cookie, new { text = "Parking is fixed now" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("editedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Delete, $"/api/reviews/{w.ReviewId}/owner-reply", w.Owner.Cookie)).Response.StatusCode);
        var gone = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json[0];
        Assert.False(gone.TryGetProperty("ownerReply", out var r) && r.ValueKind != JsonValueKind.Null);

        Assert.Equal(HttpStatusCode.Created, (await Reply(w, w.Owner.Cookie, "A fresh answer")).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_business_cannot_reply_to_a_review_that_is_not_public()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Hidden);
        Assert.Equal(HttpStatusCode.NotFound, (await Reply(w, w.Owner.Cookie)).Response.StatusCode);
    }

    // ---- the owner cannot review its own place ----

    [MongoFact]
    public async Task A_business_cannot_review_the_place_it_runs_but_can_review_others()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var (response, body) = await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.EntityId}/reviews", w.Owner.Cookie, new { answers = Answers(("security", 5)), text = "We are the best, five stars" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("own_entity", body.GetProperty("error").GetString());

        var elsewhere = await SeedEntity(w.Api);
        Assert.Equal(HttpStatusCode.Created, (await Send(w.Api, HttpMethod.Post, $"/api/entities/{elsewhere.Id}/reviews", w.Owner.Cookie, new { answers = Answers(("security", 3)), text = "Visited another compound last week" })).Response.StatusCode);
    }

    // ---- disputes are for the owner of the place ----

    [MongoFact]
    public async Task A_business_that_does_not_run_the_place_cannot_dispute_its_reviews()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Send(w.Api, HttpMethod.Put, $"/api/admin/users/{IdOf(w.Other)}/roles", (await w.Api.SignInAsync(AdminPhone)).Cookie, new { roles = new[] { "BusinessOwner" } });
        var (response, body) = await Send(w.Api, HttpMethod.Post, $"/api/reviews/{w.ReviewId}/disputes", w.Other.Cookie, new { reason = "This review is about a different compound" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_owner", body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Send(w.Api, HttpMethod.Post, $"/api/reviews/{w.ReviewId}/disputes", w.Owner.Cookie, new { reason = "This review is about a different compound" })).Response.StatusCode);
    }

    // ---- dashboard ----

    [MongoFact]
    public async Task The_dashboard_is_only_for_the_owner_of_that_place()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var url = $"/api/business/places/{w.EntityId}/dashboard";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Api, HttpMethod.Get, url)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, url, w.Reader.Cookie)).Response.StatusCode);
        await Send(w.Api, HttpMethod.Put, $"/api/admin/users/{IdOf(w.Other)}/roles", (await w.Api.SignInAsync(AdminPhone)).Cookie, new { roles = new[] { "BusinessOwner" } });
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Get, url, w.Other.Cookie)).Response.StatusCode);   // another business sees nothing about this place
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Api, HttpMethod.Get, url, w.Owner.Cookie)).Response.StatusCode);

        var places = (await Send(w.Api, HttpMethod.Get, "/api/business/places", w.Owner.Cookie)).Json;
        Assert.Equal(w.EntityId, Assert.Single(places.EnumerateArray()).GetProperty("entityId").GetString());
    }

    [MongoFact]
    public async Task The_dashboard_numbers_match_the_public_score_and_waiting_reviews_shrink_after_a_reply()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var publicScore = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/score")).Json;

        var (_, dash) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.EntityId}/dashboard", w.Owner.Cookie);
        var kpis = dash.GetProperty("kpis");
        Assert.Equal(publicScore.GetProperty("overall").GetDouble(), kpis.GetProperty("score").GetDouble());
        Assert.Equal(publicScore.GetProperty("verifiedReviews").GetInt32(), kpis.GetProperty("verifiedReviews").GetInt32());
        Assert.Equal(1, kpis.GetProperty("reviews").GetInt32());
        Assert.Equal(1, dash.GetProperty("awaitingReply").GetArrayLength());
        Assert.Equal(0, kpis.GetProperty("replyRatePercent").GetInt32());
        Assert.Equal(6, dash.GetProperty("trend").GetArrayLength());
        Assert.Equal(publicScore.GetProperty("overall").GetDouble(), dash.GetProperty("categoryAverage").GetDouble());   // the only place in its category

        await Reply(w, w.Owner.Cookie);
        var (_, after) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.EntityId}/dashboard", w.Owner.Cookie);
        Assert.Equal(0, after.GetProperty("awaitingReply").GetArrayLength());
        Assert.Equal(100, after.GetProperty("kpis").GetProperty("replyRatePercent").GetInt32());
    }

    [MongoFact]
    public async Task New_review_alerts_count_reviews_since_the_owner_last_looked()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var url = $"/api/business/places/{w.EntityId}";
        w.Api.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Post, $"{url}/seen", w.Owner.Cookie)).Response.StatusCode);
        Assert.Equal(0, (await Send(w.Api, HttpMethod.Get, $"{url}/dashboard", w.Owner.Cookie)).Json.GetProperty("newReviews").GetInt32());

        w.Api.Time.Advance(TimeSpan.FromHours(1));
        var second = await w.Api.SignInAsync("+201222222222");
        var review = await CreateReview(w.Api, second.Cookie!, w.EntityId, new { answers = Answers(("security", 2)), text = "Noisy at night" });
        await SetReviewState(w.Api, review, ReviewStatus.Published);
        Assert.Equal(1, (await Send(w.Api, HttpMethod.Get, $"{url}/dashboard", w.Owner.Cookie)).Json.GetProperty("newReviews").GetInt32());
    }

    // ---- rule 1: anonymity ----

    [MongoFact]
    public async Task The_dashboard_and_the_replies_never_reveal_an_anonymous_reviewer()
    {
        var w = await Setup(anonymous: true);
        await using var _ = w.Api;
        await Reply(w, w.Owner.Cookie);
        var readerId = IdOf(w.Reader);

        var (_, dash) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.EntityId}/dashboard", w.Owner.Cookie);
        var (_, list) = await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews");
        foreach (var text in new[] { dash.GetRawText(), list.GetRawText() })
        {
            Assert.DoesNotContain("Mona", text);
            Assert.DoesNotContain(readerId, text);
            Assert.DoesNotContain("+20111", text);
        }
        var all = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json[0];
        Assert.True(all.GetProperty("anonymous").GetBoolean());
    }

    [MongoFact]
    public async Task A_waiting_anonymous_review_has_no_author_on_the_dashboard()
    {
        var w = await Setup(anonymous: true);
        await using var _ = w.Api;
        var (_, dash) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.EntityId}/dashboard", w.Owner.Cookie);
        var waiting = Assert.Single(dash.GetProperty("awaitingReply").EnumerateArray());
        Assert.True(waiting.GetProperty("anonymous").GetBoolean());
        Assert.True(waiting.GetProperty("author").ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("Mona", dash.GetRawText());
        Assert.DoesNotContain(IdOf(w.Reader), dash.GetRawText());
    }

    // ---- rule 6: money cannot change a score ----

    [MongoFact]
    public async Task Owning_a_place_or_replying_never_changes_its_score()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var before = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/score")).Json.GetRawText();
        await Reply(w, w.Owner.Cookie);
        await Send(w.Api, HttpMethod.Post, $"/api/business/places/{w.EntityId}/seen", w.Owner.Cookie);
        Assert.Equal(before, (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/score")).Json.GetRawText());
    }
}
