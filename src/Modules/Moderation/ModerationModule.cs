using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Modules.Moderation.Data;
using Ratable.Shared.Data;

namespace Ratable.Modules.Moderation;

public static class ModerationModule
{
    public static IServiceCollection AddModerationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ReportRepository>();
        services.AddSingleton<AuditLogRepository>();
        services.AddIndexInitializer<ModerationIndexInitializer>();
        return services;
    }
}
