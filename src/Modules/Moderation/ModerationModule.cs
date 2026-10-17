using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Services;
using Ratable.Shared.Audit;
using Ratable.Shared.Moderation;
using Ratable.Shared.Data;

namespace Ratable.Modules.Moderation;

// P8 will add the moderation entry point here and route each new review by User.PhoneVerified,
// the automatic check result and Category.RatesIndividuals (rule 18):
//  - verified phone, review without proof, checks pass, not an individual: publish without a human queue;
//  - anything the checks flag: moderator first;
//  - every review of an individual (Category.RatesIndividuals): moderator first;
//  - unverified phone, review without proof: moderator first.
// Until then ReviewStatus.PendingModeration stays the default. Verification never changes a score.
public static class ModerationModule
{
    public static IServiceCollection AddModerationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ReportRepository>();
        services.AddSingleton<AuditLogRepository>();
        services.AddIndexInitializer<ModerationIndexInitializer>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditLog, AuditLogService>();
        services.Configure<ModerationOptions>(configuration.GetSection(ModerationOptions.Section));
        services.AddSingleton<ModerationCaseRepository>();
        services.AddSingleton<DisputeRepository>();
        services.AddSingleton<PrescreenService>();
        services.AddSingleton<ModerationService>();
        services.AddSingleton<IModerationIntake>(sp => sp.GetRequiredService<ModerationService>());
        services.AddSingleton<ReportQueueService>();
        services.AddSingleton<DisputeService>();
        services.AddSingleton<OverviewService>();
        services.AddHostedService<ModerationBackfillWorker>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<AnonymousAuthorLookupService>();
        return services;
    }
}
