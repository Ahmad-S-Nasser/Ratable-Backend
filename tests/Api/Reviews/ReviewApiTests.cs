using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Entities.Services;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Reviews;

public sealed class ReviewApiTests
{
    private static IdentityApiFactory Api() => new();

    private static async Task<Entity> SeedEntity(IdentityApiFactory api, string category = "compounds", EntityStatus status = EntityStatus.Active)
    {
        var entity = new Entity
        {
            CategoryId = category,
            Name = new LocalizedText("مدينتي", "Madinaty"),
            Status = status,
            Location = GeoJson.Point(GeoJson.Geographic(31.64, 30.10)),
        };
        entity.SearchTerms = EntityService.BuildSearchTerms(entity);
        await api.Services.GetRequiredService<EntityRepository>().InsertAsync(entity);
        return entity;
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Json)> Send(IdentityApiFactory api, HttpMethod method, string url, string? cookie = null, object? body = null)
    {
        using var http = api.CreateClient();
        var request = cookie is null ? new HttpRequestMessage(method, url) : IdentityApiFactory.WithCookie(method, url, cookie, body);
        if (cookie is null && body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length > 0 && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) ? JsonSerializer.Deserialize<JsonElement>(text) : default;
        return (response, json);
    }

    private static object Answers(params (string Id, object Value)[] answers) => answers.Select(a => new { criterionId = a.Id, value = a.Value }).ToArray();

    private static object Review(object answers, string? text = null, string? language = null, bool? isAnonymous = null) =>
        new { answers, text, language, isAnonymous };

    private static async Task<string> Create(IdentityApiFactory api, string cookie, string entityId, object? body = null)
    {
        var (response, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entityId}/reviews", cookie, body ?? Review(Answers(("security", 4))));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return json.GetProperty("id").GetString()!;
    }

    private static async Task SetState(IdentityApiFactory api, string reviewId, ReviewStatus? status = null, VerificationLevel? level = null)
    {
        var repo = api.Services.GetRequiredService<ReviewRepository>();
        var review = (await repo.GetAsync(reviewId))!;
        if (status is { } s) review.Status = s;
        if (level is { } l) review.Verification = new ReviewVerification { Level = l, Method = l == VerificationLevel.Verified ? "receipt" : null };
        await repo.ReplaceAsync(review);
    }

    private static async Task<Review> SeedReview(IdentityApiFactory api, string entityId, ReviewStatus status, VerificationLevel level, params (string Id, int Score)[] answers)
    {
        var review = new Review
        {
            EntityId = entityId,
            UserId = ObjectId.GenerateNewId().ToString(),
            CategoryId = "compounds",
            Status = status,
            Verification = new ReviewVerification { Level = level },
            Answers = [.. answers.Select(a => new ReviewAnswer { CriterionId = a.Id, Score = a.Score })],
        };
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(review);
        return review;
    }

    // ---- creating ----

