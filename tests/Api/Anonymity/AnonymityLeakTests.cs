using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Comments.Models;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Anonymity;

/// <summary>
/// The leak test (CLAUDE.md rule 1, anonymity-guard skill). It seeds an anonymous review and an anonymous comment whose author has unique marker
/// strings (name, avatar, phone, account id), calls EVERY public endpoint as a visitor and as another signed-in user, and searches every response
/// (body, headers) and every log line for every marker. Any hit fails the test. Run it whenever a field, endpoint, page or index touching reviews
/// or comments is added: add the new public endpoint to <see cref="PublicUrls"/>.
/// </summary>
public sealed class AnonymityLeakTests
{
    private const string Name = "LEAKNAME-9f3a7c";
    private const string Avatar = "https://cdn.example.com/LEAKAVATAR-9f3a7c.png";
    private const string PhoneDigits = "01077665544";           // the author's phone, as typed
    private const string PhoneE164Tail = "1077665544";
    private const string NamedAuthor = "NAMEDOK-5521";            // the control: a named author MUST appear

    private sealed record Markers(string[] Secret, string Control);

    private static async Task<(IdentityApiFactory Api, string EntityId, string ReviewId, string CommentId, string ReplyId, Markers Markers)> Scene()
    {
        var api = new IdentityApiFactory();
        var hidden = await api.SignInAsync(PhoneDigits);
        await SetProfile(api, hidden.Cookie!, Name, Avatar);
        var hiddenId = hidden.Body.GetProperty("user").GetProperty("id").GetString()!;
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var named = await api.SignInAsync("+201055555555");
        await SetProfile(api, named.Cookie!, NamedAuthor);
        api.Time.Advance(TimeSpan.FromMinutes(2));

        var entity = await SeedEntity(api);
        // The hidden author starts NAMED, then switches to anonymous later: the switch must cover everything at once.
        var review = await CreateReview(api, hidden.Cookie!, entity.Id, new { answers = Answers(("security", 5)), text = "anonymous review", isAnonymous = false });
        await Send(api, HttpMethod.Put, $"/api/reviews/{review}", hidden.Cookie, new { answers = Answers(("security", 5)), text = "anonymous review, edited" });
        await SetReviewState(api, review, ReviewStatus.Published, VerificationLevel.Verified);   // so it has history and counts in the score
        await Send(api, HttpMethod.Put, $"/api/reviews/{review}/anonymity", hidden.Cookie, new { isAnonymous = true });
        await CreateReview(api, named.Cookie!, entity.Id, new { answers = Answers(("security", 4)), text = "named review", isAnonymous = false });

        var comment = await Comment(api, hidden.Cookie!, review, "anonymous comment", null, anonymous: true);
        var reply = await Comment(api, hidden.Cookie!, review, "anonymous reply", comment, anonymous: true);
        await Comment(api, named.Cookie!, review, "named comment", null, anonymous: false);

        var markers = new Markers([Name, Avatar, "LEAKNAME", "LEAKAVATAR", PhoneDigits, PhoneE164Tail, hiddenId], NamedAuthor);
        return (api, entity.Id, review, comment, reply, markers);
    }

    private static async Task<string> Comment(IdentityApiFactory api, string cookie, string reviewId, string text, string? parent, bool anonymous)
    {
        var (response, json) = await Send(api, HttpMethod.Post, $"/api/reviews/{reviewId}/comments", cookie, new { text, parentId = parent, isAnonymous = anonymous });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = json.GetProperty("id").GetString()!;
        var repo = api.Services.GetRequiredService<CommentRepository>();
        var c = (await repo.GetAsync(id))!;
        c.Status = CommentStatus.Published;
        await repo.ReplaceAsync(c);
        return id;
    }

    /// <summary>Every public endpoint that can show reviews, comments, entities or anything derived from them. Add new ones here.</summary>
    private static IEnumerable<string> PublicUrls(string entityId, string reviewId) =>
    [
        $"/api/entities/{entityId}",
        $"/api/entities/search?q={Uri.EscapeDataString("مدينتي")}",
        $"/api/entities/search?q=madinaty",
        $"/api/entities/{entityId}/reviews",
        $"/api/entities/{entityId}/reviews?limit=1&skip=0",
        $"/api/entities/{entityId}/score",
        $"/api/reviews/{reviewId}/history",
        $"/api/reviews/{reviewId}/comments",
        "/api/categories",
        "/api/categories/compounds",
        "/openapi/v1.json",
        "/health",
        "/health/ready",
        // not found / bad input paths must not echo anything either
        $"/api/reviews/{Guid.NewGuid():N}/history",
        "/api/entities/not-an-id/reviews",
    ];

