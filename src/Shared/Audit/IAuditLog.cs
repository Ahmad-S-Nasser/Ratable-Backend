namespace Ratable.Shared.Audit;

/// <summary>One audit entry: who did what to which item and why. Never holds review text or an anonymous author's identity.</summary>
public sealed record AuditRecord(string Actor, string Action, string TargetType, string TargetId, string Reason, string? Model = null);

/// <summary>
/// The append-only audit log (collection audit_log), owned by the Moderation module. Other modules write to it through this interface
/// so they never touch its collection. Every removal, merge and moderator decision is logged here.
/// </summary>
public interface IAuditLog
{
    Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
