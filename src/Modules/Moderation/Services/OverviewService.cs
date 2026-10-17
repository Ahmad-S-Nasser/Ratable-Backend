using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Moderation.Services;

public sealed record QueueCount(string Name, long Count);

public sealed record Overview(List<QueueCount> Queues, DateTime? OldestOpenCaseAt, long DecidedAutomatically30Days, long DecidedByPeople30Days);

/// <summary>What is waiting for a moderator right now, in every queue, for the console home page and its alerts.</summary>
public sealed class OverviewService(
    ModerationCaseRepository cases,
    ReportRepository reports,
    DisputeRepository disputes,
    IEnumerable<IQueueCounter> counters,
    TimeProvider time)
{
    public async Task<Overview> GetAsync(CancellationToken ct = default)
    {
        var queues = new List<QueueCount>
        {
            new("reviews", await cases.CountOpenAsync(ContentKind.Review, ct)),
            new("comments", await cases.CountOpenAsync(ContentKind.Comment, ct)),
            new("reports", await reports.CountOpenAsync(ct)),
            new("disputes", await disputes.CountOpenAsync(ct)),
        };
        foreach (var counter in counters) queues.Add(new QueueCount(counter.Name, await counter.CountAsync(ct)));

        var since = time.GetUtcNow().UtcDateTime.AddDays(-30);
        return new Overview(queues, await cases.OldestOpenAsync(ct),
            await cases.CountDecidedSinceAsync(since, CaseRoute.Auto, ct), await cases.CountDecidedSinceAsync(since, CaseRoute.Human, ct));
    }
}
