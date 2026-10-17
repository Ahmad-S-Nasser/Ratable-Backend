using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Anonymity;

public sealed class ReviewAnonymityTests
{
    private static object Body(string text, bool? anonymous = null) => new { answers = Answers(("security", 4)), text, isAnonymous = anonymous };

    private static async Task<JsonElement> PublicReviews(IdentityApiFactory api, string entityId) => (await Send(api, HttpMethod.Get, $"/api/entities/{entityId}/reviews")).Json;

    // ---- who is shown ----

    [MongoFact]
    public async Task A_named_review_shows_the_authors_public_name_and_an_anonymous_one_shows_nothing()
    {
        await using var api = new IdentityApiFactory();
        var sara = await api.SignInAsync("+201111111111");
        await SetProfile(api, sara.Cookie!, "Sara Ahmed", "https://cdn.example.com/sara.png");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var omar = await api.SignInAsync("+201222222222");
        await SetProfile(api, omar.Cookie!, "Omar Hassan");
        var entity = await SeedEntity(api);
        var named = await CreateReview(api, sara.Cookie!, entity.Id, Body("named", anonymous: false));
        var hidden = await CreateReview(api, omar.Cookie!, entity.Id, Body("anonymous", anonymous: true));

        var list = (await PublicReviews(api, entity.Id)).EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);

        Assert.False(list[named].GetProperty("anonymous").GetBoolean());
        Assert.Equal("Sara Ahmed", list[named].GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("https://cdn.example.com/sara.png", list[named].GetProperty("author").GetProperty("avatarUrl").GetString());
        Assert.True(list[hidden].GetProperty("anonymous").GetBoolean());
        Assert.Equal(JsonValueKind.Null, list[hidden].GetProperty("author").ValueKind);
        Assert.DoesNotContain("Omar", list[hidden].GetRawText());
    }

    [MongoFact]
    public async Task A_named_review_by_someone_without_a_public_name_is_shown_as_anonymous()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        await CreateReview(api, user.Cookie!, entity.Id, Body("named but no profile name", anonymous: false));

        var review = (await PublicReviews(api, entity.Id)).EnumerateArray().Single();

        Assert.True(review.GetProperty("anonymous").GetBoolean());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("author").ValueKind);
    }

    // ---- switching later ----

    [MongoFact]
    public async Task Switching_to_anonymous_hides_the_author_at_once_and_switching_back_shows_them_again()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        await SetProfile(api, user.Cookie!, "Sara Ahmed");
        var entity = await SeedEntity(api);
        var id = await CreateReview(api, user.Cookie!, entity.Id, Body("hello", anonymous: false));

        Assert.Equal("Sara Ahmed", (await PublicReviews(api, entity.Id)).EnumerateArray().Single().GetProperty("author").GetProperty("name").GetString());

        var (toAnonymous, own) = await Send(api, HttpMethod.Put, $"/api/reviews/{id}/anonymity", user.Cookie, new { isAnonymous = true });
        Assert.Equal(HttpStatusCode.OK, toAnonymous.StatusCode);
        Assert.True(own.GetProperty("isAnonymous").GetBoolean());
        var hidden = (await PublicReviews(api, entity.Id)).EnumerateArray().Single();
        Assert.True(hidden.GetProperty("anonymous").GetBoolean());
        Assert.DoesNotContain("Sara", hidden.GetRawText());

        await Send(api, HttpMethod.Put, $"/api/reviews/{id}/anonymity", user.Cookie, new { isAnonymous = false });
        Assert.Equal("Sara Ahmed", (await PublicReviews(api, entity.Id)).EnumerateArray().Single().GetProperty("author").GetProperty("name").GetString());
    }

    [MongoFact]
    public async Task Switching_changes_no_content_so_there_is_no_revision_no_moderation_and_the_edited_date_is_unchanged()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await CreateReview(api, user.Cookie!, entity.Id, Body("hello", anonymous: false));
        var before = (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!;
        api.Time.Advance(TimeSpan.FromDays(2));

        await Send(api, HttpMethod.Put, $"/api/reviews/{id}/anonymity", user.Cookie, new { isAnonymous = true });

        var after = (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!;
        Assert.Equal(ReviewStatus.Published, after.Status);
        Assert.Equal(before.RevisionNumber, after.RevisionNumber);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id));
    }

    [MongoFact]
    public async Task An_edit_request_that_only_changes_the_flag_is_treated_the_same_way()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api);
        var id = await CreateReview(api, user.Cookie!, entity.Id, Body("hello", anonymous: false));

        var (response, json) = await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Body("hello", anonymous: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(json.GetProperty("isAnonymous").GetBoolean());
        Assert.Equal(1, json.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("Published", json.GetProperty("status").GetString());
        Assert.Empty(await api.Services.GetRequiredService<ReviewRevisionRepository>().ListAsync(id));
    }

    [MongoFact]
    public async Task One_switch_applies_to_the_current_version_and_to_every_earlier_version_in_the_history()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        await SetProfile(api, user.Cookie!, "Sara Ahmed");
        var entity = await SeedEntity(api);
        var id = await CreateReview(api, user.Cookie!, entity.Id, Body("first version", anonymous: false));
        await Send(api, HttpMethod.Put, $"/api/reviews/{id}", user.Cookie, Body("second version"));
        await SetReviewState(api, id, ReviewStatus.Published);
        var named = (await Send(api, HttpMethod.Get, $"/api/reviews/{id}/history")).Json;
        Assert.Equal("Sara Ahmed", named.GetProperty("current").GetProperty("author").GetProperty("name").GetString());
        Assert.Single(named.GetProperty("earlierVersions").EnumerateArray());

        await Send(api, HttpMethod.Put, $"/api/reviews/{id}/anonymity", user.Cookie, new { isAnonymous = true });

        var hidden = (await Send(api, HttpMethod.Get, $"/api/reviews/{id}/history")).Json;
        Assert.DoesNotContain("Sara", hidden.GetRawText());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("current").GetProperty("author").ValueKind);
        foreach (var version in hidden.GetProperty("earlierVersions").EnumerateArray())
            Assert.False(version.TryGetProperty("author", out _)); // earlier versions have no author field at all
    }

    [MongoFact]
    public async Task Only_the_author_can_switch_and_the_value_is_required()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var other = await api.SignInAsync("+201222222222");
        var entity = await SeedEntity(api);
        var id = await CreateReview(api, user.Cookie!, entity.Id, Body("hello", anonymous: true));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Put, $"/api/reviews/{id}/anonymity", null, new { isAnonymous = false })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Put, $"/api/reviews/{id}/anonymity", other.Cookie, new { isAnonymous = false })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Put, $"/api/reviews/{id}/anonymity", user.Cookie, new { })).Response.StatusCode);
        Assert.True((await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!.IsAnonymous);
    }

    [MongoFact]
    public async Task The_real_author_is_stored_on_every_review_whatever_the_flag()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        var userId = user.Body.GetProperty("user").GetProperty("id").GetString();
        var entity = await SeedEntity(api);
        var id = await CreateReview(api, user.Cookie!, entity.Id, Body("hello", anonymous: true));

        Assert.Equal(userId, (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!.UserId);
    }
}
