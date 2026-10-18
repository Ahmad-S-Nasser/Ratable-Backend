using Microsoft.Extensions.Options;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Audit;
using Ratable.Shared.Businesses;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Moderation.Services;

public enum ResolveFailure { None, NotFound, WrongState, ReasonRequired, InvalidAction }

public sealed record ResolveResult(ResolveFailure Failure)
{
    public bool Ok => Failure == ResolveFailure.None;
}

/// <summary>Reports from readers. A moderator resolves all open reports on the same item at once: dismiss them, or hide the item with a reason.</summary>
public sealed class ReportQueueService(
    ReportRepository reports,
    IEnumerable<IContentModerator> moderators,
    IAuditLog audit,
    TimeProvider time)
{
    public async Task<List<ReportGroup>> QueueAsync(CancellationToken ct = default)
    {
        var open = await reports.ListOpenAsync(ct: ct);
        var groups = new List<ReportGroup>();
        foreach (var g in open.GroupBy(r => (r.TargetType, r.TargetId)).OrderBy(g => g.Min(r => r.CreatedAt)))
        {
            var kind = g.Key.TargetType == ReportTarget.Comment ? ContentKind.Comment : ContentKind.Review;
            var view = await moderators.First(m => m.Kind == kind).GetViewAsync(g.Key.TargetId, ct);
            groups.Add(new ReportGroup(g.OrderBy(r => r.CreatedAt).First().Id, g.Key.TargetType.ToString(), g.Key.TargetId, g.Count(),
                [.. g.GroupBy(r => r.Reason).Select(x => new ReasonCount(x.Key, x.Count()))], [.. g.Where(r => r.Details != null).Select(r => r.Details!).Take(5)],
                g.Min(r => r.CreatedAt), view));
        }
        return groups;
    }

    /// <summary>action is "dismiss" or "hide". Hiding needs a reason (the author is shown it). Every open report on the item is closed.</summary>
    public async Task<ResolveResult> ResolveAsync(string moderatorId, string reportId, string? action, string? reason, CancellationToken ct = default)
    {
        var report = await reports.GetAsync(reportId, ct);
        if (report is null) return new(ResolveFailure.NotFound);
        if (report.Status != ReportStatus.Open) return new(ResolveFailure.WrongState);
        if (action is not ("dismiss" or "hide")) return new(ResolveFailure.InvalidAction);
        var why = reason?.Trim();
        if (action == "hide" && (string.IsNullOrEmpty(why) || why.Length > 500)) return new(ResolveFailure.ReasonRequired);

        var now = time.GetUtcNow().UtcDateTime;
        var kind = report.TargetType == ReportTarget.Comment ? ContentKind.Comment : ContentKind.Review;
        if (action == "hide") await moderators.First(m => m.Kind == kind).ApplyAsync(report.TargetId, ModerationOutcome.Hide, why, ct);

        foreach (var r in await reports.ListOpenForTargetAsync(report.TargetType, report.TargetId, ct))
        {
            r.Status = action == "hide" ? ReportStatus.Upheld : ReportStatus.Dismissed;
            r.ResolvedAt = now;
            r.ResolvedBy = moderatorId;
            await reports.ReplaceAsync(r, ct);
        }
        await audit.AppendAsync(new AuditRecord(moderatorId, action == "hide" ? "report.upheld" : "report.dismissed", kind.ToString().ToLowerInvariant(), report.TargetId,
            why ?? "Reports dismissed"), ct);
        return new(ResolveFailure.None);
    }
}

public sealed record ReasonCount(string Reason, int Count);

public sealed record ReportGroup(
    string ResolveId, string TargetType, string TargetId, int ReportCount, List<ReasonCount> Reasons, List<string> Notes, DateTime FirstReportedAt, ModerationContentView? Content);

public enum DisputeFailure { None, NotFound, InvalidReason, OwnReview, AlreadyOpen, WrongState, ReasonRequired, NotOwner }

public sealed record DisputeResult(DisputeFailure Failure, Dispute? Dispute = null)
{
    public bool Ok => Failure == DisputeFailure.None;
}

