using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Reviews;

public sealed class PlaceListTests
{
    private static async Task Seed(IdentityApiFactory api, string entityId, ReviewStatus status, VerificationLevel level, int score)
    {
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
        {
            EntityId = entityId, UserId = ObjectId.GenerateNewId().ToString(), CategoryId = "compounds", Status = status,
            Verification = new ReviewVerification { Level = level }, Answers = [new ReviewAnswer { CriterionId = "security", Score = score }],
        });
    }

    [MongoFact]
    public async Task Places_are_ranked_by_verified_score_and_unrated_places_come_last()
    {
        await using var api = new IdentityApiFactory();
        var low = await SeedEntity(api);
        var high = await SeedEntity(api);
        var unrated = await SeedEntity(api);
        await Seed(api, low.Id, ReviewStatus.Published, VerificationLevel.Verified, 3);
        await Seed(api, high.Id, ReviewStatus.Published, VerificationLevel.Verified, 5);
        // Unverified, pending and hidden reviews never count, however high they are.
        await Seed(api, unrated.Id, ReviewStatus.Published, VerificationLevel.Unverified, 5);
        await Seed(api, unrated.Id, ReviewStatus.PendingModeration, VerificationLevel.Verified, 5);
        await Seed(api, unrated.Id, ReviewStatus.Hidden, VerificationLevel.Verified, 5);

        var (response, json) = await Send(api, HttpMethod.Get, "/api/categories/compounds/places");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = json.GetProperty("places").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal([high.Id, low.Id, unrated.Id], ids);
        Assert.Equal(3, json.GetProperty("total").GetInt32());
        var last = json.GetProperty("places")[2];
        Assert.Equal(0, last.GetProperty("verifiedReviews").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, last.GetProperty("overall").ValueKind);
    }

    [MongoFact]
    public async Task An_unknown_category_is_not_found_and_pending_places_are_not_listed()
    {
        await using var api = new IdentityApiFactory();
        await SeedEntity(api, status: Ratable.Modules.Entities.Models.EntityStatus.PendingModeration);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/categories/nope/places")).Response.StatusCode);
        var (_, json) = await Send(api, HttpMethod.Get, "/api/categories/compounds/places");
        Assert.Empty(json.GetProperty("places").EnumerateArray());
    }
}
