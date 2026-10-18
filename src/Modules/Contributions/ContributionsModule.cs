using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Contributions.Data;
using Ratable.Shared.Data;
using Ratable.Shared.Rewards;
using Ratable.Shared.Verification;

namespace Ratable.Modules.Contributions;

public static class ContributionsModule
{
    public static IServiceCollection AddContributionsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RewardsOptions>(configuration.GetSection(RewardsOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ContributionRepository>();
        services.AddIndexInitializer<ContributionsIndexInitializer>();
        services.AddSingleton<RewardsService>();
        services.AddSingleton<IContributionRecorder>(sp => sp.GetRequiredService<RewardsService>());
        services.AddSingleton<IReviewDeletedHandler, PointsCleanupOnReviewDeleted>();
        return services;
    }
}

/// <summary>When a review is deleted, the points it earned are cancelled (a deleted review is hidden, so its effort does not count).</summary>
public sealed class PointsCleanupOnReviewDeleted(RewardsService rewards) : IReviewDeletedHandler
{
    public Task OnReviewDeletedAsync(string reviewId, CancellationToken cancellationToken = default) => rewards.ReverseRootAsync(reviewId, "review_deleted", cancellationToken);
}
