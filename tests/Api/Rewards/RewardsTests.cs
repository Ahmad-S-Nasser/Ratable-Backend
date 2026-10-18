using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Contributions;
using Ratable.Modules.Contributions.Data;
using Ratable.Modules.Contributions.Models;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;
using Ratable.Shared.Rewards;
using Ratable.Shared.Verification;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Rewards;

public sealed class RewardsTests
{
    private const string AdminPhone = "+201000000001";
    private const string LongText = "A review with enough words in it to count as written";

    private static IdentityApiFactory NewApi() => new(new Dictionary<string, string> { ["Identity:BootstrapAdminPhone"] = AdminPhone }, fakeAi: true);

    private static string IdOf(IdentityApiFactory.SignedIn s) => s.Body.GetProperty("user").GetProperty("id").GetString()!;

    private static async Task<string> Review(IdentityApiFactory api, IdentityApiFactory.SignedIn author, string entityId, int score = 4, string? text = LongText)
    {
        var (response, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entityId}/reviews", author.Cookie, new { answers = Answers(("security", score)), text });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return json.GetProperty("id").GetString()!;
    }

    private static IContentModerator Moderator(IdentityApiFactory api) => api.Services.GetServices<IContentModerator>().First(m => m.Kind == ContentKind.Review);

    private static Task Publish(IdentityApiFactory api, string reviewId) => Moderator(api).ApplyAsync(reviewId, ModerationOutcome.Publish, null);

    private static ContributionRepository Ledger(IdentityApiFactory api) => api.Services.GetRequiredService<ContributionRepository>();

    private static async Task<int> Points(IdentityApiFactory api, string userId) => await Ledger(api).TotalPointsAsync(userId);

    // ---- earning and cancelling ----

    [MongoFact]
    public async Task A_published_written_review_earns_10_and_the_first_review_of_a_place_earns_25_more_and_nothing_is_paid_twice()
    {
        await using var api = NewApi();
        var first = await api.SignInAsync("+201111111111");
        var second = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var a = await Review(api, first, entity.Id);
        var b = await Review(api, second, entity.Id);

        Assert.Equal(0, await Points(api, IdOf(first))); // waiting for moderation earns nothing yet
        await Publish(api, a);
        Assert.Equal(35, await Points(api, IdOf(first)));
        await Publish(api, b);
        Assert.Equal(10, await Points(api, IdOf(second))); // not the first review of the place

        await Publish(api, a); // published again (an edit, a retry): the same effort is not paid again
        Assert.Equal(35, await Points(api, IdOf(first)));
        var entries = await Ledger(api).ListForUserAsync(IdOf(first));
        Assert.Equal(["first_review", "written_review"], entries.Select(e => e.Action).Order());
    }

    [MongoFact]
    public async Task Points_do_not_depend_on_whether_the_opinion_is_positive_or_negative()
    {
        await using var api = NewApi();
        var happy = await api.SignInAsync("+201111111111");
        var angry = await api.SignInAsync("+201222222222");
        var e1 = await SeedEntity(api);
        var e2 = await SeedEntity(api);
        var praise = await Review(api, happy, e1.Id, score: 5, text: "Absolutely wonderful place, everything was perfect and clean");
        var complaint = await Review(api, angry, e2.Id, score: 1, text: "Absolutely terrible place, nothing worked and it was dirty");
        await Publish(api, praise);
        await Publish(api, complaint);

        Assert.Equal(await Points(api, IdOf(happy)), await Points(api, IdOf(angry)));
    }

