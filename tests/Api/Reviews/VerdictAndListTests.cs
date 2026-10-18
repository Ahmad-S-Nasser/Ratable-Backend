using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Reviews;

public sealed class VerdictAndListTests
{
    private static readonly VerdictOptions Options = new();

    private static Review R(string? verdict, int score = 4, bool verified = true, ReviewStatus status = ReviewStatus.Published) => new()
    {
        Verdict = verdict,
        Status = status,
        Answers = [new ReviewAnswer { CriterionId = "security", Score = score }],
        Verification = new ReviewVerification { Level = verified ? VerificationLevel.Verified : VerificationLevel.Unverified },
    };

    private static VerdictResult? Verdict(IReadOnlyCollection<Review> reviews)
    {
        var score = ScoreCalculator.ForEntity(reviews);
        return VerdictCalculator.ForEntity(reviews, score.Overall, score.VerifiedReviewCount, Options);
    }

    // ---- the calculation ----

    [Fact]
    public void A_verdict_needs_five_verified_votes()
    {
        Assert.Null(Verdict([.. Enumerable.Range(0, 4).Select(_ => R(Verdicts.Worth))]));
        var verdict = Verdict([.. Enumerable.Range(0, 5).Select(_ => R(Verdicts.Worth))])!;
        Assert.Equal(Verdicts.Worth, verdict.Kind);
        Assert.Equal(100, verdict.SharePercent);
        Assert.Equal(5, verdict.Votes);
    }

    [Fact]
    public void Only_published_verified_reviews_vote()
    {
        List<Review> reviews = [.. Enumerable.Range(0, 5).Select(_ => R(Verdicts.Worth)), .. Enumerable.Range(0, 20).Select(_ => R(Verdicts.NotWorth, verified: false)),
            .. Enumerable.Range(0, 20).Select(_ => R(Verdicts.NotWorth, status: ReviewStatus.PendingModeration)), .. Enumerable.Range(0, 20).Select(_ => R(Verdicts.NotWorth, status: ReviewStatus.Hidden)),
            .. Enumerable.Range(0, 20).Select(_ => R(Verdicts.NotWorth, status: ReviewStatus.Deleted))];

        var verdict = Verdict(reviews)!;

        Assert.Equal(Verdicts.Worth, verdict.Kind);
        Assert.Equal(5, verdict.Votes); // the 80 other votes are not verified or not public
    }

    [Fact]
    public void Mixed_opinions_and_ties_show_no_sticker_and_a_clear_majority_does()
    {
        Assert.Null(Verdict([.. Enumerable.Range(0, 3).Select(_ => R(Verdicts.Worth)), .. Enumerable.Range(0, 3).Select(_ => R(Verdicts.NotWorth))])); // a tie
        // Three kinds of vote, none with half of them: opinions are mixed. (The gem votes qualify here, so they stay separate.)
        Assert.Null(Verdict([.. Enumerable.Range(0, 3).Select(_ => R(Verdicts.Worth, 5)), .. Enumerable.Range(0, 3).Select(_ => R(Verdicts.NotWorth, 5)), .. Enumerable.Range(0, 4).Select(_ => R(Verdicts.HiddenGem, 5))]));
        var verdict = Verdict([.. Enumerable.Range(0, 7).Select(_ => R(Verdicts.NotWorth, 2)), .. Enumerable.Range(0, 3).Select(_ => R(Verdicts.Worth))])!;
        Assert.Equal(Verdicts.NotWorth, verdict.Kind);
        Assert.Equal(70, verdict.SharePercent);
        Assert.Null(Verdict([R(null), R(null), R(null), R(null), R(null), R(null)])); // nobody chose one
    }

