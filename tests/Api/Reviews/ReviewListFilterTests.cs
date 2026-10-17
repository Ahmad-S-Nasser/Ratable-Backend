using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Reviews;

public sealed class ReviewListFilterTests
{
    private static async Task Seed(IdentityApiFactory api, string entityId, VerificationLevel level, int score, string text)
    {
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
        {
            EntityId = entityId,
            UserId = ObjectId.GenerateNewId().ToString(),
            CategoryId = "compounds",
            Status = ReviewStatus.Published,
            Text = text,
            Verification = new ReviewVerification { Level = level },
            Answers = [new ReviewAnswer { CriterionId = "security", Score = score }],
        });
    }

    private static async Task<List<string>> Texts(IdentityApiFactory api, string url) =>
        [.. (await Send(api, HttpMethod.Get, url)).Json.EnumerateArray().Select(r => r.GetProperty("text").GetString()!)];

    [MongoFact]
    public async Task Reviews_can_be_limited_to_verified_ones_and_sorted_by_their_own_score()
    {
        await using var api = new IdentityApiFactory();
        var entity = await SeedEntity(api);
        await Seed(api, entity.Id, VerificationLevel.Verified, 3, "mid");
        await Seed(api, entity.Id, VerificationLevel.Unverified, 5, "high-unverified");
        await Seed(api, entity.Id, VerificationLevel.Verified, 1, "low");

        Assert.Equal(["high-unverified", "mid", "low"], await Texts(api, $"/api/entities/{entity.Id}/reviews?sort=highest"));
        Assert.Equal(["low", "mid", "high-unverified"], await Texts(api, $"/api/entities/{entity.Id}/reviews?sort=lowest"));
        Assert.Equal(["mid", "low"], await Texts(api, $"/api/entities/{entity.Id}/reviews?verified=true&sort=highest"));
        Assert.Equal(3, (await Texts(api, $"/api/entities/{entity.Id}/reviews")).Count);
    }
}
