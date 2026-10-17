using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Comments.Models;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Anonymity;

public sealed class CommentTests
{
    private sealed record World(IdentityApiFactory Api, string ReviewId, string EntityId, IdentityApiFactory.SignedIn Author, IdentityApiFactory.SignedIn Reader);

    private static async Task<World> Setup(string category = "compounds", Dictionary<string, string>? settings = null)
    {
        var api = new IdentityApiFactory(settings);
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var reader = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api, category);
        var review = await CreateReview(api, author.Cookie!, entity.Id, new { answers = Answers(category == "compounds" ? ("security", 4) : ("taste", 4)), text = "review" });
        return new World(api, review, entity.Id, author, reader);
    }

    private static Task<(HttpResponseMessage Response, JsonElement Json)> Post(World w, string cookie, object body) =>
        Send(w.Api, HttpMethod.Post, $"/api/reviews/{w.ReviewId}/comments", cookie, body);

    private static async Task Publish(World w, string commentId)
    {
        var repo = w.Api.Services.GetRequiredService<CommentRepository>();
        var c = (await repo.GetAsync(commentId))!;
        c.Status = CommentStatus.Published;
        await repo.ReplaceAsync(c);
    }

    private static async Task<JsonElement> List(World w) => (await Send(w.Api, HttpMethod.Get, $"/api/reviews/{w.ReviewId}/comments")).Json;

    // ---- writing ----

    [MongoFact]
    public async Task A_comment_waits_for_moderation_and_is_not_public_until_then()
    {
        var w = await Setup();
        await using var _ = w.Api;

        var (response, json) = await Post(w, w.Reader.Cookie!, new { text = "شكرا على المراجعة" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("PendingModeration", json.GetProperty("status").GetString());
        Assert.Empty((await List(w)).EnumerateArray());
        await Publish(w, json.GetProperty("id").GetString()!);
        Assert.Single((await List(w)).EnumerateArray());
    }

    [MongoFact]
    public async Task Commenting_needs_a_session_and_a_public_review()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.PendingModeration);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(w, null!, new { text = "hi" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(w, w.Reader.Cookie!, new { text = "hi" })).Response.StatusCode);   // the review is not public
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Get, $"/api/reviews/{w.ReviewId}/comments")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Post, "/api/reviews/not-an-id/comments", w.Reader.Cookie, new { text = "hi" })).Response.StatusCode);
    }

    [MongoFact]
    public async Task Bad_comment_text_is_refused()
    {
        var w = await Setup();
        await using var _ = w.Api;

        foreach (var text in new[] { "", "   ", new string('x', 1001), "bad\u0007text" })
        {
            var (response, json) = await Post(w, w.Reader.Cookie!, new { text });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_text", json.GetProperty("error").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(w, w.Reader.Cookie!, new { })).Response.StatusCode);
    }

    [MongoFact]
    public async Task Anonymity_of_a_comment_defaults_to_the_category_setting_unless_the_author_chooses()
    {
        var compound = await Setup("compounds");
        var cafe = await Setup("restaurants-cafes");
        await using var _1 = compound.Api;
        await using var _2 = cafe.Api;

        var (_, byDefault) = await Post(compound, compound.Reader.Cookie!, new { text = "default" });
        var (_, named) = await Post(compound, compound.Author.Cookie!, new { text = "named", isAnonymous = false });
        var (_, cafeDefault) = await Post(cafe, cafe.Reader.Cookie!, new { text = "default" });
        var (_, cafeAnonymous) = await Post(cafe, cafe.Author.Cookie!, new { text = "anon", isAnonymous = true });

        Assert.True(byDefault.GetProperty("isAnonymous").GetBoolean());
        Assert.False(named.GetProperty("isAnonymous").GetBoolean());
        Assert.False(cafeDefault.GetProperty("isAnonymous").GetBoolean());
        Assert.True(cafeAnonymous.GetProperty("isAnonymous").GetBoolean());
    }

    [MongoFact]
    public async Task The_real_author_is_stored_on_every_comment()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var readerId = w.Reader.Body.GetProperty("user").GetProperty("id").GetString();

        var (_, json) = await Post(w, w.Reader.Cookie!, new { text = "hello", isAnonymous = true });

        Assert.Equal(readerId, (await w.Api.Services.GetRequiredService<CommentRepository>().GetAsync(json.GetProperty("id").GetString()!))!.UserId);
    }

    [MongoFact]
    public async Task A_reader_can_only_post_so_many_comments_an_hour()
    {
        var w = await Setup(settings: new() { ["Comments:MaxPerHour"] = "2" });
        await using var _ = w.Api;

        Assert.Equal(HttpStatusCode.Created, (await Post(w, w.Reader.Cookie!, new { text = "one" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(w, w.Reader.Cookie!, new { text = "two" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(w, w.Reader.Cookie!, new { text = "three" })).Response.StatusCode);

        w.Api.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Created, (await Post(w, w.Reader.Cookie!, new { text = "three" })).Response.StatusCode);
    }

    // ---- one level deep ----

    [MongoFact]
    public async Task A_reply_goes_under_a_public_top_level_comment_and_a_reply_to_a_reply_is_refused()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var (_, top) = await Post(w, w.Reader.Cookie!, new { text = "top", isAnonymous = false });
        var topId = top.GetProperty("id").GetString()!;

        // The parent is still waiting for moderation, so it cannot be replied to yet.
        Assert.Equal("invalid_parent", (await Post(w, w.Author.Cookie!, new { text = "too early", parentId = topId })).Json.GetProperty("error").GetString());

        await Publish(w, topId);
        var (replyResponse, reply) = await Post(w, w.Author.Cookie!, new { text = "reply", parentId = topId, isAnonymous = false });
        Assert.Equal(HttpStatusCode.Created, replyResponse.StatusCode);
        var replyId = reply.GetProperty("id").GetString()!;
        await Publish(w, replyId);

        var deeper = await Post(w, w.Reader.Cookie!, new { text = "reply to reply", parentId = replyId });
        Assert.Equal(HttpStatusCode.BadRequest, deeper.Response.StatusCode);
        Assert.Equal("invalid_parent", deeper.Json.GetProperty("error").GetString());
        Assert.Equal("invalid_parent", (await Post(w, w.Reader.Cookie!, new { text = "nowhere", parentId = NewId() })).Json.GetProperty("error").GetString());

        var shown = (await List(w)).EnumerateArray().Single();
        Assert.Equal("top", shown.GetProperty("text").GetString());
        Assert.Equal("reply", shown.GetProperty("replies").EnumerateArray().Single().GetProperty("text").GetString());
    }

    [MongoFact]
    public async Task A_reply_cannot_point_at_a_comment_on_another_review()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var otherEntity = await SeedEntity(w.Api);
        var otherReview = await CreateReview(w.Api, w.Reader.Cookie!, otherEntity.Id);
        var (_, elsewhere) = await Send(w.Api, HttpMethod.Post, $"/api/reviews/{otherReview}/comments", w.Author.Cookie, new { text = "over there" });
        await Publish(w, elsewhere.GetProperty("id").GetString()!);

        var result = await Post(w, w.Reader.Cookie!, new { text = "wrong place", parentId = elsewhere.GetProperty("id").GetString() });

        Assert.Equal("invalid_parent", result.Json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task The_public_list_is_oldest_first_only_public_and_replies_vanish_with_their_parent()
    {
        var w = await Setup();
        await using var _ = w.Api;
        string[] ids = new string[3];
        for (var i = 0; i < 3; i++)
        {
            w.Api.Time.Advance(TimeSpan.FromMinutes(1));
            var (_, c) = await Post(w, w.Reader.Cookie!, new { text = $"c{i}" });
            ids[i] = c.GetProperty("id").GetString()!;
        }
        await Publish(w, ids[0]);
        await Publish(w, ids[1]);   // c2 stays pending
        var (_, reply) = await Post(w, w.Author.Cookie!, new { text = "reply to c0", parentId = ids[0] });
        await Publish(w, reply.GetProperty("id").GetString()!);

        Assert.Equal(["c0", "c1"], (await List(w)).EnumerateArray().Select(c => c.GetProperty("text").GetString()));

        await Send(w.Api, HttpMethod.Delete, $"/api/comments/{ids[0]}", w.Reader.Cookie);
        var afterDelete = (await List(w)).EnumerateArray().ToList();
        Assert.Equal(["c1"], afterDelete.Select(c => c.GetProperty("text").GetString()));
        Assert.DoesNotContain("reply to c0", (await List(w)).GetRawText());
    }

    // ---- edited label, delete, switch ----

    [MongoFact]
    public async Task Editing_labels_the_comment_edited_and_sends_it_back_to_moderation()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var (_, c) = await Post(w, w.Reader.Cookie!, new { text = "first", isAnonymous = false });
        var id = c.GetProperty("id").GetString()!;
        await Publish(w, id);
        Assert.False((await List(w)).EnumerateArray().Single().GetProperty("edited").GetBoolean());
        w.Api.Time.Advance(TimeSpan.FromHours(3));

        var (response, edited) = await Send(w.Api, HttpMethod.Put, $"/api/comments/{id}", w.Reader.Cookie, new { text = "second" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PendingModeration", edited.GetProperty("status").GetString());
        Assert.Empty((await List(w)).EnumerateArray()); // hidden until the new text is approved
        await Publish(w, id);
        var shown = (await List(w)).EnumerateArray().Single();
        Assert.Equal("second", shown.GetProperty("text").GetString());
        Assert.True(shown.GetProperty("edited").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, shown.GetProperty("editedOn").ValueKind);
    }

    [MongoFact]
    public async Task Saving_the_same_text_is_not_an_edit()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var (_, c) = await Post(w, w.Reader.Cookie!, new { text = "same" });
        var id = c.GetProperty("id").GetString()!;
        await Publish(w, id);

        await Send(w.Api, HttpMethod.Put, $"/api/comments/{id}", w.Reader.Cookie, new { text = "  same " });

        var stored = (await w.Api.Services.GetRequiredService<CommentRepository>().GetAsync(id))!;
        Assert.Equal(CommentStatus.Published, stored.Status);
        Assert.Null(stored.EditedAt);
    }

    [MongoFact]
    public async Task Only_the_author_can_edit_switch_or_delete_a_comment()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var (_, c) = await Post(w, w.Reader.Cookie!, new { text = "mine" });
        var id = c.GetProperty("id").GetString()!;

        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Put, $"/api/comments/{id}", new { text = "hijack" }),
            (HttpMethod.Put, $"/api/comments/{id}/anonymity", new { isAnonymous = false }),
            (HttpMethod.Delete, $"/api/comments/{id}", null),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Api, method, url, null, body)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, method, url, w.Author.Cookie, body)).Response.StatusCode);
        }
        Assert.Equal("mine", (await w.Api.Services.GetRequiredService<CommentRepository>().GetAsync(id))!.Text);
    }

    [MongoFact]
    public async Task Deleting_hides_a_comment_and_a_deleted_comment_cannot_be_edited()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var (_, c) = await Post(w, w.Reader.Cookie!, new { text = "bye" });
        var id = c.GetProperty("id").GetString()!;
        await Publish(w, id);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Delete, $"/api/comments/{id}", w.Reader.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Delete, $"/api/comments/{id}", w.Reader.Cookie)).Response.StatusCode);

        Assert.Empty((await List(w)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Put, $"/api/comments/{id}", w.Reader.Cookie, new { text = "back" })).Response.StatusCode);
        var (_, mine) = await Send(w.Api, HttpMethod.Get, "/api/me/comments", w.Reader.Cookie);
        Assert.Equal("Deleted", mine.EnumerateArray().Single().GetProperty("status").GetString());
    }

    [MongoFact]
    public async Task Switching_a_comment_between_named_and_anonymous_takes_effect_at_once_without_an_edited_label()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetProfile(w.Api, w.Reader.Cookie!, "Omar Hassan");
        var (_, c) = await Post(w, w.Reader.Cookie!, new { text = "hello", isAnonymous = false });
        var id = c.GetProperty("id").GetString()!;
        await Publish(w, id);
        Assert.Equal("Omar Hassan", (await List(w)).EnumerateArray().Single().GetProperty("author").GetProperty("name").GetString());

        await Send(w.Api, HttpMethod.Put, $"/api/comments/{id}/anonymity", w.Reader.Cookie, new { isAnonymous = true });

        var shown = (await List(w)).EnumerateArray().Single();
        Assert.True(shown.GetProperty("anonymous").GetBoolean());
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("author").ValueKind);
        Assert.DoesNotContain("Omar", shown.GetRawText());
        Assert.False(shown.GetProperty("edited").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Put, $"/api/comments/{id}/anonymity", w.Reader.Cookie, new { })).Response.StatusCode);
    }

    [MongoFact]
    public async Task Comments_never_change_a_score()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Published, VerificationLevel.Verified);
        var before = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/score")).Json.GetRawText();

        var (_, c) = await Post(w, w.Reader.Cookie!, new { text = "I disagree completely, this is terrible" });
        await Publish(w, c.GetProperty("id").GetString()!);

        Assert.Equal(before, (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/score")).Json.GetRawText());
    }
}
