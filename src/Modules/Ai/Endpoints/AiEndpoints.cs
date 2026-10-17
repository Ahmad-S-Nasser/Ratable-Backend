using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ratable.Modules.Ai.Data;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Ai.Endpoints;

public sealed record FeatureSpendDto(string Feature, decimal CostUsd, int Calls);
public sealed record SpendPeriod(decimal CostUsd, int Calls, int Deferred, int Failed, int CacheHits, long InputTokens, long OutputTokens, List<FeatureSpendDto> ByFeature);
public sealed record SpendResponse(DateTime AsOf, decimal DailyCapUsd, decimal RemainingTodayUsd, SpendPeriod Today, SpendPeriod ThisMonth);

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/ai/spend", async (AiCallRepository calls, IOptions<AiOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var dayStart = now.Date;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var today = await Period(calls, dayStart, dayStart.AddDays(1), ct);
            var month = await Period(calls, monthStart, monthStart.AddMonths(1), ct);
            var cap = options.Value.DailyCapUsd;
            return new SpendResponse(now, cap, Math.Max(0m, cap - today.CostUsd), today, month);
        })
        .WithTags("Admin")
        .RequireAuthorization(PolicyNames.Admin);
        return app;
    }

    private static async Task<SpendPeriod> Period(AiCallRepository calls, DateTime from, DateTime to, CancellationToken ct)
    {
        var s = await calls.SummaryAsync(from, to, ct);
        var byFeature = await calls.ByFeatureAsync(from, to, ct);
        return new SpendPeriod(s.CostUsd, s.Calls, s.Deferred, s.Failed, s.CacheHits, s.InputTokens, s.OutputTokens,
            [.. byFeature.Select(f => new FeatureSpendDto(f.Feature, f.CostUsd, f.Calls))]);
    }
}
