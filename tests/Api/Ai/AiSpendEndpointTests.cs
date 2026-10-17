using Ratable.Shared.Ai;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Ai;
using Ratable.Modules.Ai.Models;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Ai;

public sealed class AiSpendEndpointTests
{
    private const string AdminPhone = "+201000000001";

    private static IdentityApiFactory Factory() =>
        new(new() { ["Identity:BootstrapAdminPhone"] = AdminPhone, ["Ai:DailyCapUsd"] = "1.00" });

    [Fact]
    public void The_default_configuration_prices_every_default_model_and_has_a_daily_cap()
    {
        using var api = new IdentityApiFactory();
        var ai = api.Services.GetRequiredService<IOptions<AiOptions>>().Value;

        Assert.True(ai.DailyCapUsd > 0m);
        foreach (var tier in new[] { "Small", "Standard" })
        {
            var model = ai.Models[tier];
            Assert.True(ai.Prices.TryGetValue(model, out var price), $"{model} has no price");
            Assert.True(price!.InputPerMillionUsd > 0m && price.OutputPerMillionUsd > 0m);
        }
    }

    [Fact]
    public void The_real_client_and_a_provider_are_registered_in_the_api()
    {
        using var api = new IdentityApiFactory();

        Assert.IsType<AiClient>(api.Services.GetRequiredService<IAiClient>());
        Assert.Same(api.Services.GetRequiredService<IAiClient>(), api.Services.GetRequiredService<IAiClient>());
        Assert.Equal("none", api.Services.GetRequiredService<IAiProvider>().Name);
    }

    [MongoFact]
    public async Task Only_an_admin_can_see_the_spend()
    {
        await using var api = Factory();
        var user = await api.SignInAsync("+201111111111");
        using var http = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/admin/ai/spend")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/admin/ai/spend", user.Cookie!))).StatusCode);
    }

    [MongoFact]
    public async Task The_admin_sees_spend_today_and_this_month_by_feature_without_any_content()
    {
        await using var api = Factory();
        var admin = await api.SignInAsync(AdminPhone);
        var now = api.Time.GetUtcNow().UtcDateTime;
        var calls = api.Database.GetCollection<AiCall>(Collections.AiCalls);
        AiCall Call(string feature, decimal cost, AiCallStatus status, DateTime at) => new()
        {
            At = at,
            Feature = feature,
            ItemReference = "x",
            Provider = "fake",
            Model = "m",
            Tier = "Small",
            InputTokens = 1000,
            OutputTokens = 100,
            CostUsd = cost,
            Status = status,
        };
        await calls.InsertManyAsync(
        [
            Call("moderation", 0.25m, AiCallStatus.Completed, now),
            Call("moderation", 0.25m, AiCallStatus.Completed, now),
            Call("proof_check", 0.10m, AiCallStatus.Completed, now),
            Call("moderation", 0m, AiCallStatus.Deferred, now),
            Call("moderation", 9m, AiCallStatus.Completed, now.AddDays(-40)), // an earlier month: in neither total
        ]);
        using var http = api.CreateClient();

        var response = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/admin/ai/spend", admin.Cookie!));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1.00m, body.GetProperty("dailyCapUsd").GetDecimal());
        var today = body.GetProperty("today");
        Assert.Equal(0.60m, today.GetProperty("costUsd").GetDecimal());
        Assert.Equal(4, today.GetProperty("calls").GetInt32());
        Assert.Equal(1, today.GetProperty("deferred").GetInt32());
        Assert.Equal(0.40m, body.GetProperty("remainingTodayUsd").GetDecimal());
        var byFeature = today.GetProperty("byFeature").EnumerateArray().ToList();
        Assert.Equal("moderation", byFeature[0].GetProperty("feature").GetString());
        Assert.Equal(0.50m, byFeature[0].GetProperty("costUsd").GetDecimal());
        Assert.Equal("proof_check", byFeature[1].GetProperty("feature").GetString());
        Assert.Equal(0.60m, body.GetProperty("thisMonth").GetProperty("costUsd").GetDecimal());

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("prompt", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("itemReference", text);
    }

    [MongoFact]
    public async Task With_nothing_spent_the_admin_sees_zeros_and_the_whole_cap_left()
    {
        await using var api = Factory();
        var admin = await api.SignInAsync(AdminPhone);
        using var http = api.CreateClient();

        var body = await (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/admin/ai/spend", admin.Cookie!)))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0m, body.GetProperty("today").GetProperty("costUsd").GetDecimal());
        Assert.Equal(1.00m, body.GetProperty("remainingTodayUsd").GetDecimal());
        Assert.Empty(body.GetProperty("thisMonth").GetProperty("byFeature").EnumerateArray());
    }
}
