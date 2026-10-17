using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Entities.Services;

public sealed class EntityQueueCounter(EntityRepository entities) : IQueueCounter
{
    public string Name => "entities";

    public Task<long> CountAsync(CancellationToken ct = default) => entities.CountByStatusAsync(EntityStatus.PendingModeration, ct);
}