/// <summary>
/// Disputes (launch plan, "Moderation workflow"): a disputed review stays visible with an "under review" label unless it breaks a rule. A business cannot delete a
/// review. Raising a dispute is for business owners; deciding is for moderators. Whether the business owns the place is checked once the Businesses module exists.
/// </summary>
public sealed class DisputeService(
    DisputeRepository disputes,
    IEnumerable<IContentModerator> moderators,
    IEnumerable<IContentDirectory> directories,
    IEnumerable<IEntityOwnership> ownership,
    IAuditLog audit,
    IOptions<ModerationOptions> options,
    TimeProvider time)
{
    private IContentModerator Reviews => moderators.First(m => m.Kind == ContentKind.Review);

    public async Task<DisputeResult> RaiseAsync(string userId, string reviewId, string? reason, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        if (why is null || why.Length < options.Value.MinLookupReasonLength || why.Length > 1000) return new(DisputeFailure.InvalidReason);

        var review = await directories.First(d => d.Kind == ContentKind.Review).GetAsync(reviewId, ct);
        if (review is not { IsPublic: true }) return new(DisputeFailure.NotFound);
        if (review.AuthorId == userId) return new(DisputeFailure.OwnReview);
        // Only the account that runs the place the review is about may dispute it.
        var owns = false;
        foreach (var o in ownership) owns |= review.EntityId is not null && await o.IsOwnerAsync(userId, review.EntityId, ct);
        if (!owns) return new(DisputeFailure.NotOwner);
        if (await disputes.HasOpenForReviewAsync(reviewId, ct)) return new(DisputeFailure.AlreadyOpen);

        var dispute = new Dispute { ReviewId = review.Id, EntityId = review.EntityId, RaisedBy = userId, Reason = why, CreatedAt = time.GetUtcNow().UtcDateTime };
        await disputes.InsertAsync(dispute, ct);
        await Reviews.SetUnderReviewAsync(review.Id, true, ct); // visible, with the label
        await audit.AppendAsync(new AuditRecord(userId, "dispute.raised", "review", review.Id, why), ct);
        return new(DisputeFailure.None, dispute);
    }

    /// <summary>Upheld: the review breaks a rule, so it is hidden (a reason is needed and the author sees it). Dismissed: the label is removed and the review stays.</summary>
    public async Task<DisputeResult> DecideAsync(string moderatorId, string disputeId, bool uphold, string? reason, CancellationToken ct = default)
    {
        var dispute = await disputes.GetAsync(disputeId, ct);
        if (dispute is null) return new(DisputeFailure.NotFound);
        if (dispute.State != DisputeState.Open) return new(DisputeFailure.WrongState);
        var why = reason?.Trim();
        if (uphold && (string.IsNullOrEmpty(why) || why.Length > 500)) return new(DisputeFailure.ReasonRequired);

        if (uphold) await Reviews.ApplyAsync(dispute.ReviewId, ModerationOutcome.Hide, why, ct);
        dispute.State = uphold ? DisputeState.Upheld : DisputeState.Dismissed;
        dispute.DecidedAt = time.GetUtcNow().UtcDateTime;
        dispute.DecidedBy = moderatorId;
        dispute.DecisionReason = why;
        await disputes.ReplaceAsync(dispute, ct);

        if (!await disputes.HasOpenForReviewAsync(dispute.ReviewId, ct)) await Reviews.SetUnderReviewAsync(dispute.ReviewId, false, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, uphold ? "dispute.upheld" : "dispute.dismissed", "review", dispute.ReviewId, why ?? "Dispute dismissed: the review does not break a rule"), ct);
        return new(DisputeFailure.None, dispute);
    }

    public async Task<List<(Dispute Dispute, ModerationContentView? Review)>> QueueAsync(CancellationToken ct = default)
    {
        var open = await disputes.ListOpenAsync(ct: ct);
        var result = new List<(Dispute, ModerationContentView?)>();
        foreach (var d in open) result.Add((d, await Reviews.GetViewAsync(d.ReviewId, ct)));
        return result;
    }
}
