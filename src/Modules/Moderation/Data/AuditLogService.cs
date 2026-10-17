using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Audit;

namespace Ratable.Modules.Moderation.Data;

/// <summary>The Moderation module's implementation of the shared audit log. Append only.</summary>
public sealed class AuditLogService(AuditLogRepository repository, TimeProvider time) : IAuditLog
{
    public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
        repository.AppendAsync(new AuditLogEntry
        {
            Actor = record.Actor,
            Action = record.Action,
            TargetType = record.TargetType,
            TargetId = record.TargetId,
            Reason = record.Reason,
            Model = record.Model,
            At = time.GetUtcNow().UtcDateTime,
        }, cancellationToken);
}
