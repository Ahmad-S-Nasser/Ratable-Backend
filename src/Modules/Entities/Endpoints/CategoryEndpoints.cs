using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Endpoints;

public sealed record OptionDto(string Value, LocalizedText Label);
public sealed record CriterionDto(string Id, LocalizedText Label, string AnswerType, List<OptionDto> Options);
public sealed record FieldDto(string Key, LocalizedText Label, string Type, List<OptionDto> Options);

/// <summary>Category configuration as the web and app need it to build the review form and entity pages. Scores of choice options are not included.</summary>
public sealed record CategoryDto(
    string Id, LocalizedText Name, string Icon, string SchemaOrgType, bool AnonymousByDefault, bool VerdictEnabled, bool CollectionsEnabled,
    List<CriterionDto> Criteria, List<FieldDto> EntityFields, List<string> AcceptedProofTypes);

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/categories").WithTags("Categories");
        group.MapGet("", async (CategoryRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.ListActiveAsync(ct)).OrderBy(c => c.Id).Select(ToDto)));
        group.MapGet("/{id}", async (string id, CategoryRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { Active: true } c ? Results.Ok(ToDto(c)) : Results.NotFound());
        return app;
    }

    private static CategoryDto ToDto(Category c) => new(
        c.Id, c.Name, c.Icon, c.SchemaOrgType, c.AnonymousByDefault, c.VerdictEnabled, c.CollectionsEnabled,
        [.. c.Criteria.Select(x => new CriterionDto(x.Id, x.Label, x.AnswerType.ToString(), [.. x.Options.Select(o => new OptionDto(o.Value, o.Label))]))],
        [.. c.EntityFields.Select(f => new FieldDto(f.Key, f.Label, f.Type, [.. f.Options.Select(o => new OptionDto(o.Value, o.Label))]))],
        c.AcceptedProofTypes);
}
