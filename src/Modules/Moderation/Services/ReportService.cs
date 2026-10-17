using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Audit;
using Ratable.Shared.Authors;
using Ratable.Shared.Content;

namespace Ratable.Modules.Moderation.Services;

public sealed class ModerationOptions
{
    public const string Section = "Moderation";

    public int MaxReportsPerDay { get; set; } = 30;

    /// <summary>An AI flag counts when its confidence is at least this.</summary>
    public double FlagThreshold { get; set; } = 0.5;

    /// <summary>Behaviour signals. The same text again within this many days is flagged (texts shorter than MinCopiedTextLength are not compared).</summary>
    public int CopiedTextWindowDays { get; set; } = 90;
    public int MinCopiedTextLength { get; set; } = 20;

    /// <summary>This many new reviews of one entity within the window (this one included) is a burst.</summary>
    public int BurstThreshold { get; set; } = 5;
    public int BurstWindowMinutes { get; set; } = 60;

    /// <summary>This many OTHER accounts reviewing the same entity from the same network or device is flagged.</summary>
    public int SharedDeviceMinOtherAccounts { get; set; } = 2;
    public int SharedDeviceWindowDays { get; set; } = 30;
    public int MinLookupReasonLength { get; set; } = 10;
    public int MaxReasonLength { get; set; } = 500;
}

public enum ReportFailure { None, InvalidTarget, NotFound, InvalidReason, OwnContent, AlreadyReported, RateLimited }

public sealed record ReportResult(ReportFailure Failure, Report? Report = null)
{
    public bool Ok => Failure == ReportFailure.None;
}

/// <summary>Any signed-in reader can report a public review or comment. The report goes to the moderator queue; it never hides anything by itself.</summary>
public sealed class ReportService(
    ReportRepository reports,
    IEnumerable<IContentDirectory> directories,
    IOptions<ModerationOptions> options,
    TimeProvider time)
{
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string> { "abuse", "personal_data", "spam", "fake", "off_topic", "other" };

    public async Task<ReportResult> FileAsync(string reporterId, string? targetType, string? targetId, string? reason, string? details, CancellationToken ct = default)
    {
        if (!Enum.TryParse<ContentKind>(targetType, ignoreCase: true, out var kind)) return new(ReportFailure.InvalidTarget);
        if (reason is null || !Reasons.Contains(reason)) return new(ReportFailure.InvalidReason);
        var note = details?.Trim();
        if (note is { Length: > 500 } || note?.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) == true) return new(ReportFailure.InvalidReason);

        var content = string.IsNullOrWhiteSpace(targetId) ? null : await directories.First(d => d.Kind == kind).GetAsync(targetId, ct);
        if (content is not { IsPublic: true }) return new(ReportFailure.NotFound);
        if (content.AuthorId == reporterId) return new(ReportFailure.OwnContent);

        var now = time.GetUtcNow().UtcDateTime;
        if (await reports.CountByReporterSinceAsync(reporterId, now.AddDays(-1), ct) >= options.Value.MaxReportsPerDay) return new(ReportFailure.RateLimited);

        var report = new Report
        {
            TargetType = kind == ContentKind.Review ? ReportTarget.Review : ReportTarget.Comment,
            TargetId = content.Id,
            ReporterId = reporterId,
            Reason = reason,
            Details = string.IsNullOrEmpty(note) ? null : note,
            CreatedAt = now,
        };
        try
        {
            await reports.InsertAsync(report, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(ReportFailure.AlreadyReported);
        }
        return new(ReportFailure.None, report);
    }
}

public enum LookupFailure { None, InvalidTarget, ReasonRequired, NotFound, NotAnonymous }

public sealed record LookupResult(LookupFailure Failure, AccountSummary? Account = null, string? ContentKind = null, string? ContentId = null)
{
    public bool Ok => Failure == LookupFailure.None;
}

/// <summary>
/// The audited admin look-up of who wrote an anonymous review or comment (rule 1: the platform always knows, nobody else does).
/// A written reason is required, and the look-up is recorded in the audit log BEFORE any identity is read: if recording fails, nothing is
/// revealed. The audit entry holds the reason and the content, never the author. A phone number is never part of the answer.
/// </summary>
public sealed class AnonymousAuthorLookupService(
    IEnumerable<IContentDirectory> directories,
    IAccountDirectory accounts,
    IAuditLog audit,
    IOptions<ModerationOptions> options)
{
    public async Task<LookupResult> LookUpAsync(string adminId, string? targetType, string? targetId, string? reason, CancellationToken ct = default)
    {
        if (!Enum.TryParse<ContentKind>(targetType, ignoreCase: true, out var kind)) return new(LookupFailure.InvalidTarget);
        var why = reason?.Trim();
        if (why is null || why.Length < options.Value.MinLookupReasonLength || why.Length > options.Value.MaxReasonLength) return new(LookupFailure.ReasonRequired);

        var content = string.IsNullOrWhiteSpace(targetId) ? null : await directories.First(d => d.Kind == kind).GetAsync(targetId, ct);
        if (content is null) return new(LookupFailure.NotFound);
        if (!content.IsAnonymous) return new(LookupFailure.NotAnonymous); // a named author is already public; no reason to go through this

        await audit.AppendAsync(new AuditRecord(adminId, "anonymous_author.lookup", kind.ToString().ToLowerInvariant(), content.Id, why), ct);

        var account = await accounts.GetAsync(content.AuthorId, ct);
        return new(LookupFailure.None, account, kind.ToString(), content.Id);
    }
}
