using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Moderation;

public static class ModerationModule
{
    public static IServiceCollection AddModerationModule(this IServiceCollection services, IConfiguration configuration) => services;
}
