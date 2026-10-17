using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Comments.Services;
using Ratable.Shared.Authors;
using Ratable.Shared.Content;
using Ratable.Shared.Data;

namespace Ratable.Modules.Comments;

public static class CommentsModule
{
    public static IServiceCollection AddCommentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CommentsOptions>(configuration.GetSection(CommentsOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CommentRepository>();
        services.AddIndexInitializer<CommentsIndexInitializer>();
        services.AddSingleton<IContentDirectory, CommentContentDirectory>();
        services.TryAddSingleton<IAuthorPresenter, AuthorPresenter>();
        services.AddSingleton<CommentService>();
        return services;
    }
}
