using Ratable.Shared.Ai;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Ai.Data;
using Ratable.Modules.Ai.Models;

namespace Ratable.Modules.Ai;

/// <summary>
/// The real <see cref="IAiClient"/>. For each call it: checks the model is priced, checks the daily cap (and reserves the worst-case
/// cost so a burst of parallel calls cannot overshoot it), calls the provider, works out the real cost, and logs the call.
/// </summary>
public sealed class AiClient(
    IAiProvider provider,
    AiCallRepository calls,
    AiCacheRepository cache,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<AiClient> logger) : IAiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private readonly Lock _gate = new();
    private decimal _reserved;

    private AiOptions Opt => options.Value;

    public Task<AiResult<string>> CompleteTextAsync(AiRequest request, CancellationToken ct = default) =>
        RunAsync<string>(request, schema: null, text => (text, null), ct);

    public Task<AiResult<T>> CompleteStructuredAsync<T>(AiRequest request, CancellationToken ct = default)
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(Json, typeof(T)).ToJsonString();
        return RunAsync<T>(request, schema, text =>
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(StripFences(text), Json);
                return value is null ? (default, "invalid_output") : (value, null);
            }
            catch (JsonException)
            {
                return (default, "invalid_output");
            }
        }, ct);
    }

    private async Task<AiResult<T>> RunAsync<T>(AiRequest request, string? schema, Func<string, (T? Value, string? Error)> parse, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var now = time.GetUtcNow().UtcDateTime;
        var tier = request.Tier.ToString();
        var model = Opt.Models.GetValueOrDefault(tier) ?? "";
        var maxOut = Math.Clamp(request.MaxOutputTokens, 1, Opt.MaxOutputTokensLimit);

        AiResult<T> Finish(AiCallStatus status, string? error, AiUsage? usage = null, T? value = default, bool fromCache = false)
        {
            var result = new AiResult<T>(
                status switch { AiCallStatus.Completed or AiCallStatus.CacheHit => AiStatus.Completed, AiCallStatus.Deferred => AiStatus.Deferred, _ => AiStatus.Failed },
                value, usage, error, fromCache);
            return result;
        }

        async Task LogAsync(AiCallStatus status, string? error, long inTokens = 0, long outTokens = 0, decimal cost = 0m)
        {
            await calls.AppendAsync(new AiCall
            {
                At = now,
                Feature = request.Feature,
                ItemReference = request.ItemReference,
                Provider = provider.Name,
                Model = model,
                Tier = tier,
                InputTokens = inTokens,
                OutputTokens = outTokens,
                CostUsd = cost,
                Status = status,
                DurationMs = (long)time.GetElapsedTime(started).TotalMilliseconds,
                Error = error,
            }, CancellationToken.None);
        }

        if (string.IsNullOrWhiteSpace(model) || !Opt.Prices.TryGetValue(model, out var price))
        {
            // Never call a model whose price we do not know: that would be spend we cannot count.
            await LogAsync(AiCallStatus.Failed, "model_not_priced");
            return Finish(AiCallStatus.Failed, "model_not_priced");
        }

        // A request with an attachment is a private document: it is never cached.
        var cacheKey = request.Cacheable && request.Attachments is not { Count: > 0 } ? CacheKey(request, model, schema) : null;
        if (cacheKey is not null && await cache.GetAsync(cacheKey, now, ct) is { } hit)
        {
            var (cachedValue, cachedError) = parse(hit.Text);
            if (cachedError is null)
            {
                await LogAsync(AiCallStatus.CacheHit, null);
                return Finish(AiCallStatus.CacheHit, null, new AiUsage(model, 0, 0, 0m), cachedValue, fromCache: true);
            }
        }

        // Worst case for this call: all input and the full output allowance at the model's prices. Input is estimated at 1 token per 3 characters.
        var worstCase = (EstimateInputTokens(request) * price.InputPerMillionUsd + maxOut * price.OutputPerMillionUsd) / 1_000_000m;
        var dayStart = now.Date;
        var spentToday = await calls.SumCostAsync(dayStart, dayStart.AddDays(1), ct);
        if (!TryReserve(spentToday, worstCase))
        {
            await LogAsync(AiCallStatus.Deferred, "daily_cap");
            return Finish(AiCallStatus.Deferred, "daily_cap");
        }

        try
        {
            AiProviderResponse response;
            try
            {
                var effort = Opt.Effort.GetValueOrDefault(tier) ?? "low";
                var system = schema is null ? request.System : JoinSystem(request.System, schema);
                response = await provider.CompleteAsync(new AiProviderRequest(model, system, request.Prompt, maxOut, effort, request.Attachments), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The message is logged; exceptions from providers must not carry request content (see AiProviderException).
                logger.LogWarning("AI call failed for {Feature} {Item}: {Reason}", request.Feature, request.ItemReference, ex is AiProviderException ? ex.Message : ex.GetType().Name);
                var code = ex is AiProviderException { Message: "provider_not_configured" } ? "provider_not_configured" : "provider_error";
                await LogAsync(AiCallStatus.Failed, code);
                return Finish(AiCallStatus.Failed, code);
            }

            var cost = (response.InputTokens * price.InputPerMillionUsd + response.OutputTokens * price.OutputPerMillionUsd) / 1_000_000m;
            var usage = new AiUsage(model, response.InputTokens, response.OutputTokens, cost);

            if (response.Truncated)
            {
                await LogAsync(AiCallStatus.Failed, "truncated", response.InputTokens, response.OutputTokens, cost);
                return Finish(AiCallStatus.Failed, "truncated", usage);
            }

            var (value, parseError) = parse(response.Text);
            if (parseError is not null)
            {
                await LogAsync(AiCallStatus.Failed, parseError, response.InputTokens, response.OutputTokens, cost);
                return Finish(AiCallStatus.Failed, parseError, usage);
            }

            await LogAsync(AiCallStatus.Completed, null, response.InputTokens, response.OutputTokens, cost);
            if (cacheKey is not null)
                await cache.PutAsync(new AiCacheEntry { Key = cacheKey, Text = response.Text, CreatedAt = now, ExpiresAt = now.AddDays(Opt.CacheDays) }, CancellationToken.None);
            return Finish(AiCallStatus.Completed, null, usage, value);
        }
        finally
        {
            Release(worstCase);
        }
    }

    private bool TryReserve(decimal spentToday, decimal worstCase)
    {
        lock (_gate)
        {
            if (spentToday >= Opt.DailyCapUsd || spentToday + _reserved + worstCase > Opt.DailyCapUsd) return false;
            _reserved += worstCase;
            return true;
        }
    }

    private void Release(decimal amount)
    {
        lock (_gate) _reserved = Math.Max(0m, _reserved - amount);
    }

    // An attachment costs more than its text: reserve a generous fixed amount per document or image for the worst case.
    private const long TokensPerAttachment = 4000;

    private static long EstimateInputTokens(AiRequest r) =>
        ((r.Prompt.Length + (r.System?.Length ?? 0)) / 3) + 1 + ((r.Attachments?.Count ?? 0) * TokensPerAttachment);

    private static string CacheKey(AiRequest r, string model, string? schema)
    {
        var raw = string.Join('\u001f', r.Feature, model, r.System ?? "", r.Prompt, schema ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string JoinSystem(string? system, string schema) =>
        (string.IsNullOrWhiteSpace(system) ? "" : system + "\n\n") +
        "Reply with a single JSON value that matches this JSON Schema, and nothing else: no explanation and no code fences.\n" + schema;

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstNewline = t.IndexOf('\n');
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline ? t[(firstNewline + 1)..lastFence].Trim() : t;
    }
}
