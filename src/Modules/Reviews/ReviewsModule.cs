using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Services;
using Ratable.Shared.Authors;
using Ratable.Shared.Content;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;

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
        services.AddSingleton<ReviewService>();
        return services;
    }
}
