using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Businesses.Services;
using Ratable.Shared.Businesses;
using Ratable.Shared.Data;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Businesses;

public static class BusinessesModule
{
    public static IServiceCollection AddBusinessesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BusinessesOptions>(configuration.GetSection(BusinessesOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        var mail = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
        if (!string.IsNullOrWhiteSpace(mail.Host)) services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        else if (!string.IsNullOrWhiteSpace(mail.OutboxPath)) services.TryAddSingleton<IEmailSender, FileEmailSender>();
        else services.TryAddSingleton<IEmailSender, UnconfiguredEmailSender>();

        services.AddSingleton<ClaimRepository>();
        services.AddSingleton<OwnershipRepository>();
        services.AddSingleton<OwnerReplyRepository>();
        services.AddIndexInitializer<BusinessesIndexInitializer>();

        services.AddSingleton<IEntityOwnership, EntityOwnership>();
        services.AddSingleton<IOwnerReplyDirectory, OwnerReplyDirectory>();
        services.AddSingleton<IQueueCounter, ClaimQueueCounter>();
        services.AddSingleton<OwnerReplyModerator>();
        services.AddSingleton<IContentModerator>(sp => sp.GetRequiredService<OwnerReplyModerator>());
        services.AddSingleton<IContentDirectory>(sp => sp.GetRequiredService<OwnerReplyModerator>());
        services.AddSingleton<SubscriptionRepository>();
        services.AddSingleton<ContactRequestRepository>();
        services.AddSingleton<NudgeRepository>();
        services.AddIndexInitializer<ContactIndexInitializer>();
        services.AddSingleton<IContactAccess, ContactAccess>();
        services.AddSingleton<BusinessStatsService>();
        services.AddSingleton<IBusinessNudgeService, BusinessNudgeService>();
        services.AddSingleton<ContactRequestService>();
        services.AddSingleton<ContactAdminService>();
        services.AddHostedService<ContactRequestCloser>();
        services.AddSingleton<ClaimService>();
        services.AddSingleton<OwnerReplyService>();
        services.AddSingleton<DashboardService>();
        return services;
    }
}