    [Fact]
    public void A_hidden_gem_needs_a_high_score_and_few_reviews()
    {
        // High score and few reviews: the gem votes win.
        var gem = Verdict([.. Enumerable.Range(0, 6).Select(_ => R(Verdicts.HiddenGem, 5))])!;
        Assert.Equal(Verdicts.HiddenGem, gem.Kind);

        // A modest score: the same votes count as plain "worth it".
        var modest = Verdict([.. Enumerable.Range(0, 6).Select(_ => R(Verdicts.HiddenGem, 3))])!;
        Assert.Equal(Verdicts.Worth, modest.Kind);

        // Many reviews: it is no longer hidden.
        var popular = Verdict([.. Enumerable.Range(0, 40).Select(_ => R(Verdicts.HiddenGem, 5))])!;
        Assert.Equal(Verdicts.Worth, popular.Kind);
    }

    [Fact]
    public void A_verdict_never_changes_the_score_and_the_same_reviews_give_the_same_score_without_one()
    {
        List<Review> withVerdicts = [.. Enumerable.Range(0, 8).Select(i => R(i % 2 == 0 ? Verdicts.Worth : Verdicts.NotWorth, 1 + i % 5))];
        List<Review> without = [.. withVerdicts.Select(r => R(null, r.Answers[0].Score))];
        _ = Verdict(withVerdicts);

        var a = ScoreCalculator.ForEntity(withVerdicts);
        var b = ScoreCalculator.ForEntity(without);

        Assert.Equal(b.Overall, a.Overall);
        Assert.Equal(b.VerifiedReviewCount, a.VerifiedReviewCount);
        Assert.Equal(b.Criteria.Select(c => (c.CriterionId, c.AnswerCount, c.Score)), a.Criteria.Select(c => (c.CriterionId, c.AnswerCount, c.Score)));
    }

    // ---- the API ----

