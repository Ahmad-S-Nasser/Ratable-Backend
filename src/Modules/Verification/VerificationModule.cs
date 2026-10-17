using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Verification;

public static class VerificationModule
{
    public static IServiceCollection AddVerificationModule(this IServiceCollection services, IConfiguration configuration) => services;
}
