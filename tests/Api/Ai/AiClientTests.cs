using Ratable.Shared.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Ai;
using Ratable.Modules.Ai.Data;
using Ratable.Modules.Ai.Models;
using Ratable.Modules.Ai.Providers;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Ai;

/// <summary>An AiClient on its own throwaway database with a scripted provider and a fake clock. No model is ever called.</summary>
public sealed class AiRig : IAsyncDisposable
{
    public FakeAiProvider Provider { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    public AiOptions Options { get; }
    public IMongoDatabase Db { get; }
    public AiCallRepository Calls { get; }
    public AiClient Client { get; }

    public AiRig(decimal cap = 1m)
    {
        MongoConventions.Register();
        Options = new AiOptions
        {
            DailyCapUsd = cap,
            Models = new() { ["Small"] = "test-small", ["Standard"] = "test-standard" },
            Effort = new() { ["Small"] = "low", ["Standard"] = "medium" },
            // $1 and $5 per million tokens for the small model; $2 and $10 for the standard one.
            Prices = new()
            {
                ["test-small"] = new ModelPrice { InputPerMillionUsd = 1m, OutputPerMillionUsd = 5m },
                ["test-standard"] = new ModelPrice { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m },
            },
        };
        Db = new MongoClient(MongoFixture.ConnectionString).GetDatabase($"ratable_tests_{Guid.NewGuid():N}");
        Calls = new AiCallRepository(Db);
        Client = new AiClient(Provider, Calls, new AiCacheRepository(Db), Microsoft.Extensions.Options.Options.Create(Options), Time, NullLogger<AiClient>.Instance);
    }

    public static AiRequest Request(string prompt = "hello", AiTier tier = AiTier.Small, int maxOut = 100, bool cacheable = false, string feature = "moderation", string item = "review-1") =>
        new() { Feature = feature, ItemReference = item, Prompt = prompt, Tier = tier, MaxOutputTokens = maxOut, Cacheable = cacheable };

    public Task<List<AiCall>> AllCalls() => Db.GetCollection<AiCall>(Collections.AiCalls).Find(FilterDefinition<AiCall>.Empty).SortBy(c => c.At).ToListAsync();

    public async ValueTask DisposeAsync() => await Db.Client.DropDatabaseAsync(Db.DatabaseNamespace.DatabaseName);
}

public sealed class AiClientTests
{
    // 1,000 input and 200 output tokens on the small model: (1000 * 1 + 200 * 5) / 1,000,000 = $0.002
    private const decimal OneCall = 0.002m;

    // ---- logging: model, tokens, cost ----

    [MongoFact]
    public async Task A_call_is_logged_with_model_tokens_cost_feature_and_item()
    {
        await using var rig = new AiRig();

        var result = await rig.Client.CompleteTextAsync(AiRig.Request(feature: "moderation", item: "review-42"));

        Assert.Equal(AiStatus.Completed, result.Status);
        Assert.Equal("ok", result.Value);
        Assert.Equal(new AiUsage("test-small", 1000, 200, OneCall), result.Usage);
        var log = Assert.Single(await rig.AllCalls());
        Assert.Equal("moderation", log.Feature);
        Assert.Equal("review-42", log.ItemReference);
        Assert.Equal("test-small", log.Model);
        Assert.Equal("fake", log.Provider);
        Assert.Equal(1000, log.InputTokens);
        Assert.Equal(200, log.OutputTokens);
        Assert.Equal(OneCall, log.CostUsd);
        Assert.Equal(AiCallStatus.Completed, log.Status);
    }

    [MongoFact]
    public async Task The_log_never_contains_the_prompt_or_the_answer()
    {
        await using var rig = new AiRig();
        rig.Provider.Default = new AiProviderResponse("the secret answer about Sara Ahmed", 10, 10, false);

        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "Review text by Sara Ahmed, phone 01012345678"));

