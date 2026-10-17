using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Audit;
using Ratable.Shared.Catalog;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;
using Ratable.Shared.Text;

namespace Ratable.Modules.Moderation.Services;

public enum CaseFailure { None, NotFound, WrongState, ReasonRequired, ContentGone }

public sealed record CaseResult(CaseFailure Failure, ModerationCase? Case = null)
{
    public bool Ok => Failure == CaseFailure.None;
}

/// <summary>
/// The moderation queue. Every new or edited review and comment gets a case: the behaviour signals and the AI pre-screen run, the routing rules decide who
/// reads it first, and the content is published by the rules or waits for a moderator. A decision needs no explanation to publish, and a written reason to hide
/// (the author is shown it). Nothing a business pays for, and nothing about the author's identity, takes part in any of this.
/// </summary>
public sealed class ModerationService(
    ModerationCaseRepository cases,
    PrescreenService prescreen,
    IEnumerable<IContentModerator> moderators,
    IEnumerable<IContentDirectory> directories,
    IAccountSignals accountSignals,
    IEntityCatalog catalog,
    IAuditLog audit,
    IOptions<ModerationOptions> options,
    TimeProvider time) : IModerationIntake
{
    private ModerationOptions Opt => options.Value;

    private IContentModerator For(ContentKind kind) => moderators.First(m => m.Kind == kind);

    // ---- intake: a new or edited review or comment ----

    public async Task SubmitAsync(ModerationSubmission s, CancellationToken ct = default)
    {
        if (await cases.ExistsAsync(s.Kind, s.ContentId, s.RevisionNumber, ct)) return; // each version is screened once
        await cases.SupersedeOlderAsync(s.Kind, s.ContentId, s.RevisionNumber, ct);

        var now = time.GetUtcNow().UtcDateTime;
        var account = await accountSignals.GetAsync(s.AuthorId, ct);
        var category = s.CategoryId is null ? null : await catalog.GetCategoryAsync(s.CategoryId, ct);

        var textHash = HashOf(s.Text);
        var flags = new List<string>();

        // Behaviour signals (weak on their own: they only send the content to a person, never reject it).
        if (textHash is not null && await cases.TextSeenElsewhereAsync(textHash, s.ContentId, now.AddDays(-Opt.CopiedTextWindowDays), ct)) flags.Add("copied_text");
        if (s is { Kind: ContentKind.Review, EntityId: not null })
        {
            if (await cases.CountForEntitySinceAsync(s.EntityId, now.AddMinutes(-Opt.BurstWindowMinutes), ct) + 1 >= Opt.BurstThreshold) flags.Add("review_burst");
            if (await cases.OtherAccountsOnSameDeviceAsync(s.EntityId, s.AuthorId, account.IpHash, account.DeviceHash, now.AddDays(-Opt.SharedDeviceWindowDays), ct) >= Opt.SharedDeviceMinOtherAccounts)
                flags.Add("shared_device_or_network");
        }

        var screen = await prescreen.RunAsync(s.Kind, s.ContentId, s.Text, s.Language, s.IsAnonymous, ct);
        flags.AddRange(screen.Reasons);

        var decision = RoutingPolicy.Decide(new RoutingInput(s.Kind, screen.Completed, screen.Deferred, flags, category?.RatesIndividuals ?? false, s.HasVerifiedProof, account.PhoneVerified));

        var c = new ModerationCase
        {
            Kind = s.Kind,
            ContentId = s.ContentId,
            RevisionNumber = s.RevisionNumber,
            EntityId = s.EntityId,
            CategoryId = s.CategoryId,
            Route = decision.Route,
            Reasons = decision.Reasons,
            AiStatus = screen.AiStatus,
            Prescreen = screen.Result,
            AuthorPhoneVerified = account.PhoneVerified,
            HasVerifiedProof = s.HasVerifiedProof,
            IsAnonymous = s.IsAnonymous,
            TextHash = textHash,
            IpHash = account.IpHash,
            DeviceHash = account.DeviceHash,
            AuthorId = s.AuthorId,
            CreatedAt = now,
        };
        if (!await cases.TryInsertAsync(c, ct)) return;

        if (decision.Route == CaseRoute.Auto) await PublishByRulesAsync(c, now, ct);
    }

    public async Task OnReviewVerifiedAsync(string reviewId, CancellationToken ct = default)
    {
        var c = await cases.GetOpenForContentAsync(ContentKind.Review, reviewId, ct);
        if (c is null) return;
        c.HasVerifiedProof = true;
        if (RoutingPolicy.OnlyWaitingForProof(c.Reasons))
        {
            // The checks were clean and the only thing missing was proof: now there is one.
            c.Route = CaseRoute.Auto;
            c.Reasons = [];
            await PublishByRulesAsync(c, time.GetUtcNow().UtcDateTime, ct);
        }
        else
        {
            await cases.ReplaceAsync(c, ct);
        }
    }

    private async Task PublishByRulesAsync(ModerationCase c, DateTime now, CancellationToken ct)
    {
        if (!await For(c.Kind).ApplyAsync(c.ContentId, ModerationOutcome.Publish, null, ct))
        {
            c.State = CaseState.Superseded; // the content changed or is gone: its newer version has its own case
            await cases.ReplaceAsync(c, ct);
            return;
        }
        c.State = CaseState.Decided;
        c.Decision = CaseDecision.Published;
        c.DecidedAt = now;
        c.DecidedBy = "auto";
        await cases.ReplaceAsync(c, ct);
    }

    // ---- a moderator decides ----

    public async Task<CaseResult> DecideAsync(string moderatorId, string caseId, bool publish, string? reason, CancellationToken ct = default)
    {
        var c = await cases.GetAsync(caseId, ct);
        if (c is null) return new(CaseFailure.NotFound);
        if (c.State != CaseState.Open) return new(CaseFailure.WrongState);
        var why = reason?.Trim();
        if (!publish && (string.IsNullOrEmpty(why) || why.Length > 500)) return new(CaseFailure.ReasonRequired);

        var applied = await For(c.Kind).ApplyAsync(c.ContentId, publish ? ModerationOutcome.Publish : ModerationOutcome.Hide, why, ct);
        if (!applied)
        {
            c.State = CaseState.Superseded; // edited or removed while it waited
            await cases.ReplaceAsync(c, ct);
            return new(CaseFailure.ContentGone, c);
        }

        var now = time.GetUtcNow().UtcDateTime;
        c.State = CaseState.Decided;
        c.Decision = publish ? CaseDecision.Published : CaseDecision.Hidden;
        c.DecisionReason = why;
        c.DecidedAt = now;
        c.DecidedBy = moderatorId;
        await cases.ReplaceAsync(c, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, publish ? "moderation.published" : "moderation.hidden", c.Kind.ToString().ToLowerInvariant(), c.ContentId,
            why ?? "Published from the moderation queue", c.Prescreen?.Model), ct);
        return new(CaseFailure.None, c);
    }

    // ---- reading the queue ----

    public async Task<(List<(ModerationCase Case, ModerationContentView? Content)> Items, long Total)> QueueAsync(ContentKind? kind, int skip, int limit, CancellationToken ct = default)
    {
        var open = await cases.ListOpenAsync(kind, skip, limit, ct);
        var items = new List<(ModerationCase, ModerationContentView?)>();
        foreach (var c in open) items.Add((c, await For(c.Kind).GetViewAsync(c.ContentId, ct)));
        return (items, await cases.CountOpenAsync(kind, ct));
    }

    /// <summary>
    /// Makes sure nothing waits without a case (for example when moderation could not be reached at the moment a review was written):
    /// every pending review or comment that has no case for its version gets one now.
    /// </summary>
    public async Task<int> BackfillAsync(CancellationToken ct = default)
    {
        var created = 0;
        foreach (var moderator in moderators)
        {
            var directory = directories.First(d => d.Kind == moderator.Kind);
            foreach (var pending in await moderator.ListPendingAsync(100, ct))
            {
                if (await cases.ExistsAsync(moderator.Kind, pending.Id, pending.RevisionNumber, ct)) continue;
                var info = await directory.GetAsync(pending.Id, ct);
                var view = await moderator.GetViewAsync(pending.Id, ct);
                if (info is null || view is null) continue;
                await SubmitAsync(new ModerationSubmission(moderator.Kind, pending.Id, info.AuthorId, info.EntityId, info.CategoryId, view.Text, view.Language,
                    pending.RevisionNumber, info.IsAnonymous, view.VerifiedProof), ct);
                created++;
            }
        }
        return created;
    }

    /// <summary>A one-way hash of the normalized text, so the same text posted again is noticed without keeping the text. Too-short texts are not compared.</summary>
    internal string? HashOf(string? text)
    {
        var normalized = ArabicNormalizer.Normalize(text);
        return normalized.Length < Opt.MinCopiedTextLength ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