    [MongoFact]
    public async Task The_reviewer_can_choose_a_verdict_and_change_it_and_a_bad_one_is_refused()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);

        var bad = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie, new { answers = Answers(("security", 4)), verdict = "amazing" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Response.StatusCode);
        Assert.Equal("invalid_verdict", bad.Json.GetProperty("error").GetString());

        var created = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie, new { answers = Answers(("security", 4)), verdict = "Worth" });
        Assert.Equal(HttpStatusCode.Created, created.Response.StatusCode);
        Assert.Equal("worth", created.Json.GetProperty("verdict").GetString());
        var id = created.Json.GetProperty("id").GetString()!;

        // Changing only the verdict is a change of content: a new revision that keeps the old verdict.
        var edited = await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, new { answers = Answers(("security", 4)), verdict = "hidden_gem" });
        Assert.Equal("hidden_gem", edited.Json.GetProperty("verdict").GetString());
        Assert.Equal(2, edited.Json.GetProperty("revisionNumber").GetInt32());
        var revisions = await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id);
        Assert.Equal("worth", Assert.Single(revisions).Verdict);

        // Clearing it with an empty value.
        var cleared = await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, new { answers = Answers(("security", 4)), verdict = "" });
        Assert.Equal(JsonValueKindNull, cleared.Json.GetProperty("verdict").ValueKind);
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [MongoFact]
    public async Task A_category_without_verdicts_refuses_one()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        var categories = api.Services.GetRequiredService<CategoryRepository>();
        var category = (await categories.GetAsync("compounds"))!;
        category.VerdictEnabled = false;
        await categories.ReplaceAsync(category);
        var entity = await SeedEntity(api);

        var result = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie, new { answers = Answers(("security", 4)), verdict = "worth" });

        Assert.Equal(HttpStatusCode.BadRequest, result.Response.StatusCode);
    }

    private static async Task Seed(IdentityApiFactory api, string entityId, string? verdict, int score, VerificationLevel level = VerificationLevel.Verified)
    {
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
        {
            EntityId = entityId,
            UserId = ObjectId.GenerateNewId().ToString(),
            CategoryId = "compounds",
            Status = ReviewStatus.Published,
            Verdict = verdict,
            Verification = new ReviewVerification { Level = level },
            Answers = [new ReviewAnswer { CriterionId = "security", Score = score }],
        });
    }

    [MongoFact]
    public async Task The_score_endpoint_and_the_place_cards_carry_the_verdict_only_after_five_verified_votes()
    {
        await using var api = new IdentityApiFactory();
        var entity = await SeedEntity(api);
        for (var i = 0; i < 4; i++) await Seed(api, entity.Id, Verdicts.Worth, 4);

        Assert.Equal(JsonValueKindNull, (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score")).Json.GetProperty("verdict").ValueKind);

        await Seed(api, entity.Id, Verdicts.Worth, 4);
        await Seed(api, entity.Id, Verdicts.NotWorth, 1, VerificationLevel.Unverified); // not verified: no vote, no effect on the score either
        var score = (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score")).Json;
        Assert.Equal("worth", score.GetProperty("verdict").GetProperty("kind").GetString());
        Assert.Equal(100, score.GetProperty("verdict").GetProperty("sharePercent").GetInt32());
        Assert.Equal(4.0, score.GetProperty("overall").GetDouble());

        var places = (await Send(api, HttpMethod.Get, "/api/categories/compounds/places")).Json.GetProperty("places");
        Assert.Equal("worth", places[0].GetProperty("verdict").GetProperty("kind").GetString());
    }

    // ---- lists ----

    private static async Task<Entity> Place(IdentityApiFactory api, string category, string nameEn, string city, string? priceBand = null, double lon = 31.2, double lat = 30.0)
    {
        var entity = new Entity
        {
            CategoryId = category,
            Name = new Ratable.Shared.Data.LocalizedText("مكان", nameEn),
            City = city,
            Status = EntityStatus.Active,
            Location = MongoDB.Driver.GeoJsonObjectModel.GeoJson.Point(MongoDB.Driver.GeoJsonObjectModel.GeoJson.Geographic(lon, lat)),
            Fields = priceBand is null ? [] : new Dictionary<string, string> { ["priceBand"] = priceBand },
        };
        entity.Slugs = new Dictionary<string, string> { ["en"] = nameEn.ToLowerInvariant().Replace(' ', '-'), ["ar"] = "مكان" };
        await api.Services.GetRequiredService<EntityRepository>().InsertAsync(entity);
        return entity;
    }

    [MongoFact]
    public async Task The_lists_are_configuration_seeded_and_ranked_only_by_verified_score()
    {
        await using var api = new IdentityApiFactory();
        var listed = (await Send(api, HttpMethod.Get, "/api/collections")).Json.EnumerateArray().Select(l => l.GetProperty("id").GetString()).ToList();
        Assert.Contains("L01", listed);
        Assert.Contains("L02", listed);

        var low = await Place(api, "compounds", "Low Gardens", "Cairo");
        var high = await Place(api, "compounds", "High Gardens", "Cairo");
        var thin = await Place(api, "compounds", "Thin Gardens", "Cairo"); // a perfect score from too few reviews: below the list's minimum
        var unverified = await Place(api, "compounds", "Unverified Gardens", "Cairo");
        for (var i = 0; i < 3; i++) { await Seed(api, low.Id, null, 3); await Seed(api, high.Id, null, 5); }
        await Seed(api, thin.Id, null, 5);
        for (var i = 0; i < 5; i++) await Seed(api, unverified.Id, null, 5, VerificationLevel.Unverified);

        var list = (await Send(api, HttpMethod.Get, "/api/collections/L01")).Json;

        Assert.Equal(["High Gardens", "Low Gardens"], list.GetProperty("places").EnumerateArray().Select(p => p.GetProperty("name").GetProperty("en").GetString()).ToList());
        Assert.Equal("top-compounds", list.GetProperty("slug").GetProperty("en").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, list.GetProperty("updatedAt").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/collections/nope")).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_list_with_an_area_keeps_only_places_there_and_cards_carry_price_band_and_distance_only_when_asked()
    {
        await using var api = new IdentityApiFactory();
        var cairo = await Place(api, "restaurants-cafes", "Cairo Grill", "Cairo", "$$", lon: 31.2357, lat: 30.0444);
        var alex = await Place(api, "restaurants-cafes", "Alex Grill", "Alexandria", "$", lon: 29.9187, lat: 31.2001);
        foreach (var e in new[] { cairo, alex })
            for (var i = 0; i < 5; i++)
            {
                var review = new Review
                {
                    EntityId = e.Id,
                    UserId = ObjectId.GenerateNewId().ToString(),
                    CategoryId = "restaurants-cafes",
                    Status = ReviewStatus.Published,
                    Verification = new ReviewVerification { Level = VerificationLevel.Verified },
                    Answers = [new ReviewAnswer { CriterionId = "taste", Score = 5 }],
                };
                await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(review);
            }

        var without = (await Send(api, HttpMethod.Get, "/api/collections/L02")).Json;
        var card = Assert.Single(without.GetProperty("places").EnumerateArray());
        Assert.Equal("Cairo Grill", card.GetProperty("name").GetProperty("en").GetString());
        Assert.Equal("$$", card.GetProperty("priceBand").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, card.GetProperty("distanceMeters").ValueKind); // no location shared: no distance

        var with = (await Send(api, HttpMethod.Get, "/api/collections/L02?lat=30.0600&lng=31.2300")).Json.GetProperty("places")[0];
        Assert.InRange(with.GetProperty("distanceMeters").GetDouble(), 1000, 3000);
        Assert.Contains("taste", with.GetProperty("topCriteria").EnumerateArray().Select(c => c.GetString()));
    }

    [MongoFact]
    public async Task Ranking_and_verdict_are_the_same_with_or_without_a_visitor_location_and_nothing_paid_is_an_input()
    {
        await using var api = new IdentityApiFactory();
        var near = await Place(api, "compounds", "Near Place", "Cairo", lon: 31.2357, lat: 30.0444);
        var far = await Place(api, "compounds", "Far Place", "Cairo", lon: 33.0, lat: 28.0);
        for (var i = 0; i < 5; i++) { await Seed(api, near.Id, Verdicts.Worth, 3); await Seed(api, far.Id, Verdicts.Worth, 5); }

        string Order(System.Text.Json.JsonElement json) => string.Join(",", json.GetProperty("places").EnumerateArray().Select(p => p.GetProperty("name").GetProperty("en").GetString()));
        var plain = (await Send(api, HttpMethod.Get, "/api/categories/compounds/places")).Json;
        var located = (await Send(api, HttpMethod.Get, "/api/categories/compounds/places?lat=30.0444&lng=31.2357")).Json;

        Assert.Equal("Far Place,Near Place", Order(plain)); // the nearer place does not rank higher
        Assert.Equal(Order(plain), Order(located));
        Assert.Equal(plain.GetProperty("places")[0].GetProperty("verdict").ToString(), located.GetProperty("places")[0].GetProperty("verdict").ToString());
    }
    [MongoFact]
    public async Task An_anonymous_author_never_appears_on_a_card_or_a_list()
    {
        await using var api = new IdentityApiFactory();
        var author = await api.SignInAsync("+201111111111");
        await Send(api, HttpMethod.Put, "/api/me/profile", author.Cookie, new { displayName = "Secret Sam" });
        var entity = await Place(api, "compounds", "Plain Gardens", "Cairo");
        var created = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", author.Cookie, new { answers = Answers(("security", 5)), isAnonymous = true, verdict = "worth" });
        await SetReviewState(api, created.Json.GetProperty("id").GetString()!, ReviewStatus.Published, VerificationLevel.Verified);
        var userId = author.Body.GetProperty("user").GetProperty("id").GetString()!;

        foreach (var url in new[] { "/api/categories/compounds/places", "/api/collections/L01", "/api/collections", $"/api/entities/{entity.Id}/score", "/api/sitemap/places" })
        {
            var body = await (await api.CreateClient().GetAsync(url)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("Secret Sam", body);
            Assert.DoesNotContain(userId, body);
        }
    }
}
