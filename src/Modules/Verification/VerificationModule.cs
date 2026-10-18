using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Verification.Data;
using Ratable.Modules.Verification.Presence;
using Ratable.Modules.Verification.Services;
using Ratable.Modules.Verification.Storage;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;
using Ratable.Shared.Moderation;
using Ratable.Shared.Verification;

namespace Ratable.Modules.Verification;

public static class VerificationModule
{
    public static IServiceCollection AddVerificationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VerificationOptions>(configuration.GetSection(VerificationOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<VerificationRepository>();
        services.AddSingleton<ConsentRepository>();
        services.AddIndexInitializer<VerificationIndexInitializer>();
        services.AddSingleton<IEntityMergeParticipant, VerificationMergeParticipant>();

        services.TryAddSingleton<IProofStorage, LocalDiskProofStorage>();
        services.TryAddSingleton<Ratable.Shared.Storage.IPrivateFileStore>(sp => sp.GetRequiredService<IProofStorage>());
        services.TryAddSingleton<IProofSampler, RandomProofSampler>();
        services.TryAddSingleton<IPresenceProofService, UnavailablePresenceProofService>();
        services.AddSingleton<ProofChecker>();
        services.AddSingleton<VerificationService>();
        services.AddSingleton<IQueueCounter, ProofQueueCounter>();
        services.AddSingleton<IReviewDeletedHandler, ProofCleanupOnReviewDeleted>();
        services.AddHostedService<ProofRetentionWorker>();
        return services;
    }
}
