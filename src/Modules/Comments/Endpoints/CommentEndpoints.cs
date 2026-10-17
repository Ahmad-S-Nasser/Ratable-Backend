using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Comments.Models;
using Ratable.Modules.Comments.Services;
using Ratable.Shared.Auth;
using Ratable.Shared.Authors;
using Ratable.Shared.Content;

namespace Ratable.Modules.Comments.Endpoints;

public sealed record ErrorBody(string Error);
public sealed record EditCommentBody(string? Text);
public sealed record AnonymityBody(bool? IsAnonymous);

/// <summary>A comment as everyone sees it. <see cref="Author"/> comes only from <see cref="IAuthorPresenter"/>, so there is no author id here to leak.</summary>
public sealed record PublicComment(
    string Id, bool Anonymous, PublicAuthor? Author, string Text, DateTime CreatedAt, bool Edited, DateTime? EditedOn, List<PublicComment> Replies);

public sealed record OwnComment(string Id, string ReviewId, string? ParentId, string Status, bool IsAnonymous, string Text, DateTime CreatedAt, DateTime? EditedAt);

public static class CommentEndpoints
{
    public static IEndpointRouteBuilder MapCommentEndpoints(this IEndpointRouteBuilder app)
    {
        var onReview = app.MapGroup("/api/reviews/{reviewId}/comments").WithTags("Comments");
        onReview.MapGet("", List).Produces<List<PublicComment>>().Produces(404);
        onReview.MapPost("", Create).RequireAuthorization(PolicyNames.SignedIn);

        var comments = app.MapGroup("/api/comments").WithTags("Comments").RequireAuthorization(PolicyNames.SignedIn);
        comments.MapPut("/{id}", Edit);
        comments.MapPut("/{id}/anonymity", SetAnonymity);
        comments.MapDelete("/{id}", Delete);

        app.MapGet("/api/me/comments", Mine).WithTags("Comments").RequireAuthorization(PolicyNames.SignedIn);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static async Task<IResult> Create(string reviewId, CommentInput body, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
        ToResult(await service.CreateAsync(UserId(user), reviewId, body, ct), created: true);

    private static async Task<IResult> Edit(string id, EditCommentBody body, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
        ToResult(await service.EditAsync(UserId(user), id, body.Text, ct), created: false);

    private static async Task<IResult> SetAnonymity(string id, AnonymityBody body, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
        body.IsAnonymous is null
            ? Results.BadRequest(new ErrorBody("is_anonymous_required"))
            : ToResult(await service.SetAnonymityAsync(UserId(user), id, body.IsAnonymous.Value, ct), created: false);

    private static async Task<IResult> Delete(string id, ClaimsPrincipal user, CommentService service, CancellationToken ct)
    {
        var result = await service.DeleteAsync(UserId(user), id, ct);
        return result.Ok ? Results.NoContent() : ToResult(result, created: false);
    }

    private static IResult ToResult(CommentResult r, bool created) => r.Failure switch
    {
        CommentFailure.None => created ? Results.Created($"/api/me/comments?reviewId={r.Comment!.ReviewId}", ToOwn(r.Comment)) : Results.Ok(ToOwn(r.Comment!)),
        CommentFailure.ReviewNotFound => Results.NotFound(new ErrorBody("review_not_found")),
        CommentFailure.NotFound => Results.NotFound(new ErrorBody("comment_not_found")),
        CommentFailure.InvalidText => Results.BadRequest(new ErrorBody("invalid_text")),
        CommentFailure.InvalidParent => Results.BadRequest(new ErrorBody("invalid_parent")),
        _ => Results.Json(new ErrorBody("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests),
    };

    private static async Task<IResult> List(
        string reviewId, CommentRepository repository, IEnumerable<IContentDirectory> directories, IAuthorPresenter authors, CancellationToken ct)
    {
        var review = await directories.First(d => d.Kind == ContentKind.Review).GetAsync(reviewId, ct);
        if (review is not { IsPublic: true }) return Results.NotFound();

        var published = await repository.ListPublishedAsync(review.Id, ct);
        var presented = await authors.PresentAsync([.. published.Select(c => new AuthorSlot(c.Id, c.UserId, c.IsAnonymous))], ct);

        PublicComment Map(Comment c, List<PublicComment> replies) =>
            new(c.Id, presented[c.Id] is null, presented[c.Id], c.Text, c.CreatedAt, c.EditedAt is not null, c.EditedAt, replies);

        // A reply is shown only under a top-level comment that is itself public.
        var topLevel = published.Where(c => c.ParentId is null)
            .Select(c => Map(c, [.. published.Where(r => r.ParentId == c.Id).Select(r => Map(r, []))]));
        return Results.Ok(topLevel);
    }

    private static async Task<IResult> Mine(string? reviewId, ClaimsPrincipal user, CommentRepository repository, CancellationToken ct) =>
        Results.Ok((await repository.ListByUserAsync(UserId(user), reviewId, ct)).Select(ToOwn));

    private static OwnComment ToOwn(Comment c) => new(c.Id, c.ReviewId, c.ParentId, c.Status.ToString(), c.IsAnonymous, c.Text, c.CreatedAt, c.EditedAt);
}