    [MongoFact]
    public async Task A_new_review_starts_unverified_and_waiting_for_moderation_and_is_not_public()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);

        var (response, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie,
            Review(Answers(("security", 4), ("noise", 2)), "مكان هادي", isAnonymous: false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("PendingModeration", json.GetProperty("status").GetString());
        Assert.False(json.GetProperty("verified").GetBoolean());
        Assert.Equal(1, json.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(3.0, json.GetProperty("overall").GetDouble());
        Assert.Equal("ar", json.GetProperty("language").GetString());
        var (_, list) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews");
        Assert.Empty(list.EnumerateArray());
        var (_, score) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score");
        Assert.Equal(0, score.GetProperty("publishedReviews").GetInt32());
        Assert.Equal(JsonValueKind.Null, score.GetProperty("overall").ValueKind);
    }

    [MongoFact]
    public async Task The_author_sees_their_own_review_whatever_its_state()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);

        var (_, mine) = await Send(api, HttpMethod.Get, $"/api/me/reviews?entityId={entity.Id}", user.Cookie);

        Assert.Equal(id, mine.EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal("PendingModeration", mine.EnumerateArray().Single().GetProperty("status").GetString());
    }

    [MongoFact]
    public async Task Choice_answers_count_as_the_score_of_the_chosen_option()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api, "restaurants-cafes");

        var (response, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie,
            Review(Answers(("taste", 5), ("wait_time", "over_30"))));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var answers = json.GetProperty("answers").EnumerateArray().ToDictionary(a => a.GetProperty("criterionId").GetString()!);
        Assert.Equal(1, answers["wait_time"].GetProperty("score").GetInt32());
        Assert.Equal("over_30", answers["wait_time"].GetProperty("optionValue").GetString());
        Assert.Equal(3.0, json.GetProperty("overall").GetDouble());
    }

    [MongoFact]
    public async Task Writing_a_review_needs_a_session_and_a_public_entity()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var active = await SeedEntity(api);
        var pending = await SeedEntity(api, status: EntityStatus.PendingModeration);
        var body = Review(Answers(("security", 4)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, $"/api/entities/{active.Id}/reviews", null, body)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Post, $"/api/entities/{pending.Id}/reviews", user.Cookie, body)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Post, $"/api/entities/{ObjectId.GenerateNewId()}/reviews", user.Cookie, body)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Post, "/api/entities/not-an-id/reviews", user.Cookie, body)).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_review_needs_at_least_one_valid_answer_and_every_other_field_is_optional()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api, "restaurants-cafes");
        string url = $"/api/entities/{entity.Id}/reviews";

        var bad = new (object Body, string Error)[]
        {
            (Review(Answers()), "no_answers"),
            (new { text = "only text" }, "no_answers"),
            (Review(Answers(("nonsense", 3))), "invalid_answer"),
            (Review(Answers(("taste", 0))), "invalid_answer"),
            (Review(Answers(("taste", 6))), "invalid_answer"),
            (Review(Answers(("taste", 3.5))), "invalid_answer"),
            (Review(Answers(("taste", "4"))), "invalid_answer"),
            (Review(Answers(("wait_time", "forever"))), "invalid_answer"),
            (Review(Answers(("wait_time", 3))), "invalid_answer"),
            (Review(Answers(("taste", 3), ("taste", 4))), "duplicate_criterion"),
            (Review(Answers(("taste", 3)), text: new string('x', 4001)), "invalid_text"),
            (Review(Answers(("taste", 3)), text: "bad\u0007text"), "invalid_text"),
            (Review(Answers(("taste", 3)), language: "fr"), "invalid_text"),
        };
        foreach (var (body, error) in bad)
        {
            var (response, json) = await Send(api, HttpMethod.Post, url, user.Cookie, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(error, json.GetProperty("error").GetString());
        }

        // Answers only: no text, no language, no anonymity choice.
        var (ok, _) = await Send(api, HttpMethod.Post, url, user.Cookie, new { answers = Answers(("taste", 3)) });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [MongoFact]
    public async Task The_language_is_taken_from_the_request_or_guessed_from_the_text()
    {
        await using var api = Api();
        var entity = await SeedEntity(api, "restaurants-cafes");
        var cases = new (string Phone, string? Text, string? Language, string Expected)[]
        {
            ("+201000000011", "الأكل ممتاز", null, "ar"),
            ("+201000000012", "Great food", null, "en"),
            ("+201000000013", "Great food", "ar", "ar"),
            ("+201000000014", "الأكل ممتاز", "EN", "en"),
            ("+201000000015", null, null, "ar"),
        };
        foreach (var c in cases)
        {
            var user = await api.SignInAsync(c.Phone);
            api.Time.Advance(TimeSpan.FromMinutes(2));
            var (_, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie, Review(Answers(("taste", 4)), c.Text, c.Language));
            Assert.Equal(c.Expected, json.GetProperty("language").GetString());
        }
    }

    [MongoFact]
    public async Task Anonymity_defaults_to_the_category_setting_unless_the_author_chooses()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var other = await api.SignInAsync("+201222222222");
        var compound = await SeedEntity(api, "compounds");
        var cafe = await SeedEntity(api, "restaurants-cafes");

        var (_, byDefault) = await Send(api, HttpMethod.Post, $"/api/entities/{compound.Id}/reviews", user.Cookie, Review(Answers(("security", 3))));
        var (_, chosen) = await Send(api, HttpMethod.Post, $"/api/entities/{compound.Id}/reviews", other.Cookie, Review(Answers(("security", 3)), isAnonymous: false));
        var (_, cafeDefault) = await Send(api, HttpMethod.Post, $"/api/entities/{cafe.Id}/reviews", user.Cookie, Review(Answers(("taste", 3))));

        Assert.True(byDefault.GetProperty("isAnonymous").GetBoolean());   // compounds default to anonymous
        Assert.False(chosen.GetProperty("isAnonymous").GetBoolean());
        Assert.False(cafeDefault.GetProperty("isAnonymous").GetBoolean());
    }

    // ---- one review per account per entity (rule 4) ----

    [MongoFact]
    public async Task One_review_per_account_per_entity()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var other = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var anotherEntity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);

        var (again, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie, Review(Answers(("noise", 1))));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_reviewed", json.GetProperty("error").GetString());
        Assert.Equal(id, json.GetProperty("reviewId").GetString());
        await Create(api, other.Cookie!, entity.Id);          // someone else may
        await Create(api, user.Cookie!, anotherEntity.Id);    // the same person may review another place
        Assert.Equal(1, await api.Database.GetCollection<Review>(Collections.Reviews).CountDocumentsAsync(r => r.UserId == user.Body.GetProperty("user").GetProperty("id").GetString() && r.EntityId == entity.Id));
    }

    [MongoFact]
    public async Task Parallel_posts_still_make_only_one_review()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie, Review(Answers(("security", 4))))));

        Assert.Equal(1, results.Count(r => r.Response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.Response.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await api.Database.GetCollection<Review>(Collections.Reviews).CountDocumentsAsync(FilterDefinition<Review>.Empty));
    }

    // ---- editing keeps history ----

    [MongoFact]
    public async Task Editing_saves_the_old_version_as_a_dated_revision_and_moves_the_review_on()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id, Review(Answers(("security", 4)), "الأمن كويس"));
        var created = (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!;
        api.Time.Advance(TimeSpan.FromDays(3));

        var (response, json) = await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", 2), ("noise", 5)), "الأمن اتغير"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, json.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("الأمن اتغير", json.GetProperty("text").GetString());
        var revisions = await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id);
        var old = Assert.Single(revisions);
        Assert.Equal(1, old.RevisionNumber);
        Assert.Equal("الأمن كويس", old.Text);
        Assert.Equal(4, Assert.Single(old.Answers).Score);
        Assert.InRange((old.ChangedAt - created.CreatedAt).Duration().TotalMilliseconds, 0, 1);          // when that version was written
        Assert.InRange((old.SupersededAt - api.Time.GetUtcNow().UtcDateTime).Duration().TotalMilliseconds, 0, 1); // when it was replaced
        var now = (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!;
        Assert.Equal(2, now.Answers.Count);
        Assert.InRange((now.UpdatedAt - api.Time.GetUtcNow().UtcDateTime).Duration().TotalMilliseconds, 0, 1); // the edited-on date
    }

    [MongoFact]
    public async Task Every_edit_adds_a_revision_in_order()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);

        for (var score = 1; score <= 3; score++)
        {
            api.Time.Advance(TimeSpan.FromHours(1));
            await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", score)), $"v{score}"));
        }

        var revisions = await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id);
        Assert.Equal([1, 2, 3], revisions.Select(r => r.RevisionNumber));
        Assert.Equal([4, 1, 2], revisions.Select(r => r.Answers.Single().Score)); // each is the version that was replaced
        Assert.Equal(4, (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!.RevisionNumber);
    }

    [MongoFact]
    public async Task An_edit_that_changes_nothing_makes_no_revision()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id, Review(Answers(("security", 4), ("noise", 3)), "نص", "ar", true));

        var (response, json) = await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("noise", 3), ("security", 4)), "  نص  ", "ar", true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, json.GetProperty("revisionNumber").GetInt32());
        Assert.Empty(await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id));
    }

    [MongoFact]
    public async Task An_edited_review_goes_back_to_moderation_but_keeps_its_verification()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);
        await SetState(api, id, ReviewStatus.Published, VerificationLevel.Verified);

        var (_, json) = await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", 1)), "تغيير"));

        Assert.Equal("PendingModeration", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("verified").GetBoolean());
        var (_, score) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score"); // not public, so not in the score, until moderated again
        Assert.Equal(0, score.GetProperty("verifiedReviews").GetInt32());
    }

    [MongoFact]
    public async Task Only_the_author_can_edit_or_delete()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var other = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);
        var body = Review(Answers(("security", 1)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Put, $"/api/reviews/{id}", null, body)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Put, $"/api/reviews/{id}", other.Cookie, body)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Delete, $"/api/reviews/{id}", other.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Put, "/api/reviews/not-an-id", user.Cookie, body)).Response.StatusCode);
        Assert.Equal(4, (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!.Answers.Single().Score);
    }

    [MongoFact]
    public async Task Edits_made_at_the_same_time_never_lose_or_duplicate_a_revision()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(i =>
            Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", (i % 5) + 1)), $"edit {i}"))));

        var succeeded = results.Count(r => r.Response.StatusCode == HttpStatusCode.OK);
        Assert.All(results, r => Assert.Contains(r.Response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.True(succeeded >= 1);
        var review = (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!;
        var revisions = await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id);
        Assert.Equal(review.RevisionNumber - 1, revisions.Count);
        Assert.Equal(Enumerable.Range(1, revisions.Count), revisions.Select(r => r.RevisionNumber));
    }

    // ---- deleting hides but keeps (rule 4) ----

    [MongoFact]
    public async Task Deleting_hides_the_review_everywhere_but_keeps_its_revisions()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);
        await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", 1)), "v2"));
        await SetState(api, id, ReviewStatus.Published, VerificationLevel.Verified);

        var (deleted, _) = await Send(api, HttpMethod.Delete, $"/api/reviews/{id}", user.Cookie);
        var (again, _) = await Send(api, HttpMethod.Delete, $"/api/reviews/{id}", user.Cookie);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode); // deleting twice is fine
        var (_, list) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews");
        Assert.Empty(list.EnumerateArray());
        var (_, score) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score");
        Assert.Equal(0, score.GetProperty("verifiedReviews").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/reviews/{id}/history")).Response.StatusCode);
        var stored = (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!;
        Assert.Equal(ReviewStatus.Deleted, stored.Status);
        Assert.NotNull(stored.DeletedAt);
        Assert.Single(await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id));
        var (_, mine) = await Send(api, HttpMethod.Get, "/api/me/reviews", user.Cookie);
        Assert.Equal("Deleted", mine.EnumerateArray().Single().GetProperty("status").GetString());
    }

    [MongoFact]
    public async Task A_deleted_review_blocks_a_new_one_but_editing_it_restores_it_with_history()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id);
        await Send(api, HttpMethod.Delete, $"/api/reviews/{id}", user.Cookie);

        var (create, _) = await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", user.Cookie, Review(Answers(("security", 5))));
        var (restore, json) = await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", 5)), "رجعت"));

        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("PendingModeration", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("revisionNumber").GetInt32());
        Assert.Null((await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!.DeletedAt);
        Assert.Single(await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id));
    }

    // ---- public reading ----

    [MongoFact]
    public async Task The_public_list_shows_only_published_reviews_labels_verification_and_never_shows_the_author()
    {
        await using var api = Api();
        var entity = await SeedEntity(api);
        var verified = await SeedReview(api, entity.Id, ReviewStatus.Published, VerificationLevel.Verified, ("security", 5));
        var unverified = await SeedReview(api, entity.Id, ReviewStatus.Published, VerificationLevel.Unverified, ("security", 1));
        await SeedReview(api, entity.Id, ReviewStatus.PendingModeration, VerificationLevel.Verified, ("security", 3));
        await SeedReview(api, entity.Id, ReviewStatus.Hidden, VerificationLevel.Verified, ("security", 3));
        await SeedReview(api, entity.Id, ReviewStatus.Deleted, VerificationLevel.Verified, ("security", 3));

        using var http = api.CreateClient();
        var response = await http.GetAsync($"/api/entities/{entity.Id}/reviews");
        var text = await response.Content.ReadAsStringAsync();
        var list = JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);

        Assert.Equal(2, list.Count);
        Assert.True(list[verified.Id].GetProperty("verified").GetBoolean());
        Assert.False(list[unverified.Id].GetProperty("verified").GetBoolean());
        Assert.DoesNotContain(verified.UserId, text);
        Assert.DoesNotContain(unverified.UserId, text);
        foreach (var forbidden in new[] { "userId", "isAnonymous", "status" })
            Assert.DoesNotContain($"\"{forbidden}\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(list.Values, r =>
        {
            Assert.True(r.GetProperty("anonymous").GetBoolean()); // these authors have no public name
            Assert.Equal(JsonValueKind.Null, r.GetProperty("author").ValueKind);
        });
    }

    [MongoFact]
    public async Task The_public_list_pages_newest_first_and_only_for_public_entities()
    {
        await using var api = Api();
        var entity = await SeedEntity(api);
        var pending = await SeedEntity(api, status: EntityStatus.PendingModeration);
        var created = new List<Review>();
        for (var i = 0; i < 5; i++)
        {
            api.Time.Advance(TimeSpan.FromMinutes(1));
            var r = await SeedReview(api, entity.Id, ReviewStatus.Published, VerificationLevel.Unverified, ("security", 3));
            r.CreatedAt = api.Time.GetUtcNow().UtcDateTime;
            await api.Services.GetRequiredService<ReviewRepository>().ReplaceAsync(r);
            created.Add(r);
        }

        var (_, firstPage) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews?limit=2");
        var (_, secondPage) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/reviews?limit=2&skip=2");

        Assert.Equal(created.AsEnumerable().Reverse().Take(2).Select(r => r.Id), firstPage.EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(created.AsEnumerable().Reverse().Skip(2).Take(2).Select(r => r.Id), secondPage.EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/entities/{pending.Id}/reviews")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/entities/{pending.Id}/score")).Response.StatusCode);
    }

    [MongoFact]
    public async Task The_score_uses_only_verified_reviews_and_shows_a_criterion_only_after_five_answers()
    {
        await using var api = Api();
        var entity = await SeedEntity(api);
        for (var i = 0; i < 4; i++) await SeedReview(api, entity.Id, ReviewStatus.Published, VerificationLevel.Verified, ("security", 5), ("noise", 3));
        // none of these may count
        await SeedReview(api, entity.Id, ReviewStatus.Published, VerificationLevel.Unverified, ("security", 1), ("noise", 1));
        await SeedReview(api, entity.Id, ReviewStatus.PendingModeration, VerificationLevel.Verified, ("security", 1));
        await SeedReview(api, entity.Id, ReviewStatus.Hidden, VerificationLevel.Verified, ("security", 1));
        await SeedReview(api, entity.Id, ReviewStatus.Deleted, VerificationLevel.Verified, ("security", 1));

        var (_, four) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score");
        Assert.Equal(4, four.GetProperty("verifiedReviews").GetInt32());
        Assert.Equal(5, four.GetProperty("publishedReviews").GetInt32()); // the unverified one is published, just not in the score
        Assert.Equal(4.0, four.GetProperty("overall").GetDouble());
        Assert.Equal(5, four.GetProperty("minAnswersForCriterion").GetInt32());
        var hidden = four.GetProperty("criteria").EnumerateArray().Single(c => c.GetProperty("criterionId").GetString() == "security");
        Assert.Equal(4, hidden.GetProperty("answers").GetInt32());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("score").ValueKind);

        await SeedReview(api, entity.Id, ReviewStatus.Published, VerificationLevel.Verified, ("security", 3));
        var (_, five) = await Send(api, HttpMethod.Get, $"/api/entities/{entity.Id}/score");
        var shown = five.GetProperty("criteria").EnumerateArray().Single(c => c.GetProperty("criterionId").GetString() == "security");
        Assert.Equal(5, shown.GetProperty("answers").GetInt32());
        Assert.Equal(4.6, shown.GetProperty("score").GetDouble());
        var noise = five.GetProperty("criteria").EnumerateArray().Single(c => c.GetProperty("criterionId").GetString() == "noise");
        Assert.Equal(JsonValueKind.Null, noise.GetProperty("score").ValueKind); // still only 4 answers
    }

    [MongoFact]
    public async Task The_public_history_shows_the_current_version_and_the_earlier_versions_that_were_public()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id, Review(Answers(("security", 5)), "النسخة الأولى"));
        await SetState(api, id, ReviewStatus.Published);                                   // v1 approved and public
        api.Time.Advance(TimeSpan.FromDays(1));
        await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", 3)), "النسخة الثانية"));
        // v2 is waiting for moderation, so the review is not public yet: no history to show
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/reviews/{id}/history")).Response.StatusCode);
        await SetState(api, id, ReviewStatus.Published);                                   // v2 approved
        api.Time.Advance(TimeSpan.FromDays(1));
        await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", 1)), "نسخة رفضت"));
        await SetState(api, id, ReviewStatus.Hidden);                                       // v3 rejected by a moderator
        await SetState(api, id, ReviewStatus.Published);                                    // (re-published for the test)

        var (_, history) = await Send(api, HttpMethod.Get, $"/api/reviews/{id}/history");

        Assert.Equal(3, history.GetProperty("current").GetProperty("revisionNumber").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, history.GetProperty("current").GetProperty("editedOn").ValueKind);
        var earlier = history.GetProperty("earlierVersions").EnumerateArray().ToList();
        Assert.Equal(["النسخة الأولى", "النسخة الثانية"], earlier.Select(e => e.GetProperty("text").GetString()));
        Assert.Equal([1, 2], earlier.Select(e => e.GetProperty("revisionNumber").GetInt32()));
        Assert.Equal(5.0, earlier[0].GetProperty("overall").GetDouble());
        Assert.NotEqual(earlier[0].GetProperty("writtenAt").GetDateTime(), earlier[0].GetProperty("replacedAt").GetDateTime());
        Assert.DoesNotContain(user.Body.GetProperty("user").GetProperty("id").GetString()!, history.GetRawText());
    }

    [MongoFact]
    public async Task An_earlier_version_that_was_never_public_is_not_in_the_public_history()
    {
        await using var api = Api();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await Create(api, user.Cookie!, entity.Id, Review(Answers(("security", 5)), "never approved text"));
        await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Review(Answers(("security", 4)), "second"));  // v1 was still pending
        await SetState(api, id, ReviewStatus.Published);

        var (_, history) = await Send(api, HttpMethod.Get, $"/api/reviews/{id}/history");

        Assert.Empty(history.GetProperty("earlierVersions").EnumerateArray());
        Assert.DoesNotContain("never approved text", history.GetRawText());
        Assert.Single(await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id)); // it is kept, just not shown
    }

    // ---- categories and regression ----

    [MongoFact]
    public async Task Categories_are_listed_with_the_criteria_the_review_form_is_built_from()
    {
        await using var api = Api();

        var (_, all) = await Send(api, HttpMethod.Get, "/api/categories");
        var (found, compounds) = await Send(api, HttpMethod.Get, "/api/categories/compounds");
        var (_, cafes) = await Send(api, HttpMethod.Get, "/api/categories/restaurants-cafes");

        Assert.Equal(7, all.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(7, compounds.GetProperty("criteria").GetArrayLength());
        Assert.Equal("الصيانة", compounds.GetProperty("criteria")[0].GetProperty("label").GetProperty("ar").GetString());
        var wait = cafes.GetProperty("criteria").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "wait_time");
        Assert.Equal("Choice", wait.GetProperty("answerType").GetString());
        Assert.Equal(3, wait.GetProperty("options").GetArrayLength());
        Assert.DoesNotContain("\"score\"", wait.GetRawText()); // how options are scored is not part of the form
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/categories/spaceships")).Response.StatusCode);
    }

    [MongoFact]
    public async Task An_id_that_is_not_an_object_id_is_simply_not_found()
    {
        await using var api = Api();

        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/entities/not-an-id")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/entities/not-an-id/reviews")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/reviews/not-an-id/history")).Response.StatusCode);
    }
}