    [MongoFact]
    public async Task A_review_without_words_earns_no_written_points_and_a_short_one_neither()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var e1 = await SeedEntity(api);
        var e2 = await SeedEntity(api);
        var e3 = await SeedEntity(api);
        await Publish(api, await Review(api, user, e1.Id, text: null));
        await Publish(api, await Review(api, user, e2.Id, text: "Nice"));
        await Publish(api, await Review(api, user, e3.Id));
        Assert.Equal(3 * 25 + 10, await Points(api, IdOf(user))); // three first reviews, one written review
    }

    [MongoFact]
    public async Task Hiding_or_deleting_a_review_cancels_every_point_it_earned()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var other = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var review = await Review(api, user, entity.Id);
        await Publish(api, review);
        await api.Services.GetRequiredService<IReviewVerifier>().SetVerifiedAsync(review, "receipt", DateTime.UtcNow);
        Assert.Equal(50, await Points(api, IdOf(user))); // 10 + 25 + 15

        await Moderator(api).ApplyAsync(review, ModerationOutcome.Hide, "duplicate review");
        Assert.Equal(0, await Points(api, IdOf(user)));

        // Deleted by the author: the same.
        var entity2 = await SeedEntity(api);
        var review2 = await Review(api, other, entity2.Id);
        await Publish(api, review2);
        Assert.True(await Points(api, IdOf(other)) > 0);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Delete, $"/api/reviews/{review2}", other.Cookie)).Response.StatusCode);
        Assert.Equal(0, await Points(api, IdOf(other)));
    }

    [MongoFact]
    public async Task A_verified_visit_earns_15_and_losing_the_proof_takes_it_back()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await Review(api, user, entity.Id, text: null);
        var verifier = api.Services.GetRequiredService<IReviewVerifier>();

        await verifier.SetVerifiedAsync(review, "receipt", DateTime.UtcNow);
        await verifier.SetVerifiedAsync(review, "receipt", DateTime.UtcNow); // twice is the same
        Assert.Equal(15, await Points(api, IdOf(user)));
        await verifier.SetUnverifiedAsync(review);
        Assert.Equal(0, await Points(api, IdOf(user)));
    }

    // ---- helpful votes ----

    [MongoFact]
    public async Task A_helpful_vote_earns_the_author_2_up_to_20_a_day_and_cannot_be_given_to_yourself_or_twice()
    {
        await using var api = NewApi();
        var author = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await Review(api, author, entity.Id, text: null);
        await Publish(api, review);
        var before = await Points(api, IdOf(author));

        Assert.Equal("own_review", (await Send(api, HttpMethod.Put, $"/api/reviews/{review}/helpful", author.Cookie)).Json.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Put, $"/api/reviews/{review}/helpful")).Response.StatusCode);

        var voters = new List<IdentityApiFactory.SignedIn>();
        for (var i = 0; i < 12; i++) voters.Add(await api.SignInAsync($"+2012{i:D8}"));
        foreach (var v in voters)
        {
            var result = await Send(api, HttpMethod.Put, $"/api/reviews/{review}/helpful", v.Cookie);
            Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        }
        await Send(api, HttpMethod.Put, $"/api/reviews/{review}/helpful", voters[0].Cookie); // the same person again

        var count = (await Send(api, HttpMethod.Put, $"/api/reviews/{review}/helpful", voters[0].Cookie)).Json.GetProperty("count").GetInt32();
        Assert.Equal(12, count); // twelve different people, each counted once
        Assert.Equal(before + 20, await Points(api, IdOf(author))); // ten votes paid (the daily cap), two did not add points

        // Taking a vote back cancels its points; the review shows the count publicly.
        var removed = await Send(api, HttpMethod.Delete, $"/api/reviews/{review}/helpful", voters[1].Cookie);
        Assert.Equal(11, removed.Json.GetProperty("count").GetInt32());
        Assert.Equal(before + 18, await Points(api, IdOf(author)));
        var list = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews");
        Assert.Equal(11, list.Json[0].GetProperty("helpfulCount").GetInt32());

        var mine = await Send(api, HttpMethod.Get, $"/api/me/helpful?reviewIds={review}", voters[0].Cookie);
        Assert.Equal(review, Assert.Single(mine.Json.EnumerateArray()).GetString());
        Assert.Empty((await Send(api, HttpMethod.Get, $"/api/me/helpful?reviewIds={review}", voters[1].Cookie)).Json.EnumerateArray());
    }

    [MongoFact]
    public async Task A_review_that_is_not_public_cannot_be_voted_on_and_the_voter_is_never_shown()
    {
        await using var api = NewApi();
        var author = await api.SignInAsync("+201111111111");
        var voter = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var review = await Review(api, author, entity.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Put, $"/api/reviews/{review}/helpful", voter.Cookie)).Response.StatusCode);

        await Publish(api, review);
        var result = await Send(api, HttpMethod.Put, $"/api/reviews/{review}/helpful", voter.Cookie);
        Assert.DoesNotContain(IdOf(voter), result.Json.ToString());
        Assert.DoesNotContain(IdOf(voter), (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews")).Json.ToString());
    }

    // ---- ranks, badges, missions, streak ----

    [MongoFact]
    public async Task Ranks_follow_the_configured_thresholds_and_the_rank_up_is_announced_once()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var id = IdOf(user);
        var first = (await Send(api, HttpMethod.Get, "/api/me/rewards", user.Cookie)).Json;
        Assert.Equal("explorer", first.GetProperty("rank").GetProperty("id").GetString());
        Assert.Equal("reviewer", first.GetProperty("nextRank").GetProperty("id").GetString());
        Assert.Equal(100, first.GetProperty("pointsToNext").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, first.GetProperty("rankUp").ValueKind);

        await Ledger(api).AppendAsync(new ContributionEvent { UserId = id, Action = "written_review", Points = 100, Reason = "written_review", Reference = "r1", RootReference = "r1" });
        var up = (await Send(api, HttpMethod.Get, "/api/me/rewards", user.Cookie)).Json;
        Assert.Equal("reviewer", up.GetProperty("rank").GetProperty("id").GetString());
        Assert.Equal("explorer", up.GetProperty("rankUp").GetProperty("from").GetProperty("id").GetString());
        Assert.Equal("reviewer", up.GetProperty("rankUp").GetProperty("to").GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Post, "/api/me/rewards/seen", user.Cookie, new { rankId = "reviewer" })).Response.StatusCode);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Send(api, HttpMethod.Get, "/api/me/rewards", user.Cookie)).Json.GetProperty("rankUp").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, "/api/me/rewards/seen", user.Cookie, new { rankId = "emperor" })).Response.StatusCode);

        await Ledger(api).AppendAsync(new ContributionEvent { UserId = id, Action = "photo", Points = 400, Reason = "photo", Reference = "r2", RootReference = "r2" });
        await Ledger(api).AppendAsync(new ContributionEvent { UserId = id, Action = "photo", Points = 1500, Reason = "photo", Reference = "r3", RootReference = "r3" });
        Assert.Equal("ambassador", (await Send(api, HttpMethod.Get, "/api/me/rewards", user.Cookie)).Json.GetProperty("rank").GetProperty("id").GetString());
    }

    [MongoFact]
    public async Task Badges_and_missions_are_worked_out_from_the_live_ledger()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var id = IdOf(user);
        for (var i = 0; i < 3; i++) await Ledger(api).AppendAsync(new ContributionEvent { UserId = id, Action = "photo", Points = 5, Reason = "photo", Reference = $"p{i}", RootReference = "r" });
        await Ledger(api).AppendAsync(new ContributionEvent { UserId = id, Action = "written_review", Points = 10, Reason = "written_review", Reference = "w1", RootReference = "w1" });

        var rewards = (await Send(api, HttpMethod.Get, "/api/me/rewards", user.Cookie)).Json;
        var badges = rewards.GetProperty("badges").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetString()!);
        Assert.True(badges["first_words"].GetProperty("earned").GetBoolean());
        Assert.False(badges["photographer"].GetProperty("earned").GetBoolean());
        Assert.Equal(3, badges["photographer"].GetProperty("progress").GetInt32());
        var missions = rewards.GetProperty("missions").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!);
        Assert.True(missions["add_photos"].GetProperty("done").GetBoolean());
        Assert.Equal(1, missions["write_two"].GetProperty("progress").GetInt32());
        Assert.False(missions["write_two"].GetProperty("done").GetBoolean());
        Assert.Equal(1, rewards.GetProperty("streakWeeks").GetInt32());

        // A cancelled photo no longer counts for the badge or the mission.
        var photo = (await Ledger(api).ListForUserAsync(id)).First(e => e.Reference == "p0");
        await Ledger(api).ReverseAsync(photo, "media_removed");
        var after = (await Send(api, HttpMethod.Get, "/api/me/rewards", user.Cookie)).Json;
        Assert.Equal(2, after.GetProperty("badges").EnumerateArray().First(b => b.GetProperty("id").GetString() == "photographer").GetProperty("progress").GetInt32());
        Assert.False(after.GetProperty("missions").EnumerateArray().First(m => m.GetProperty("id").GetString() == "add_photos").GetProperty("done").GetBoolean());
    }

    [MongoFact]
    public async Task The_streak_counts_weeks_not_days()
    {
        await using var api = NewApi();
        var rewards = api.Services.GetRequiredService<RewardsService>();
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc); // a Saturday: the week starts today
        ContributionEvent At(int daysAgo, string action = "written_review") => new() { UserId = ObjectId.GenerateNewId().ToString(), Action = action, Points = 10, CreatedAt = now.AddDays(-daysAgo) };

        Assert.Equal(0, rewards.StreakWeeks([], now));
        Assert.Equal(1, rewards.StreakWeeks([At(0)], now));
        // Three effort entries in one week are still one week.
        Assert.Equal(1, rewards.StreakWeeks([At(0), At(0), At(0)], now));
        // This week, last week and the one before: three weeks. A week missing breaks it.
        Assert.Equal(3, rewards.StreakWeeks([At(0), At(7), At(14)], now));
        Assert.Equal(1, rewards.StreakWeeks([At(0), At(14)], now));
        // This week is still open: last week's streak is kept until the week ends.
        Assert.Equal(2, rewards.StreakWeeks([At(7), At(14)], now));
        // Helpful votes are usefulness, not effort: they do not keep a streak.
        Assert.Equal(0, rewards.StreakWeeks([At(0, "helpful_vote")], now));
    }

    // ---- rule 16: points never change a score or a verdict ----

    [MongoFact]
    public async Task Points_never_change_a_score_a_verdict_or_the_order_of_places()
    {
        await using var api = NewApi();
        var entity = await SeedEntity(api);
        var other = await SeedEntity(api);
        var authors = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var userId = ObjectId.GenerateNewId().ToString();
            authors.Add(userId);
            await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
            {
                EntityId = entity.Id,
                UserId = userId,
                CategoryId = "compounds",
                Status = ReviewStatus.Published,
                Verdict = "worth",
                Verification = new ReviewVerification { Level = VerificationLevel.Verified },
                Answers = [new ReviewAnswer { CriterionId = "security", Score = 4 }],
            });
        }
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
        {
            EntityId = other.Id,
            UserId = ObjectId.GenerateNewId().ToString(),
            CategoryId = "compounds",
            Status = ReviewStatus.Published,
            Verification = new ReviewVerification { Level = VerificationLevel.Verified },
            Answers = [new ReviewAnswer { CriterionId = "security", Score = 5 }],
        });

        async Task<string> Snapshot() =>
            (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score")).Json + "|" + (await Send(api, HttpMethod.Get, "/api/categories/compounds/places")).Json
            + "|" + (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews")).Json;
        var before = await Snapshot();

        // Give every author a huge number of points, in every way the ledger allows.
        foreach (var userId in authors)
            for (var i = 0; i < 5; i++) await Ledger(api).AppendAsync(new ContributionEvent { UserId = userId, Action = "written_review", Points = 1000, Reason = "written_review", Reference = $"{userId}-{i}", RootReference = userId });

        Assert.Equal(before, await Snapshot());
    }

    // ---- the audit, the configuration ----

    [MongoFact]
    public async Task An_admin_can_reverse_one_entry_with_a_reason_once_and_a_reader_cannot()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var reader = await api.SignInAsync("+201333333333");
        var entry = new ContributionEvent { UserId = IdOf(reader), Action = "written_review", Points = 10, Reason = "written_review", Reference = "x1", RootReference = "x1" };
        await Ledger(api).AppendAsync(entry);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Post, $"/api/admin/contributions/{entry.Id}/reverse", reader.Cookie, new { reason = "test" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, $"/api/admin/contributions/{entry.Id}/reverse", admin.Cookie, new { reason = "" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Post, $"/api/admin/contributions/{entry.Id}/reverse", admin.Cookie, new { reason = "Paid reviews ring" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(api, HttpMethod.Post, $"/api/admin/contributions/{entry.Id}/reverse", admin.Cookie, new { reason = "again" })).Response.StatusCode);
        Assert.Equal(0, await Points(api, IdOf(reader)));
        var audit = await api.Database.GetCollection<BsonDocument>("audit_log").Find(Builders<BsonDocument>.Filter.Eq("action", "points.reversed")).ToListAsync();
        Assert.Single(audit);
    }

    [MongoFact]
    public async Task The_how_points_work_page_reads_the_real_configuration()
    {
        await using var api = NewApi();
        var config = (await Send(api, HttpMethod.Get, "/api/rewards/config")).Json;
        Assert.Equal(10, config.GetProperty("points").GetProperty("written_review").GetInt32());
        Assert.Equal(5, config.GetProperty("points").GetProperty("photo").GetInt32());
        Assert.Equal(20, config.GetProperty("points").GetProperty("video").GetInt32());
        Assert.Equal(15, config.GetProperty("points").GetProperty("verified_visit").GetInt32());
        Assert.Equal(25, config.GetProperty("points").GetProperty("first_review").GetInt32());
        Assert.Equal(2, config.GetProperty("points").GetProperty("helpful_vote").GetInt32());
        Assert.Equal(20, config.GetProperty("dailyCaps").GetProperty("helpful_vote").GetInt32());
        Assert.Equal([0, 100, 500, 2000], config.GetProperty("ranks").EnumerateArray().Select(r => r.GetProperty("minPoints").GetInt32()));
    }

    [MongoFact]
    public async Task The_numbers_can_be_changed_by_configuration_without_code()
    {
        await using var api = new IdentityApiFactory(new Dictionary<string, string> { ["Rewards:Points:written_review"] = "12", ["Rewards:MinWrittenLength"] = "5" }, fakeAi: true);
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review { EntityId = entity.Id, UserId = IdOf(user), CategoryId = "compounds", Text = "short ok", Status = ReviewStatus.PendingModeration, Answers = [new ReviewAnswer { CriterionId = "security", Score = 4 }] });
        var id = (await api.Services.GetRequiredService<ReviewRepository>().ListByUserAsync(IdOf(user), null)).Single().Id;
        await Publish(api, id);
        Assert.Equal(12 + 25, await Points(api, IdOf(user)));
    }
}
