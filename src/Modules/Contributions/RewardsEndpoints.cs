using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ratable.Modules.Contributions.Data;
using Ratable.Shared.Audit;
using Ratable.Shared.Auth;
using Ratable.Shared.Data;

namespace Ratable.Modules.Contributions;

public sealed record ErrorBody(string Error);
public sealed record SeenBody(string? RankId);
public sealed record ReverseBody(string? Reason);

/// <summary>What the "How points work" page shows. It is the configuration itself, so the page can never disagree with what is actually paid.</summary>
public sealed record RewardsConfigView(
    Dictionary<string, int> Points, Dictionary<string, int> DailyCaps, int MinWrittenLength, List<RankDefinition> Ranks, List<BadgeDefinition> Badges, List<MissionDefinition> Missions);

public static class RewardsEndpoints
{
    public static IEndpointRouteBuilder MapRewardsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me/rewards", async (ClaimsPrincipal user, RewardsService service, CancellationToken ct) => Results.Ok(await service.GetAsync(UserId(user), ct)))
            .WithTags("Rewards").RequireAuthorization(PolicyNames.SignedIn).Produces<RewardsService.RewardsView>();

        app.MapPost("/api/me/rewards/seen", async (SeenBody body, ClaimsPrincipal user, RewardsService service, IOptions<RewardsOptions> options, CancellationToken ct) =>
            {
                if (body.RankId is null || options.Value.Ranks.All(r => r.Id != body.RankId)) return Results.BadRequest(new ErrorBody("invalid_rank"));
                await service.AcknowledgeRankAsync(UserId(user), body.RankId, ct);
                return Results.NoContent();
            })
            .WithTags("Rewards").RequireAuthorization(PolicyNames.SignedIn).Produces(204).Produces<ErrorBody>(400);

        app.MapGet("/api/rewards/config", (IOptions<RewardsOptions> options) =>
            {
                var o = options.Value;
                return Results.Ok(new RewardsConfigView(o.Points, o.DailyCaps, o.MinWrittenLength, [.. o.Ranks.OrderBy(r => r.MinPoints)], o.Badges, o.Missions));
            })
            .WithTags("Rewards").Produces<RewardsConfigView>();

        // Every point can be audited and reversed: an admin reverses one entry with a reason, which is logged.
        app.MapPost("/api/admin/contributions/{id}/reverse", async (string id, ReverseBody body, ClaimsPrincipal user, ContributionRepository ledger, IAuditLog audit, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 300) return Results.BadRequest(new ErrorBody("reason_required"));
                var entry = await ledger.GetAsync(id, ct);
                if (entry is null || entry.ReversesEventId is not null || entry.Points <= 0) return Results.NotFound(new ErrorBody("entry_not_found"));
                if (!await ledger.ReverseAsync(entry, "admin_reversal", ct)) return Results.Conflict(new ErrorBody("already_reversed"));
                await audit.AppendAsync(new AuditRecord(UserId(user), "points.reversed", "contribution", entry.Id, body.Reason.Trim()), ct);
                return Results.NoContent();
            })
            .WithTags("Rewards").RequireAuthorization(PolicyNames.Admin).Produces(204).Produces<ErrorBody>(400).Produces<ErrorBody>(404).Produces<ErrorBody>(409);

        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
