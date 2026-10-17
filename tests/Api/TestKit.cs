using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Entities.Services;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests;

/// <summary>Small helpers shared by the API tests: send a request, seed an entity, move a review to a state.</summary>
public static class TestKit
{
    public static async Task<(HttpResponseMessage Response, JsonElement Json)> Send(
        IdentityApiFactory api, HttpMethod method, string url, string? cookie = null, object? body = null)
    {
        using var http = api.CreateClient();
        var request = cookie is null ? new HttpRequestMessage(method, url) : IdentityApiFactory.WithCookie(method, url, cookie, body);
        if (cookie is null && body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length > 0 && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) ? JsonSerializer.Deserialize<JsonElement>(text) : default;
        return (response, json);
    }

    public static object Answers(params (string Id, object Value)[] answers) => answers.Select(a => new { criterionId = a.Id, value = a.Value }).ToArray();

    public static async Task<Entity> SeedEntity(IdentityApiFactory api, string category = "compounds", EntityStatus status = EntityStatus.Active)
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

    public static async Task SetReviewState(IdentityApiFactory api, string reviewId, ReviewStatus? status = null, VerificationLevel? level = null)
    {
        var repo = api.Services.GetRequiredService<ReviewRepository>();
        var review = (await repo.GetAsync(reviewId))!;
        if (status is { } s) review.Status = s;
        if (level is { } l) review.Verification = new ReviewVerification { Level = l, Method = l == VerificationLevel.Verified ? "receipt" : null };
        await repo.ReplaceAsync(review);
    }

    /// <summary>Creates a review through the API as the signed-in user and returns its id.</summary>
    public static async Task<string> CreateReview(IdentityApiFactory api, string cookie, string entityId, object? body = null, ReviewStatus? state = ReviewStatus.Published)
    {
        var (response, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entityId}/reviews", cookie,
            body ?? new { answers = Answers(("security", 4)), text = "review text" });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var id = json.GetProperty("id").GetString()!;
        if (state is { } s) await SetReviewState(api, id, s);
        return id;
    }

    public static string NewId() => ObjectId.GenerateNewId().ToString();

    public static async Task SetProfile(IdentityApiFactory api, string cookie, string? name, string? avatar = null)
    {
        var (response, _) = await Send(api, HttpMethod.Put, "/api/me/profile", cookie, new { displayName = name, avatarUrl = avatar });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
