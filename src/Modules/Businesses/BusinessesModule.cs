using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Businesses;

public static class BusinessesModule
{
    public static IServiceCollection AddBusinessesModule(this IServiceCollection services, IConfiguration configuration) => services;
}