    private static async Task<string> Collect(IdentityApiFactory api, IEnumerable<string> urls, string? cookie)
    {
        var all = new StringBuilder();
        foreach (var url in urls)
        {
            using var http = api.CreateClient();
            var request = cookie is null ? new HttpRequestMessage(HttpMethod.Get, url) : IdentityApiFactory.WithCookie(HttpMethod.Get, url, cookie);
            var response = await http.SendAsync(request);
            all.AppendLine($"GET {url} -> {(int)response.StatusCode}");
            foreach (var h in response.Headers) all.AppendLine($"{h.Key}: {string.Join(',', h.Value)}");
            all.AppendLine(await response.Content.ReadAsStringAsync());
        }
        return all.ToString();
    }

    private static void AssertNoMarkers(string where, string haystack, IEnumerable<string> markers)
    {
        foreach (var marker in markers)
            Assert.False(haystack.Contains(marker, StringComparison.OrdinalIgnoreCase), $"LEAK: '{marker}' found in {where}");
    }

    // ---- the test ----

    [MongoFact]
    public async Task No_public_endpoint_shows_an_anonymous_author_to_a_visitor_or_a_signed_in_user()
    {
        var (api, entityId, reviewId, _, _, markers) = await Scene();
        await using var _ = api;
        var viewer = await api.SignInAsync("+201077778888");

        var asVisitor = await Collect(api, PublicUrls(entityId, reviewId), cookie: null);
        var asUser = await Collect(api, PublicUrls(entityId, reviewId), viewer.Cookie);

        AssertNoMarkers("a public response seen by a visitor", asVisitor, markers.Secret);
        AssertNoMarkers("a public response seen by a signed-in user", asUser, markers.Secret);
    }

    [MongoFact]
    public async Task The_scan_really_finds_things_a_named_author_does_appear_and_so_do_the_texts_of_the_anonymous_items()
    {
        var (api, entityId, reviewId, _, _, markers) = await Scene();
        await using var _ = api;

        var visitor = await Collect(api, PublicUrls(entityId, reviewId), cookie: null);

        // If this fails the scan above is blind and proves nothing.
        Assert.Contains(markers.Control, visitor);
        Assert.Contains("anonymous review, edited", visitor);
        Assert.Contains("anonymous comment", visitor);
        Assert.Contains("anonymous reply", visitor);
        Assert.Throws<Xunit.Sdk.FalseException>(() => AssertNoMarkers("canary", visitor, [markers.Control]));
    }

    [MongoFact]
    public async Task The_review_history_and_comment_replies_show_no_author_for_anonymous_items()
    {
        var (api, entityId, reviewId, _, _, _) = await Scene();
        await using var _ = api;

        var history = (await Send(api, HttpMethod.Get, $"/api/reviews/{reviewId}/history")).Json;
        var comments = (await Send(api, HttpMethod.Get, $"/api/reviews/{reviewId}/comments")).Json.EnumerateArray().ToList();
        var anonymousTop = comments.Single(c => c.GetProperty("text").GetString() == "anonymous comment");

        Assert.True(history.GetProperty("current").GetProperty("anonymous").GetBoolean());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("current").GetProperty("author").ValueKind);
        Assert.Equal(JsonValueKind.Null, anonymousTop.GetProperty("author").ValueKind);
        Assert.Equal(JsonValueKind.Null, anonymousTop.GetProperty("replies")[0].GetProperty("author").ValueKind);
        Assert.Equal(NamedAuthor, comments.Single(c => c.GetProperty("text").GetString() == "named comment").GetProperty("author").GetProperty("name").GetString());
    }

    [MongoFact]
    public async Task Logs_never_hold_an_anonymous_authors_identity()
    {
        var (api, entityId, reviewId, _, _, markers) = await Scene();
        await using var _ = api;
        await Collect(api, PublicUrls(entityId, reviewId), cookie: null);

        var logs = string.Join('\n', api.Logs.Lines);

        Assert.NotEmpty(api.Logs.Lines); // the sink is wired: requests were logged
        AssertNoMarkers("the log", logs, markers.Secret);
    }

    [MongoFact]
    public async Task Only_the_author_sees_their_own_anonymous_items_in_the_account_endpoints()
    {
        var (api, entityId, reviewId, _, _, _) = await Scene();
        await using var _ = api;
        var stranger = await api.SignInAsync("+201099999999");

        var (_, theirReviews) = await Send(api, HttpMethod.Get, "/api/me/reviews", stranger.Cookie);
        var (_, theirComments) = await Send(api, HttpMethod.Get, "/api/me/comments", stranger.Cookie);

        Assert.Empty(theirReviews.EnumerateArray());
        Assert.Empty(theirComments.EnumerateArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Get, "/api/me/reviews")).Response.StatusCode);
    }
}
