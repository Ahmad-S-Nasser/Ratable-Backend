using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Modules.Comments.Data;
using Ratable.Shared.Data;

namespace Ratable.Modules.Comments;

public static class CommentsModule
{
    public static IServiceCollection AddCommentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<CommentRepository>();
        services.AddIndexInitializer<CommentsIndexInitializer>();
        return services;
    }
}
