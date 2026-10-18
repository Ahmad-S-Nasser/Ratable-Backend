using Microsoft.Extensions.Options;
using Ratable.Modules.Contributions.Data;
using Ratable.Modules.Contributions.Models;
using Ratable.Shared.Data;
using Ratable.Shared.Rewards;

namespace Ratable.Modules.Contributions;

/// <summary>
/// Writes to and reads the points ledger (CLAUDE.md rule 16). Points reward effort and usefulness only: the amount depends on what was done (a written
/// review, a photo ...), never on whether the opinion was positive or negative, and never on payment. Nothing in this class, or any class that reads it,
/// feeds a score, a verdict, a ranking or a removal.
/// </summary>
public sealed class RewardsService(ContributionRepository ledger, IOptions<RewardsOptions> options, TimeProvider time) : IContributionRecorder
{
    private RewardsOptions Opt => options.Value;

    public async Task RecordAsync(string userId, string action, string reference, string rootReference, string? entityId = null, int? textLength = null, CancellationToken ct = default)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(userId, out _) || !Opt.Points.TryGetValue(action, out var points) || points <= 0) return;
        // A review is "written" only when it has some words. Ratings alone earn nothing under this action.
        if (action == ContributionActions.WrittenReview && (textLength ?? 0) < Opt.MinWrittenLength) return;

        var now = time.GetUtcNow().UtcDateTime;
        if (Opt.DailyCaps.TryGetValue(action, out var cap))
        {
            var earnedToday = await ledger.NetPointsSinceAsync(userId, action, now.Date, ct);
            if (earnedToday + points > cap) return; // the daily limit for this kind of action is reached
        }

        await ledger.TryAppendEarnAsync(new ContributionEvent
        {
            UserId = userId,
            Action = action,
            Points = points,
            Reason = action,
            Reference = reference,
            RootReference = rootReference,
            EntityId = entityId,
            CreatedAt = now,
        }, ct);
    }

    public async Task ReverseReferenceAsync(string reference, string reason, CancellationToken ct = default)
    {
        foreach (var entry in await ledger.ListLiveByReferenceAsync(reference, null, ct)) await ledger.ReverseAsync(entry, reason, ct);
    }

    public async Task ReverseRootAsync(string rootReference, string reason, CancellationToken ct = default)
    {
        foreach (var entry in await ledger.ListLiveByReferenceAsync(null, rootReference, ct)) await ledger.ReverseAsync(entry, reason, ct);
    }

    // ---- reading ----

    public sealed record RankView(string Id, int MinPoints, LocalizedText Title);
    public sealed record BadgeView(string Id, LocalizedText Title, LocalizedText Description, int Progress, int Threshold, bool Earned);
    public sealed record MissionView(string Id, LocalizedText Title, int Progress, int Target, bool Done);
    public sealed record EventView(string Id, string Action, int Points, string Reason, DateTime CreatedAt);
    public sealed record RankUp(RankView From, RankView To);
    public sealed record RewardsView(
        int Points, RankView Rank, RankView? NextRank, int PointsToNext, List<BadgeView> Badges, List<MissionView> Missions, int StreakWeeks, List<EventView> Recent, RankUp? RankUp);

    private RankView RankFor(int points)
    {
        var rank = Opt.Ranks.OrderBy(r => r.MinPoints).Last(r => points >= r.MinPoints);
        return new RankView(rank.Id, rank.MinPoints, rank.Title);
    }

    public async Task<RewardsView> GetAsync(string userId, CancellationToken ct = default)
    {
        var all = await ledger.ListForUserAsync(userId, ct);
        var reversed = all.Where(e => e.ReversesEventId is not null).Select(e => e.ReversesEventId!).ToHashSet();
        // The live entries: earned and not cancelled.
        var live = all.Where(e => e.ReversesEventId is null && e.Points > 0 && !reversed.Contains(e.Id)).ToList();

        var points = Math.Max(0, all.Sum(e => e.Points));
        var rank = RankFor(points);
        var ordered = Opt.Ranks.OrderBy(r => r.MinPoints).ToList();
        var next = ordered.FirstOrDefault(r => r.MinPoints > points);

        var counts = live.GroupBy(e => e.Action).ToDictionary(g => g.Key, g => g.Count());
        var badges = Opt.Badges.Select(b =>
        {
            var have = counts.GetValueOrDefault(b.Action);
            return new BadgeView(b.Id, b.Title, b.Description, Math.Min(have, b.Threshold), b.Threshold, have >= b.Threshold);
        }).ToList();

        var now = time.GetUtcNow().UtcDateTime;
        var weekStart = WeekStart(now);
        var thisWeek = live.Where(e => e.CreatedAt >= weekStart).GroupBy(e => e.Action).ToDictionary(g => g.Key, g => g.Count());
        var missions = Opt.Missions.Select(m =>
        {
            var have = thisWeek.GetValueOrDefault(m.Action);
            return new MissionView(m.Id, m.Title, Math.Min(have, m.Target), m.Target, have >= m.Target);
        }).ToList();

        // A rank-up is announced once: the first time a person is seen only records their rank.
        var seen = await ledger.GetSeenRankAsync(userId, ct);
        RankUp? rankUp = null;
        if (seen is null) await ledger.SetSeenRankAsync(userId, rank.Id, ct);
        else if (seen != rank.Id)
        {
            var from = ordered.FirstOrDefault(r => r.Id == seen);
            if (from is not null && from.MinPoints < rank.MinPoints) rankUp = new RankUp(new RankView(from.Id, from.MinPoints, from.Title), rank);
            else await ledger.SetSeenRankAsync(userId, rank.Id, ct); // a rank that went down is not announced
        }

        return new RewardsView(
            points, rank, next is null ? null : new RankView(next.Id, next.MinPoints, next.Title), next is null ? 0 : next.MinPoints - points, badges, missions,
            StreakWeeks(live, now), [.. all.Take(20).Select(e => new EventView(e.Id, e.Action, e.Points, e.Reason, e.CreatedAt))], rankUp);
    }

    public Task AcknowledgeRankAsync(string userId, string rankId, CancellationToken ct = default) => ledger.SetSeenRankAsync(userId, rankId, ct);

    /// <summary>The start of the week that contains the given moment (the first day of the week is configurable; Saturday by default).</summary>
    public DateTime WeekStart(DateTime moment)
    {
        var diff = ((int)moment.DayOfWeek - (int)Opt.WeekStartsOn + 7) % 7;
        return moment.Date.AddDays(-diff);
    }

    /// <summary>
    /// The streak counts weeks (not days): how many weeks in a row had at least one effort entry. The current week counts if it has one; if it has none
    /// yet, the streak so far is kept and the week is still open.
    /// </summary>
    public int StreakWeeks(IReadOnlyCollection<ContributionEvent> live, DateTime now)
    {
        var weeks = live.Where(e => Opt.StreakActions.Contains(e.Action)).Select(e => WeekStart(e.CreatedAt)).ToHashSet();
        var week = WeekStart(now);
        if (!weeks.Contains(week)) week = week.AddDays(-7);
        var streak = 0;
        while (weeks.Contains(week))
        {
            streak++;
            week = week.AddDays(-7);
        }
        return streak;
    }
}
