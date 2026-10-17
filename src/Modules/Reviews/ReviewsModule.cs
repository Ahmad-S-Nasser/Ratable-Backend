using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Reviews;

public static class ReviewsModule
{
    public static IServiceCollection AddReviewsModule(this IServiceCollection services, IConfiguration configuration) => services;
}
