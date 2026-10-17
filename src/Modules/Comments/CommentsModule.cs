using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ratable.Modules.Comments;

public static class CommentsModule
{
    public static IServiceCollection AddCommentsModule(this IServiceCollection services, IConfiguration configuration) => services;
}
