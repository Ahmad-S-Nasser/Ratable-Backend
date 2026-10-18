using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Media.Data;
using Ratable.Modules.Media.Processing;
using Ratable.Modules.Media.Services;
using Ratable.Modules.Media.Storage;
using Ratable.Shared.Data;
using Ratable.Shared.Media;
using Ratable.Shared.Moderation;
using Ratable.Shared.Verification;

namespace Ratable.Modules.Media;

public static class MediaModule
{
    public static IServiceCollection AddMediaModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<MediaRepository>();
        services.AddIndexInitializer<MediaIndexInitializer>();
        services.TryAddSingleton<IMediaStorage, LocalDiskMediaStorage>();
        services.TryAddSingleton<IMediaProcessor, FfmpegMediaProcessor>();
        services.AddSingleton<UploadTokens>();
        services.AddSingleton<MediaAnalyzer>();
        services.AddSingleton<MediaPipeline>();
        services.AddSingleton<MediaService>();
        services.AddSingleton<MediaModerationService>();
        services.AddSingleton<MediaPublicService>();
        services.AddSingleton<IReviewMedia>(sp => sp.GetRequiredService<MediaPublicService>());
        services.AddSingleton<IQueueCounter, MediaQueueCounter>();
        services.AddSingleton<IReviewDeletedHandler, MediaCleanupOnReviewDeleted>();
        services.AddHostedService<MediaWorker>();
        return services;
    }
}

/// <summary>When a review is deleted, its photos and videos (and their files) are deleted with it.</summary>
public sealed class MediaCleanupOnReviewDeleted(MediaService service) : IReviewDeletedHandler
{
    public Task OnReviewDeletedAsync(string reviewId, CancellationToken cancellationToken = default) => service.DeleteForReviewAsync(reviewId, cancellationToken);
}