        var raw = (await rig.Db.GetCollection<BsonDocument>(Collections.AiCalls).Find(new BsonDocument()).SingleAsync()).ToJson();
        Assert.DoesNotContain("Sara", raw);
        Assert.DoesNotContain("01012345678", raw);
        Assert.DoesNotContain("secret answer", raw);
    }

    [MongoFact]
    public async Task Each_tier_uses_its_own_model_and_price_and_effort()
    {
        await using var rig = new AiRig();

        var small = await rig.Client.CompleteTextAsync(AiRig.Request(tier: AiTier.Small));
        var standard = await rig.Client.CompleteTextAsync(AiRig.Request(tier: AiTier.Standard));

        Assert.Equal(0.002m, small.Usage!.CostUsd);
        Assert.Equal(0.004m, standard.Usage!.CostUsd); // (1000 * 2 + 200 * 10) / 1,000,000
        Assert.Equal(["test-small", "test-standard"], rig.Provider.Requests.Select(r => r.Model));
        Assert.Equal(["low", "medium"], rig.Provider.Requests.Select(r => r.Effort));
    }

    // ---- the daily cap ----

    [MongoFact]
    public async Task When_the_cap_is_reached_the_result_is_deferred_and_the_model_is_not_called()
    {
        await using var rig = new AiRig(cap: 0.005m);
        await rig.Client.CompleteTextAsync(AiRig.Request(item: "r1"));
        await rig.Client.CompleteTextAsync(AiRig.Request(item: "r2"));
        await rig.Client.CompleteTextAsync(AiRig.Request(item: "r3")); // spent 0.004, this one still fits (worst case 0.0005) and takes it to 0.006
        Assert.Equal(3, rig.Provider.Calls);

        var fourth = await rig.Client.CompleteTextAsync(AiRig.Request(item: "r4"));

        Assert.Equal(AiStatus.Deferred, fourth.Status);
        Assert.True(fourth.IsDeferred);
        Assert.Null(fourth.Value);
        Assert.Equal(3, rig.Provider.Calls);
        var deferred = (await rig.AllCalls()).Last();
        Assert.Equal(AiCallStatus.Deferred, deferred.Status);
        Assert.Equal("daily_cap", deferred.Error);
        Assert.Equal("r4", deferred.ItemReference);
        Assert.Equal(0m, deferred.CostUsd);
    }

    [MongoFact]
    public async Task A_call_that_could_cost_more_than_what_is_left_is_deferred_up_front()
    {
        await using var rig = new AiRig(cap: 0.001m);

        // 1,000 output tokens at $5 per million is up to $0.005, more than the whole cap.
        var result = await rig.Client.CompleteTextAsync(AiRig.Request(maxOut: 1000));

        Assert.True(result.IsDeferred);
        Assert.Equal(0, rig.Provider.Calls);
    }

    [MongoFact]
    public async Task The_cap_resets_on_the_next_utc_day()
    {
        await using var rig = new AiRig(cap: 0.003m);
        await rig.Client.CompleteTextAsync(AiRig.Request());
        await rig.Client.CompleteTextAsync(AiRig.Request());
        Assert.True((await rig.Client.CompleteTextAsync(AiRig.Request())).IsDeferred);

        rig.Time.Advance(TimeSpan.FromHours(12)); // 2026-10-18 00:00 UTC
        var tomorrow = await rig.Client.CompleteTextAsync(AiRig.Request());

        Assert.Equal(AiStatus.Completed, tomorrow.Status);
    }

    [MongoFact]
    public async Task Parallel_calls_cannot_overshoot_the_cap()
    {
        await using var rig = new AiRig(cap: 0.01m);
        // Realistic: the real cost (about $0.0005) is below the worst case that was reserved ($0.001).
        rig.Provider.Default = new AiProviderResponse("ok", 10, 100, false);
        rig.Provider.Delay = TimeSpan.FromMilliseconds(50);

        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            rig.Client.CompleteTextAsync(AiRig.Request(prompt: "hi", maxOut: 200, item: $"r{i}"))));

        var spent = await rig.Calls.SumCostAsync(new DateTime(2026, 10, 17), new DateTime(2026, 10, 18));
        Assert.True(spent <= 0.01m, $"spent {spent}");
        Assert.Contains(results, r => r.IsDeferred);
        Assert.True(results.Count(r => r.Completed) >= 5);
        Assert.Equal(results.Count(r => r.Completed), rig.Provider.Calls);
    }

    [MongoFact]
    public async Task A_deferred_result_is_never_a_completed_one()
    {
        await using var rig = new AiRig(cap: 0m);

        var result = await rig.Client.CompleteTextAsync(AiRig.Request());

        Assert.False(result.Completed);
        Assert.True(result.IsDeferred);
    }

    // ---- failures never throw and are never approvals ----

    [MongoFact]
    public async Task A_model_without_a_price_is_refused_so_spend_is_never_untracked()
    {
        await using var rig = new AiRig();
        rig.Options.Prices.Remove("test-small");

        var result = await rig.Client.CompleteTextAsync(AiRig.Request());

        Assert.Equal(AiStatus.Failed, result.Status);
        Assert.Equal("model_not_priced", result.Error);
        Assert.Equal(0, rig.Provider.Calls);
        Assert.Equal("model_not_priced", Assert.Single(await rig.AllCalls()).Error);
    }

    [MongoFact]
    public async Task A_provider_error_is_a_failed_result_and_does_not_block_later_calls()
    {
        await using var rig = new AiRig(cap: 0.0015m);
        rig.Provider.Throw = new AiProviderException("anthropic_http_529");

        var failed = await rig.Client.CompleteTextAsync(AiRig.Request(maxOut: 200));
        rig.Provider.Throw = null;
        var later = await rig.Client.CompleteTextAsync(AiRig.Request(maxOut: 200)); // would be deferred if the failed call kept its reservation

        Assert.Equal(AiStatus.Failed, failed.Status);
        Assert.Equal("provider_error", failed.Error);
        Assert.Equal(AiStatus.Completed, later.Status);
        Assert.Equal(AiCallStatus.Failed, (await rig.AllCalls()).First().Status);
    }

    [MongoFact]
    public async Task With_no_provider_configured_every_call_fails_visibly()
    {
        await using var rig = new AiRig();
        var client = new AiClient(new NoAiProvider(), rig.Calls, new AiCacheRepository(rig.Db),
            Microsoft.Extensions.Options.Options.Create(rig.Options), rig.Time, NullLogger<AiClient>.Instance);

        var result = await client.CompleteTextAsync(AiRig.Request());

        Assert.Equal(AiStatus.Failed, result.Status);
        Assert.Equal("provider_not_configured", result.Error);
    }

    // ---- structured output ----

    public sealed record Verdict(string Decision, double Confidence);

    [MongoFact]
    public async Task Structured_output_is_parsed_into_the_type_and_the_schema_is_sent_to_the_model()
    {
        await using var rig = new AiRig();
        rig.Provider.Enqueue(new AiProviderResponse("""{"decision":"approve","confidence":0.93}""", 500, 30, false));

        var result = await rig.Client.CompleteStructuredAsync<Verdict>(AiRig.Request());

        Assert.True(result.Completed);
        Assert.Equal(new Verdict("approve", 0.93), result.Value);
        var system = rig.Provider.Requests.Single().System!;
        Assert.Contains("JSON Schema", system);
        Assert.Contains("decision", system);
        Assert.Contains("confidence", system);
    }

    [MongoFact]
    public async Task Structured_output_wrapped_in_code_fences_is_still_read()
    {
        await using var rig = new AiRig();
        rig.Provider.Enqueue(new AiProviderResponse("```json\n{\"decision\":\"flag\",\"confidence\":0.5}\n```", 500, 30, false));

        var result = await rig.Client.CompleteStructuredAsync<Verdict>(AiRig.Request());

        Assert.Equal("flag", result.Value!.Decision);
    }

    [MongoFact]
    public async Task Invalid_structured_output_is_failed_but_its_cost_is_still_counted()
    {
        await using var rig = new AiRig();
        rig.Provider.Enqueue(new AiProviderResponse("I think this review is fine.", 500, 30, false));

        var result = await rig.Client.CompleteStructuredAsync<Verdict>(AiRig.Request());

        Assert.Equal(AiStatus.Failed, result.Status);
        Assert.Equal("invalid_output", result.Error);
        var log = Assert.Single(await rig.AllCalls());
        Assert.Equal(AiCallStatus.Failed, log.Status);
        Assert.True(log.CostUsd > 0m);
    }

    [MongoFact]
    public async Task An_answer_cut_off_by_the_token_limit_is_failed_not_trusted()
    {
        await using var rig = new AiRig();
        rig.Provider.Enqueue(new AiProviderResponse("""{"decision":"appro""", 500, 100, true));

        var result = await rig.Client.CompleteStructuredAsync<Verdict>(AiRig.Request());

        Assert.Equal(AiStatus.Failed, result.Status);
        Assert.Equal("truncated", result.Error);
        Assert.True((await rig.AllCalls()).Single().CostUsd > 0m);
    }

    // ---- cache of identical requests ----

    [MongoFact]
    public async Task An_identical_cacheable_request_is_answered_from_the_cache_at_no_cost()
    {
        await using var rig = new AiRig();
        var first = await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "same", cacheable: true));
        var second = await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "same", cacheable: true));

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal("ok", second.Value);
        Assert.Equal(0m, second.Usage!.CostUsd);
        Assert.Equal(1, rig.Provider.Calls);
        var log = await rig.AllCalls();
        Assert.Equal([AiCallStatus.Completed, AiCallStatus.CacheHit], log.Select(c => c.Status));
        Assert.Equal(OneCall, log.Sum(c => c.CostUsd));
    }

    [MongoFact]
    public async Task Requests_that_differ_or_are_not_cacheable_always_call_the_model()
    {
        await using var rig = new AiRig();
        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "a", cacheable: true));
        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "b", cacheable: true));
        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "a", cacheable: true, tier: AiTier.Standard));
        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "c", cacheable: false));
        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "c", cacheable: false));

        Assert.Equal(5, rig.Provider.Calls);
    }

    [MongoFact]
    public async Task A_cache_hit_is_served_even_when_the_cap_is_reached()
    {
        await using var rig = new AiRig(cap: 0.003m);
        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "same", cacheable: true));
        await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "other"));
        Assert.True((await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "new"))).IsDeferred);

        var cached = await rig.Client.CompleteTextAsync(AiRig.Request(prompt: "same", cacheable: true));

        Assert.True(cached.Completed);
        Assert.True(cached.FromCache);
    }

    // ---- the fake used by every other module's tests ----

    [Fact]
    public async Task The_fake_client_returns_scripted_answers_records_requests_and_can_simulate_the_cap()
    {
        var fake = new FakeAiClient().Enqueue(new Verdict("reject", 0.99));

        var ok = await fake.CompleteStructuredAsync<Verdict>(AiRig.Request(item: "review-9"));
        fake.DeferAll = true;
        var deferred = await fake.CompleteStructuredAsync<Verdict>(AiRig.Request());

        Assert.Equal("reject", ok.Value!.Decision);
        Assert.True(deferred.IsDeferred);
        Assert.Null(deferred.Value);
        Assert.Equal("review-9", fake.Requests.First().ItemReference);
        Assert.Equal(2, fake.Requests.Count);
    }
}
