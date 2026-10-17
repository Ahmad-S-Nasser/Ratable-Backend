using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Comments.Models;
using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Moderation.Services;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Verification.Services;
using Ratable.Shared.Content;
using Ratable.Shared.Data;
using Ratable.Shared.Moderation;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Moderation;

/// <summary>What happens to a new or edited review or comment: the behaviour signals, the AI pre-screen and the routing rules, end to end.</summary>
public sealed class ModerationIntakeTests
{
    // ---- the routing rules, through the real flow ----

    [MongoFact]
    public async Task An_unverified_accounts_review_without_proof_waits_for_a_moderator_even_when_the_checks_are_clean()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);

        var review = await NewReview(api, user, entity.Id, Clean());

        var c = await CaseFor(api, review);
        Assert.Equal(CaseState.Open, c.State);
        Assert.Equal(CaseRoute.Human, c.Route);
        Assert.Equal([RoutingPolicy.NoProofUnverifiedAccount], c.Reasons);
        Assert.Equal("completed", c.AiStatus);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, review)).Status);
    }

    [MongoFact]
    public async Task A_verified_accounts_review_without_proof_is_published_when_the_checks_pass_and_stays_unverified()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        await VerifyPhone(api, user);
        var entity = await SeedEntity(api);

        var review = await NewReview(api, user, entity.Id, Clean());

        var c = await CaseFor(api, review);
        Assert.Equal(CaseState.Decided, c.State);
        Assert.Equal(CaseRoute.Auto, c.Route);
        Assert.Equal("auto", c.DecidedBy);
        Assert.Equal(CaseDecision.Published, c.Decision);
        var stored = await ReviewOf(api, review);
        Assert.Equal(ReviewStatus.Published, stored.Status);
        Assert.Equal(VerificationLevel.Unverified, stored.Verification.Level);   // a verified phone shortens the wait; only proof makes a review Verified
        var score = (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score")).Json;
        Assert.Equal(0, score.GetProperty("verifiedReviews").GetInt32());        // verification never changes a score
        Assert.Equal(1, score.GetProperty("publishedReviews").GetInt32());
    }

    [MongoFact]
    public async Task A_verified_accounts_review_that_the_checks_flag_goes_to_a_moderator_first()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        await VerifyPhone(api, user);
        var entity = await SeedEntity(api);

        var review = await NewReview(api, user, entity.Id, Flagged(abuse: 0.92, personalData: 0.7));

        var c = await CaseFor(api, review);
        Assert.Equal(CaseState.Open, c.State);
        Assert.Equal(["abuse", "personal_data"], c.Reasons);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, review)).Status);
    }

    [MongoFact]
    public async Task A_flag_below_the_confidence_threshold_does_not_count()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        await VerifyPhone(api, user);
        var entity = await SeedEntity(api);

        var review = await NewReview(api, user, entity.Id, Flagged(abuse: 0.3));

        Assert.Equal(ReviewStatus.Published, (await ReviewOf(api, review)).Status);
    }

    [MongoFact]
    public async Task A_review_of_an_individual_always_goes_to_a_moderator_even_from_a_verified_phone()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        await VerifyPhone(api, user);
        await SetRatesIndividuals(api, "compounds", true);
        var entity = await SeedEntity(api);

        var review = await NewReview(api, user, entity.Id, Clean());

        var c = await CaseFor(api, review);
        Assert.Equal(CaseState.Open, c.State);
        Assert.Equal([RoutingPolicy.Individual], c.Reasons);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, review)).Status);
    }

    [MongoFact]
    public async Task When_the_ai_is_capped_or_down_nothing_is_published_by_default()
    {
        await using var api = NewApi();
        var capped = await api.SignInAsync("+201111111111");
        var down = await api.SignInAsync("+201222222222");
        await VerifyPhone(api, capped);
        await VerifyPhone(api, down);
        var entity = await SeedEntity(api);

        api.Ai.DeferAll = true;
        var a = await NewReview(api, capped, entity.Id, null);
        api.Ai.DeferAll = false;
        api.Ai.FailAll = true;
        var b = await NewReview(api, down, entity.Id, null);

        Assert.Equal(["ai_deferred"], (await CaseFor(api, a)).Reasons);
        Assert.Equal("deferred", (await CaseFor(api, a)).AiStatus);
        Assert.Equal(["ai_unavailable"], (await CaseFor(api, b)).Reasons);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, a)).Status);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, b)).Status);
    }

    [MongoFact]
    public async Task A_pre_screen_that_returns_nothing_usable_is_not_clean()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        await VerifyPhone(api, user);
        var entity = await SeedEntity(api);

        var review = await NewReview(api, user, entity.Id, answer: null); // the fake returns an empty answer

        Assert.Equal(["ai_unavailable"], (await CaseFor(api, review)).Reasons);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, review)).Status);
    }

    [MongoFact]
    public async Task A_review_with_no_text_has_nothing_to_screen_and_follows_the_account_rule()
    {
        await using var api = NewApi();
        var verified = await api.SignInAsync("+201111111111");
        var plain = await api.SignInAsync("+201222222222");
        await VerifyPhone(api, verified);
        var entity = await SeedEntity(api);

        var a = await NewReview(api, verified, entity.Id, null, text: "");
        var b = await NewReview(api, plain, entity.Id, null, text: "");

        Assert.Empty(api.Ai.Requests);
        Assert.Equal("not_needed", (await CaseFor(api, a)).AiStatus);
        Assert.Equal(ReviewStatus.Published, (await ReviewOf(api, a)).Status);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, b)).Status);
    }

    // ---- a proof accepted later ----

    [MongoFact]
    public async Task A_review_held_only_for_lack_of_proof_is_published_when_a_proof_is_accepted()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Clean());
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, review)).Status);
        api.Ai.Enqueue(new ProofReading("lease", "مدينتي", true, true, api.Time.GetUtcNow().ToString("yyyy-MM-dd"), true, false, false, 0.95));

        var (response, _) = await UploadProof(api, user.Cookie, review, Png(), "lease_contract");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await ReviewOf(api, review);
        Assert.Equal(VerificationLevel.Verified, stored.Verification.Level);
        Assert.Equal(ReviewStatus.Published, stored.Status);
        var c = await CaseFor(api, review);
        Assert.Equal(CaseState.Decided, c.State);
        Assert.Equal("auto", c.DecidedBy);
        Assert.True(c.HasVerifiedProof);
        var score = (await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score")).Json;
        Assert.Equal(1, score.GetProperty("verifiedReviews").GetInt32());
    }

    [MongoFact]
    public async Task A_proof_does_not_publish_a_review_that_is_held_for_another_reason()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Flagged(spam: 0.9));
        api.Ai.Enqueue(new ProofReading("lease", "مدينتي", true, true, api.Time.GetUtcNow().ToString("yyyy-MM-dd"), true, false, false, 0.95));

        await UploadProof(api, user.Cookie, review, Png(), "lease_contract");

        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(api, review)).Verification.Level);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, review)).Status);
        Assert.Equal(CaseState.Open, (await CaseFor(api, review)).State);
    }

    // ---- the behaviour signals ----

    [MongoFact]
    public async Task The_same_text_posted_again_is_flagged_even_when_the_spelling_is_changed()
    {
        await using var api = NewApi();
        var a = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var b = await api.SignInAsync("+201222222222");
        await VerifyPhone(api, a);
        await VerifyPhone(api, b);
        var first = await SeedEntity(api);
        var second = await SeedEntity(api);

        var one = await NewReview(api, a, first.Id, Clean(), text: "الخدمة ممتازة والأمن كويس جدا في المكان ده");
        var two = await NewReview(api, b, second.Id, Clean(), text: "الخدمه ممتازه والامن كويس جدا في المكان ده");

        Assert.Equal(ReviewStatus.Published, (await ReviewOf(api, one)).Status);
        Assert.Equal(["copied_text"], (await CaseFor(api, two)).Reasons);
        Assert.Equal(ReviewStatus.PendingModeration, (await ReviewOf(api, two)).Status);
    }

    [MongoFact]
    public async Task Short_texts_are_not_compared()
    {
        await using var api = NewApi();
        var a = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var b = await api.SignInAsync("+201222222222");
        await VerifyPhone(api, a);
        await VerifyPhone(api, b);
        var entity = await SeedEntity(api);

        await NewReview(api, a, entity.Id, Clean(), text: "ممتاز");
        var two = await NewReview(api, b, entity.Id, Clean(), text: "ممتاز");

        Assert.Empty((await CaseFor(api, two)).Reasons);
    }

    [MongoFact]
    public async Task A_burst_of_reviews_for_one_entity_is_flagged()
    {
        await using var api = NewApi(new() { ["Moderation:BurstThreshold"] = "3" });
        var entity = await SeedEntity(api);
        var reviews = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var user = await api.SignInAsync($"+20110000000{i}");
            await VerifyPhone(api, user);
            reviews.Add(await NewReview(api, user, entity.Id, Clean(), text: $"review number {i} with its own words {Guid.NewGuid():N}"));
            api.Time.Advance(TimeSpan.FromMinutes(2));
        }

        var reasons = new List<string[]>();
        foreach (var r in reviews) reasons.Add([.. (await CaseFor(api, r)).Reasons]);
        Assert.Equal([[], [], ["review_burst"], ["review_burst"]], reasons);
    }

    [MongoFact]
    public async Task The_burst_window_forgets_old_reviews()
    {
        await using var api = NewApi(new() { ["Moderation:BurstThreshold"] = "3" });
        var entity = await SeedEntity(api);
        for (var i = 0; i < 3; i++)
        {
            var user = await api.SignInAsync($"+20110000000{i}");
            await VerifyPhone(api, user);
            var id = await NewReview(api, user, entity.Id, Clean(), text: $"spread out review {i} with unique words {Guid.NewGuid():N}");
            Assert.Empty((await CaseFor(api, id)).Reasons);
            api.Time.Advance(TimeSpan.FromHours(2));
        }
    }

    [MongoFact]
    public async Task Several_accounts_on_one_device_reviewing_the_same_place_are_flagged()
    {
        await using var api = NewApi();
        var entity = await SeedEntity(api);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var user = await api.SignInAsync($"+20111000000{i}", device: "device-abc-123");
            await VerifyPhone(api, user);
            ids.Add(await NewReview(api, user, entity.Id, Clean(), text: $"my own words number {i} {Guid.NewGuid():N}"));
            api.Time.Advance(TimeSpan.FromMinutes(2));
        }
        var other = await api.SignInAsync("+201119999999", device: "someone-elses-phone");
        await VerifyPhone(api, other);
        var control = await NewReview(api, other, entity.Id, Clean(), text: $"a different person {Guid.NewGuid():N}");

        Assert.Empty((await CaseFor(api, ids[0])).Reasons);
        Assert.Empty((await CaseFor(api, ids[1])).Reasons);                         // only one other account so far
        Assert.Equal(["shared_device_or_network"], (await CaseFor(api, ids[2])).Reasons);
        Assert.Empty((await CaseFor(api, control)).Reasons);
    }

    [MongoFact]
    public async Task Only_hashes_of_the_device_and_network_are_stored()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111", device: "device-abc-123");
        await VerifyPhone(api, user);
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Clean());

        var c = await CaseFor(api, review);

        Assert.NotNull(c.DeviceHash);
        Assert.DoesNotContain("device-abc-123", c.DeviceHash);
        Assert.Equal(64, c.DeviceHash!.Length);
        Assert.Equal(64, c.TextHash!.Length);
    }

    // ---- the pre-screen ----

    [MongoFact]
    public async Task The_pre_screen_gets_the_text_and_nothing_that_identifies_the_author_or_the_place()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        await SetProfileName(api, user, "Sara Ahmed");
        var entity = await SeedEntity(api);

        await NewReview(api, user, entity.Id, Clean(), text: "the lifts are slow in the evening", anonymous: true);

        var asked = Assert.Single(api.Ai.Requests);
        Assert.Equal("moderation_review", asked.Feature);
        Assert.Equal(AiTierSmall(), asked.Tier.ToString());
        Assert.Contains("the lifts are slow", asked.Prompt);
        Assert.Contains("\"isAnonymous\":true", asked.Prompt);
        var everything = asked.Prompt + asked.System;
        Assert.DoesNotContain(IdOf(user), everything);
        Assert.DoesNotContain("Sara", everything);
        Assert.DoesNotContain("1111111111", everything);
        Assert.DoesNotContain("Madinaty", everything);
        Assert.DoesNotContain(entity.Id, everything);
    }

    private static string AiTierSmall() => "Small";

    private static async Task SetProfileName(Ratable.Api.Tests.Identity.IdentityApiFactory api, Ratable.Api.Tests.Identity.IdentityApiFactory.SignedIn user, string name) =>
        await SetProfile(api, user.Cookie!, name);

    [MongoFact]
    public async Task The_verdict_is_kept_on_the_case_with_the_model_name_and_never_the_text()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Flagged(abuse: 0.8, fake: 0.4), text: "a very unusual sentence about purple elephants");

        var c = await CaseFor(api, review);

        Assert.True(c.Prescreen!.Abuse.Flagged);
        Assert.Equal(0.8, c.Prescreen.Abuse.Confidence);
        Assert.Equal("fake", c.Prescreen.Model); // the fake client reports this model name
        var raw = (await api.Database.GetCollection<MongoDB.Bson.BsonDocument>(Collections.ModerationCases).Find(new MongoDB.Bson.BsonDocument()).SingleAsync()).ToJson();
        Assert.DoesNotContain("purple elephants", raw);
    }

    [MongoFact]
    public async Task Identifying_details_are_a_problem_only_for_an_anonymous_author()
    {
        await using var api = NewApi();
        var anonymous = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var named = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);

        var a = await NewReview(api, anonymous, entity.Id, Flagged(identifying: 0.9), text: "I live in flat 12 building 3 and the noise is bad", anonymous: true);
        var b = await NewReview(api, named, entity.Id, Flagged(identifying: 0.9), text: "I live in flat 14 building 3 and the noise is bad too", anonymous: false);

        Assert.Contains("identifying_details", (await CaseFor(api, a)).Reasons);
        Assert.DoesNotContain("identifying_details", (await CaseFor(api, b)).Reasons);
    }

    // ---- edits ----

    [MongoFact]
    public async Task An_edit_is_screened_again_and_replaces_the_waiting_case_of_the_old_version()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Clean(), text: "first version of the text here");

        api.Ai.Enqueue(Flagged(abuse: 0.9));
        var (response, _) = await Send(api, HttpMethod.Put, $"/api/reviews/{review}", user.Cookie, new { answers = Answers(("security", 2)), text = "second version, now rude" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var all = await AllCases(api);
        Assert.Equal(2, all.Count);
        Assert.Equal([CaseState.Superseded, CaseState.Open], all.OrderBy(c => c.RevisionNumber).Select(c => c.State));
        Assert.Equal(["abuse"], all.Single(c => c.RevisionNumber == 2).Reasons);
        Assert.Equal(2, api.Ai.Requests.Count);
    }

    [MongoFact]
    public async Task A_published_review_that_is_edited_by_a_verified_account_is_published_again_when_the_new_text_is_clean()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        await VerifyPhone(api, user);
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Clean());
        Assert.Equal(ReviewStatus.Published, (await ReviewOf(api, review)).Status);

        api.Ai.Enqueue(Clean());
        var (_, json) = await Send(api, HttpMethod.Put, $"/api/reviews/{review}", user.Cookie, new { answers = Answers(("security", 3)), text = "an edited version of the review text" });

        Assert.Equal("Published", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(CaseState.Decided, (await CaseFor(api, review)).State);
    }

    [MongoFact]
    public async Task Changing_only_the_anonymity_flag_does_not_create_a_case()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Clean(), anonymous: false);

        await Send(api, HttpMethod.Put, $"/api/reviews/{review}/anonymity", user.Cookie, new { isAnonymous = true });

        Assert.Single(await AllCases(api));
        Assert.Single(api.Ai.Requests);
    }

    [MongoFact]
    public async Task Each_version_is_screened_only_once()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var review = await NewReview(api, user, entity.Id, Clean());
        var intake = api.Services.GetRequiredService<IModerationIntake>();
        var again = new ModerationSubmission(ContentKind.Review, review, IdOf(user), entity.Id, "compounds", "A review with enough words in it to read", "en", 1, false, false);

        await intake.SubmitAsync(again);
        await intake.SubmitAsync(again);

        Assert.Single(await AllCases(api));
        Assert.Single(api.Ai.Requests);
    }

    // ---- nothing waits without a case ----

    [MongoFact]
    public async Task The_backfill_queues_a_pending_review_that_has_no_case_and_does_it_only_once()
    {
        await using var api = NewApi();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var orphan = new Review
        {
            EntityId = entity.Id,
            UserId = IdOf(user),
            CategoryId = "compounds",
            Status = ReviewStatus.PendingModeration,
            Answers = [new ReviewAnswer { CriterionId = "security", Score = 4 }],
            Text = "a review that was saved when moderation was down",
            Language = "en",
        };
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(orphan);
        api.Ai.Enqueue(Clean());
        var service = api.Services.GetRequiredService<ModerationService>();

        Assert.Equal(1, await service.BackfillAsync());
        Assert.Equal(0, await service.BackfillAsync());

        var c = await CaseFor(api, orphan.Id);
        Assert.Equal(CaseState.Open, c.State);
        Assert.Equal(IdOf(user), c.AuthorId);
    }

    // ---- comments ----

    [MongoFact]
    public async Task Comments_get_the_same_screening_and_routing()
    {
        await using var api = NewApi();
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var verified = await api.SignInAsync("+201222222222");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var plain = await api.SignInAsync("+201333333333");
        await VerifyPhone(api, verified);
        var entity = await SeedEntity(api);
        var review = await CreateReview(api, author.Cookie!, entity.Id);

        api.Ai.Enqueue(Clean());
        var (_, a) = await Send(api, HttpMethod.Post, $"/api/reviews/{review}/comments", verified.Cookie, new { text = "thanks, this helped me" });
        api.Ai.Enqueue(Clean());
        var (_, b) = await Send(api, HttpMethod.Post, $"/api/reviews/{review}/comments", plain.Cookie, new { text = "me too, thanks for sharing" });
        api.Ai.Enqueue(Flagged(abuse: 0.9));
        var (_, c) = await Send(api, HttpMethod.Post, $"/api/reviews/{review}/comments", verified.Cookie, new { text = "you are all idiots" });

        Assert.Equal("Published", a.GetProperty("status").GetString());
        Assert.Equal("PendingModeration", b.GetProperty("status").GetString());
        Assert.Equal("PendingModeration", c.GetProperty("status").GetString());
        Assert.Equal(["no_proof_unverified_account"], (await CaseFor(api, b.GetProperty("id").GetString()!)).Reasons);
        Assert.Equal(["abuse"], (await CaseFor(api, c.GetProperty("id").GetString()!)).Reasons);
        Assert.Equal("moderation_comment", api.Ai.Requests.Last().Feature);
        Assert.Single((await Send(api, HttpMethod.Get, $"/api/reviews/{review}/comments")).Json.EnumerateArray());
    }

    [MongoFact]
    public async Task An_edited_comment_is_screened_again_and_a_moderator_can_see_what_changed()
    {
        await using var api = NewApi();
        var author = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var commenter = await api.SignInAsync("+201222222222");
        await VerifyPhone(api, commenter);
        var entity = await SeedEntity(api);
        var review = await CreateReview(api, author.Cookie!, entity.Id);
        api.Ai.Enqueue(Clean());
        var (_, created) = await Send(api, HttpMethod.Post, $"/api/reviews/{review}/comments", commenter.Cookie, new { text = "a polite first version" });
        var id = created.GetProperty("id").GetString()!;

        api.Ai.Enqueue(Flagged(abuse: 0.9));
        await Send(api, HttpMethod.Put, $"/api/comments/{id}", commenter.Cookie, new { text = "now an insulting second version" });

        var cases = (await AllCases(api)).Where(c => c.ContentId == id).OrderBy(c => c.RevisionNumber).ToList();
        Assert.Equal([1, 2], cases.Select(c => c.RevisionNumber));
        Assert.Equal(CaseState.Open, cases[1].State);
        var view = await api.Services.GetRequiredService<IEnumerable<IContentModerator>>().First(m => m.Kind == ContentKind.Comment).GetViewAsync(id);
        Assert.Equal("a polite first version", view!.PreviousText);
        Assert.Equal("now an insulting second version", view.Text);
        Assert.Equal(CommentStatus.PendingModeration, (await api.Services.GetRequiredService<CommentRepository>().GetAsync(id))!.Status);
    }
}
