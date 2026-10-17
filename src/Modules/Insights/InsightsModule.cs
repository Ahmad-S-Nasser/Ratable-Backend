using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Insights;

public static class InsightsModule
{
    public static IServiceCollection AddInsightsModule(this IServiceCollection services, IConfiguration configuration) => services;
}
