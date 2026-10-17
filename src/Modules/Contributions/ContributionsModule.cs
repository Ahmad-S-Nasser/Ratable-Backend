using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Contributions;

public static class ContributionsModule
{
    public static IServiceCollection AddContributionsModule(this IServiceCollection services, IConfiguration configuration) => services;
}
