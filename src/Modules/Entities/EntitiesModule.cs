using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Entities;

public static class EntitiesModule
{
    public static IServiceCollection AddEntitiesModule(this IServiceCollection services, IConfiguration configuration) => services;
}
