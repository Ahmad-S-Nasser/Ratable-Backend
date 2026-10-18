using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Services;
using Ratable.Shared.Authors;
using Ratable.Shared.Content;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;
using Ratable.Shared.Moderation;
using Ratable.Shared.Verification;

namespace Ratable.Modules.Reviews;

public static class ReviewsModule
{
    public static IServiceCollection AddReviewsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ReviewRepository>();
        services.AddSingleton<ReviewRevisionRepository>();
        services.AddIndexInitializer<ReviewsIndexInitializer>();
        services.AddSingleton<IEntityMergeParticipant, ReviewMergeParticipant>();
        services.AddSingleton<IContentDirectory, ReviewContentDirectory>();
        services.TryAddSingleton<IAuthorPresenter, AuthorPresenter>();
        services.AddSingleton<IReviewVerifier, ReviewVerifier>();
        services.AddSingleton<IContentModerator, ReviewContentModerator>();
        services.AddSingleton<ReviewService>();
        services.AddSingleton<Votes.ReviewVoteRepository>();
        services.AddIndexInitializer<Votes.ReviewVoteIndexInitializer>();
        services.AddSingleton<Lists.ListRepository>();
        services.AddSeeder<Lists.ListSeeder>();
        services.Configure<Seo.SeoOptions>(configuration.GetSection("Seo"));
        services.Configure<Scoring.VerdictOptions>(configuration.GetSection(Scoring.VerdictOptions.Section));
        services.AddHttpClient();
        services.AddSingleton<Seo.PlaceChangeFeed>();
        services.AddSingleton<Seo.SeoNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<Seo.SeoNotifier>());
        return services;
    }
}
