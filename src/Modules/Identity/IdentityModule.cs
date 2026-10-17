using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Modules.Identity.Data;
using Ratable.Shared.Data;

namespace Ratable.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<UserRepository>();
        services.AddIndexInitializer<IdentityIndexInitializer>();
        return services;
    }
}
