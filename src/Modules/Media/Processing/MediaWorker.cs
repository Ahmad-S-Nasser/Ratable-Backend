using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Media.Data;
using Ratable.Modules.Media.Services;

namespace Ratable.Modules.Media.Processing;

/// <summary>The background worker: takes uploaded photos and videos one at a time and runs them through the pipeline.</summary>
public sealed class MediaWorker(MediaRepository items, MediaPipeline pipeline, IOptions<MediaOptions> options, TimeProvider time, ILogger<MediaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        try { await items.RequeueStuckAsync(time.GetUtcNow().UtcDateTime.AddMinutes(-30), stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not requeue stuck media"); }

        var delay = TimeSpan.FromSeconds(Math.Max(1, options.Value.WorkerIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = await items.ClaimNextUploadedAsync(stoppingToken);
                if (item is null)
                {
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }
                await pipeline.ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Media worker error");
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
