using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Modules.Reviews.Data;
using Ratable.Shared.Data;

namespace Ratable.Modules.Reviews;

public static class ReviewsModule
{
    public static IServiceCollection AddReviewsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ReviewRepository>();
        services.AddSingleton<ReviewRevisionRepository>();
        services.AddIndexInitializer<ReviewsIndexInitializer>();
        return services;
    }
}
