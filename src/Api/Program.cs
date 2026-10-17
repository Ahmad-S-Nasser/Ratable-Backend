using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Ratable.Modules.Ai;
using Ratable.Modules.Ai.Endpoints;
using Ratable.Modules.Businesses;
using Ratable.Modules.Comments;
using Ratable.Modules.Comments.Endpoints;
using Ratable.Modules.Contributions;
using Ratable.Modules.Entities;
using Ratable.Modules.Entities.Endpoints;
using Ratable.Modules.Identity;
using Ratable.Modules.Identity.Endpoints;
using Ratable.Modules.Insights;
using Ratable.Modules.Moderation;
using Ratable.Modules.Moderation.Endpoints;
using Ratable.Modules.Reviews;
using Ratable.Modules.Reviews.Endpoints;
using Ratable.Modules.Verification;
using Ratable.Modules.Verification.Endpoints;
using Ratable.Shared.Data;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configuration: appsettings.json holds defaults only; real values come from environment variables
// (for example Mongo__ConnectionString), which on IIS are set per site in web.config or the app pool.
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddOpenApi();
builder.Services.AddRatableMongo(builder.Configuration);
builder.Services
    .AddIdentityModule(builder.Configuration)
    .AddEntitiesModule(builder.Configuration)
    .AddReviewsModule(builder.Configuration)
    .AddCommentsModule(builder.Configuration)
    .AddVerificationModule(builder.Configuration)
    .AddModerationModule(builder.Configuration)
    .AddBusinessesModule(builder.Configuration)
    .AddInsightsModule(builder.Configuration)
    .AddContributionsModule(builder.Configuration)
    .AddAiModule(builder.Configuration);

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityEndpoints();
app.MapAdminUserEndpoints();
app.MapAiEndpoints();
app.MapEntityEndpoints();
app.MapCategoryEndpoints();
app.MapReviewEndpoints();
app.MapCommentEndpoints();
app.MapModerationEndpoints();
app.MapVerificationEndpoints();

app.MapOpenApi("/openapi/{documentName}.json");

// Liveness: the process is up. Readiness: its dependencies (MongoDB) answer.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
