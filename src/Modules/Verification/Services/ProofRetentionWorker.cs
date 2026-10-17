using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratable.Modules.Verification.Services;

/// <summary>Deletes proof files that are due (decided and set to delete, or past their keep date), every hour. A safety net: decisions also delete at once.</summary>
public sealed class ProofRetentionWorker(VerificationService service, ILogger<ProofRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var deleted = await service.PurgeDueAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("Deleted {Count} proof files that were due", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Proof retention run failed; it will try again in an hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
