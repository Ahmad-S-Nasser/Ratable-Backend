using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Modules.Verification.Data;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;

namespace Ratable.Modules.Verification;

public static class VerificationModule
{
    public static IServiceCollection AddVerificationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<VerificationRepository>();
        services.AddIndexInitializer<VerificationIndexInitializer>();
        services.AddSingleton<IEntityMergeParticipant, VerificationMergeParticipant>();
        return services;
    }
}
