using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Moderation.Services;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Content;
using Ratable.Shared.Data;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Moderation;

/// <summary>Shared set-up for the moderation tests: a fake AI that answers the pre-screen, accounts with a verified phone, and cases to look at.</summary>
public static class ModerationKit
{
    public const string AdminPhone = "+201000000001";

    public static IdentityApiFactory NewApi(Dictionary<string, string>? extra = null)
    {
        var settings = new Dictionary<string, string> { ["Identity:BootstrapAdminPhone"] = AdminPhone };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new IdentityApiFactory(settings, fakeAi: true);
    }

    public static PrescreenAnswer Clean() => new(new(false, 0.02), new(false, 0.01), new(false, 0.03), new(false, 0.04), new(false, 0.0));

    public static PrescreenAnswer Flagged(double abuse = 0, double personalData = 0, double spam = 0, double fake = 0, double identifying = 0) =>
        new(new(abuse > 0, abuse), new(personalData > 0, personalData), new(spam > 0, spam), new(fake > 0, fake), new(identifying > 0, identifying));

    public static string IdOf(IdentityApiFactory.SignedIn s) => s.Body.GetProperty("user").GetProperty("id").GetString()!;

    /// <summary>Gives an account a verified phone. Only the WhatsApp verifier (P9h) will do this in the product; tests set it directly.</summary>
    public static async Task VerifyPhone(IdentityApiFactory api, IdentityApiFactory.SignedIn user)
    {
        var users = api.Services.GetRequiredService<UserRepository>();
        var u = (await users.ListByIdsAsync([IdOf(user)])).Single();
        u.PhoneVerified = true;
        await users.ReplaceAsync(u);
    }

    public static async Task SetRatesIndividuals(IdentityApiFactory api, string categoryId, bool value)
    {
        var repo = api.Services.GetRequiredService<CategoryRepository>();
        var c = (await repo.GetAsync(categoryId))!;
        c.RatesIndividuals = value;
        await repo.ReplaceAsync(c);
    }

    public static ModerationCaseRepository Cases(IdentityApiFactory api) => api.Services.GetRequiredService<ModerationCaseRepository>();

    public static async Task<List<ModerationCase>> AllCases(IdentityApiFactory api) =>
        await api.Database.GetCollection<ModerationCase>(Collections.ModerationCases).Find(FilterDefinition<ModerationCase>.Empty).SortBy(c => c.Id).ToListAsync();

    public static async Task<Review> ReviewOf(IdentityApiFactory api, string id) => (await api.Services.GetRequiredService<ReviewRepository>().GetAsync(id))!;

    /// <summary>Creates a review through the API. The fake AI answers its pre-screen with <paramref name="answer"/> (null: nothing queued, so the pre-screen fails).</summary>
    public static async Task<string> NewReview(
        IdentityApiFactory api, IdentityApiFactory.SignedIn author, string entityId, PrescreenAnswer? answer, string? text = null,
        string criterion = "security", bool? anonymous = null)
    {
        text ??= $"A review with enough words in it to read {Guid.NewGuid():N}"; // unique, so tests do not trip the copied-text signal by accident
        if (answer is not null) api.Ai.Enqueue(answer);
        var (response, json) = await Send(api, HttpMethod.Post, $"/api/entities/{entityId}/reviews", author.Cookie, new { answers = Answers((criterion, 4)), text, isAnonymous = anonymous });
        Xunit.Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return json.GetProperty("id").GetString()!;
    }

    public static async Task<ModerationCase> CaseFor(IdentityApiFactory api, string contentId) =>
        (await AllCases(api)).Where(c => c.ContentId == contentId && c.State != CaseState.Superseded).OrderBy(c => c.RevisionNumber).Last();
}
