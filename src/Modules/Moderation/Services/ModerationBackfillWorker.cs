using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratable.Modules.Moderation.Services;

/// <summary>Every few minutes, makes sure no pending review or comment is waiting without a case in the queue (for example if moderation was unreachable when it was written).</summary>
public sealed class ModerationBackfillWorker(ModerationService service, ILogger<ModerationBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                var created = await service.BackfillAsync(stoppingToken);
                if (created > 0) logger.LogInformation("Queued {Count} items that had no moderation case", created);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Moderation backfill failed; it will try again in a few minutes.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
