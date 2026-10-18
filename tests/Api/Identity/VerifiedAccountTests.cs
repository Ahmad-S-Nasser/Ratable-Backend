using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Moderation.Services;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Identity;

/// <summary>What a verified phone changes (a review without proof can skip the human queue when the checks pass, and a mark is shown) and what it never changes (any score).</summary>
public sealed class VerifiedAccountTests
{
    private const string Secret = "whatsapp-app-secret";

    private static IdentityApiFactory NewVerifyApi() => new(
        new Dictionary<string, string>
        {
            ["Identity:BootstrapAdminPhone"] = AdminPhone,
            ["Identity:WhatsAppNumber"] = "+201099999999",
            ["Identity:WhatsAppAppSecret"] = Secret,
        },
        fakeAi: true);

    /// <summary>The real flow: start, then the WhatsApp message arrives at the signed webhook.</summary>
    private static async Task VerifyThroughWhatsApp(IdentityApiFactory api, IdentityApiFactory.SignedIn user)
    {
        using var http = api.CreateClient();
        using var start = IdentityApiFactory.WithCookie(HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { });
        var started = JsonSerializer.Deserialize<JsonElement>(await (await http.SendAsync(start)).Content.ReadAsStringAsync());
        var code = started.GetProperty("code").GetString()!;

        var body = JsonSerializer.SerializeToUtf8Bytes(new { entry = new[] { new { changes = new[] { new { value = new { messages = new[] { new { from = user.Phone.TrimStart('+'), text = new { body = $"Ratable {code}" } } } } } } } } });
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/whatsapp") { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new("application/json");
        message.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(message)).StatusCode);
    }

    [MongoFact]
    public async Task After_verifying_through_whatsapp_a_review_without_proof_skips_the_queue_when_the_checks_pass_but_is_not_a_verified_review()
    {
        await using var api = NewVerifyApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);

        // Before: it waits for a moderator.
        var before = await NewReview(api, user, entity.Id, Clean());
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, before)).Status);
        Assert.Equal([RoutingPolicy.NoProofUnverifiedAccount], (await CaseFor(api, before)).Reasons);

        await VerifyThroughWhatsApp(api, user);

        // After: a new review of another place is published as soon as the checks pass, and it is still not a Verified review.
        var other = await SeedEntity(api);
        var after = await NewReview(api, user, other.Id, Clean());
        var review = await ReviewOf(api, after);
        Assert.Equal(ReviewStatus.Published, review.Status);
        Assert.Equal(VerificationLevel.Unverified, review.Verification.Level);
        var (_, score) = await Send(api, HttpMethod.Get, $"/api/entities/{other.Id}/score");
        Assert.Equal(0, score.GetProperty("verifiedReviews").GetInt32()); // it does not count toward the score
        Assert.Equal(JsonValueKind.Null, score.GetProperty("overall").ValueKind);
    }

    [MongoFact]
    public async Task Verifying_a_phone_never_changes_any_score_a_verdict_or_the_order_of_places()
    {
        await using var api = NewVerifyApi();
        var author = await api.SignInAsync("+201111111111");
        var other = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var second = await SeedEntity(api);
        var review = await NewReview(api, author, entity.Id, Clean());
        var rival = await NewReview(api, other, second.Id, Clean());
        foreach (var id in new[] { review, rival }) await SetReviewState(api, id, ReviewStatus.Published, VerificationLevel.Verified); // proof-verified reviews: these count

        async Task<string> Snapshot() =>
            (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score")).Json + "|" + (await Send(api, HttpMethod.Get, $"/api/entities/{second.Id}/score")).Json
            + "|" + (await Send(api, HttpMethod.Get, "/api/categories/compounds/places")).Json;
        var before = await Snapshot();

        await VerifyThroughWhatsApp(api, author);

        Assert.Equal(before, await Snapshot());
    }

    [MongoFact]
    public async Task A_verified_account_is_marked_on_its_named_reviews_and_never_on_an_anonymous_one()
    {
        await using var api = NewVerifyApi();
        var named = await api.SignInAsync("+201111111111");
        var hidden = await api.SignInAsync("+201222222222");
        var plain = await api.SignInAsync("+201333333333");
        foreach (var u in new[] { named, hidden }) await Send(api, HttpMethod.Put, "/api/me/profile", u.Cookie, new { displayName = u == named ? "Open Nora" : "Secret Sami" });
        await Send(api, HttpMethod.Put, "/api/me/profile", plain.Cookie, new { displayName = "Plain Pat" });
        var entity = await SeedEntity(api);
        var a = await NewReview(api, named, entity.Id, Clean(), anonymous: false);
        var b = await NewReview(api, hidden, entity.Id, Clean(), anonymous: true);
        var c = await NewReview(api, plain, entity.Id, Clean(), anonymous: false);
        foreach (var id in new[] { a, b, c }) await SetReviewState(api, id, ReviewStatus.Published);
        await VerifyThroughWhatsApp(api, named);
        await VerifyThroughWhatsApp(api, hidden);

        var list = (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews")).Json.EnumerateArray().ToList();
        var byId = list.ToDictionary(r => r.GetProperty("id").GetString()!);

        Assert.True(byId[a].GetProperty("author").GetProperty("accountVerified").GetBoolean());
        Assert.False(byId[c].GetProperty("author").GetProperty("accountVerified").GetBoolean()); // named, not verified: no mark
        Assert.Equal(JsonValueKind.Null, byId[b].GetProperty("author").ValueKind); // anonymous: no author at all, so no mark and no hint
        Assert.DoesNotContain("Secret Sami", JsonSerializer.Serialize(list));
    }
}
