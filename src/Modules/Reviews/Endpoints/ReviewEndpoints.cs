using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Modules.Reviews.Services;
using Ratable.Shared.Auth;
using Ratable.Shared.Catalog;

namespace Ratable.Modules.Reviews.Endpoints;

public sealed record ErrorBody(string Error, string? Detail = null);

public sealed record AnswerDto(string CriterionId, int Score, string? OptionValue);

/// <summary>
/// A review as everyone sees it. There is deliberately NO author field: who wrote a review is added by the anonymity work (P6),
/// together with the leak test, so nothing about the author can slip into a public response before then.
/// </summary>
public sealed record PublicReview(
    string Id, List<AnswerDto> Answers, double? Overall, string? Text, string Language, bool Verified,
    DateTime CreatedAt, DateTime? EditedOn, int RevisionNumber);

public sealed record PublicRevision(int RevisionNumber, List<AnswerDto> Answers, double? Overall, string? Text, string Language, DateTime WrittenAt, DateTime ReplacedAt);

public sealed record ReviewHistory(PublicReview Current, List<PublicRevision> EarlierVersions);

/// <summary>The author's own view of their review, including what state it is in.</summary>
public sealed record OwnReview(
    string Id, string EntityId, string Status, bool IsAnonymous, bool Verified, List<AnswerDto> Answers, double? Overall, string? Text,
    string Language, int RevisionNumber, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record CriterionScoreDto(string CriterionId, int Answers, double? Score);
public sealed record ScoreResponse(double? Overall, int VerifiedReviews, int PublishedReviews, int MinAnswersForCriterion, List<CriterionScoreDto> Criteria);

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var onEntity = app.MapGroup("/api/entities/{entityId}").WithTags("Reviews");
        onEntity.MapPost("/reviews", Create).RequireAuthorization(PolicyNames.SignedIn);
        onEntity.MapGet("/reviews", List);
        onEntity.MapGet("/score", Score);

        var reviews = app.MapGroup("/api/reviews").WithTags("Reviews");
        reviews.MapGet("/{id}/history", History);
        reviews.MapPut("/{id}", Edit).RequireAuthorization(PolicyNames.SignedIn);
        reviews.MapDelete("/{id}", Delete).RequireAuthorization(PolicyNames.SignedIn);

        app.MapGet("/api/me/reviews", Mine).WithTags("Reviews").RequireAuthorization(PolicyNames.SignedIn);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static async Task<IResult> Create(string entityId, ReviewInput body, ClaimsPrincipal user, ReviewService service, CancellationToken ct) =>
        ToResult(await service.CreateAsync(UserId(user), entityId, body, ct), created: true);

    private static async Task<IResult> Edit(string id, ReviewInput body, ClaimsPrincipal user, ReviewService service, CancellationToken ct) =>
        ToResult(await service.EditAsync(UserId(user), id, body, ct), created: false);

    private static async Task<IResult> Delete(string id, ClaimsPrincipal user, ReviewService service, CancellationToken ct)
    {
        var result = await service.DeleteAsync(UserId(user), id, ct);
        return result.Ok ? Results.NoContent() : ToResult(result, created: false);
    }

    private static IResult ToResult(ReviewResult r, bool created) => r.Failure switch
    {
        ReviewFailure.None => created
            ? Results.Created($"/api/me/reviews?entityId={r.Review!.EntityId}", ToOwn(r.Review))
            : Results.Ok(ToOwn(r.Review!)),
        ReviewFailure.EntityNotFound => Results.NotFound(new ErrorBody("entity_not_found")),
        ReviewFailure.NotFound => Results.NotFound(new ErrorBody("review_not_found")),
        ReviewFailure.NoAnswers => Results.BadRequest(new ErrorBody("no_answers")),
        ReviewFailure.InvalidAnswer => Results.BadRequest(new ErrorBody("invalid_answer", r.Detail)),
        ReviewFailure.DuplicateCriterion => Results.BadRequest(new ErrorBody("duplicate_criterion", r.Detail)),
        ReviewFailure.InvalidText => Results.BadRequest(new ErrorBody("invalid_text", r.Detail)),
        ReviewFailure.AlreadyReviewed => Results.Conflict(new { error = "already_reviewed", reviewId = r.Review?.Id, status = r.Review?.Status.ToString() }),
        _ => Results.Conflict(new ErrorBody("edit_conflict")),
    };

    private static async Task<IResult> List(string entityId, int? limit, int? skip, ReviewRepository repository, IEntityCatalog catalog, CancellationToken ct)
    {
        if (await catalog.GetActiveEntityAsync(entityId, ct) is null) return Results.NotFound();
        var found = await repository.ListPublishedAsync(entityId, Math.Clamp(limit ?? 20, 1, 50), Math.Max(0, skip ?? 0), ct);
        return Results.Ok(found.Select(ToPublic));
    }

    private static async Task<IResult> Score(string entityId, ReviewRepository repository, IEntityCatalog catalog, CancellationToken ct)
    {
        if (await catalog.GetActiveEntityAsync(entityId, ct) is null) return Results.NotFound();
        var score = ScoreCalculator.ForEntity(await repository.ListScoringAsync(entityId, ct));
        var published = (int)await repository.CountPublishedAsync(entityId, ct);
        return Results.Ok(new ScoreResponse(score.Overall, score.VerifiedReviewCount, published, ScoreCalculator.MinAnswersForCriterion,
            [.. score.Criteria.Select(c => new CriterionScoreDto(c.CriterionId, c.AnswerCount, c.Score))]));
    }

    /// <summary>The public history: the current version and the earlier versions that were public when they were replaced.</summary>
    private static async Task<IResult> History(string id, ReviewRepository repository, ReviewRevisionRepository revisions, CancellationToken ct)
    {
        var review = await repository.GetAsync(id, ct);
        if (review is not { Status: ReviewStatus.Published }) return Results.NotFound();
        var earlier = (await revisions.ListAsync(review.Id, ct)).Where(r => r.StatusWhenSuperseded == ReviewStatus.Published);
        return Results.Ok(new ReviewHistory(ToPublic(review),
            [.. earlier.Select(r => new PublicRevision(r.RevisionNumber, ToAnswers(r.Answers), ScoreCalculator.ReviewOverall(r.Answers), r.Text, r.Language, r.ChangedAt, r.SupersededAt))]));
    }

    private static async Task<IResult> Mine(string? entityId, ClaimsPrincipal user, ReviewRepository repository, CancellationToken ct) =>
        Results.Ok((await repository.ListByUserAsync(UserId(user), entityId, ct)).Select(ToOwn));

    private static List<AnswerDto> ToAnswers(IEnumerable<ReviewAnswer> answers) => [.. answers.Select(a => new AnswerDto(a.CriterionId, a.Score, a.OptionValue))];

    private static PublicReview ToPublic(Review r) => new(
        r.Id, ToAnswers(r.Answers), ScoreCalculator.ReviewOverall(r.Answers), r.Text, r.Language,
        r.Verification.Level == VerificationLevel.Verified, r.CreatedAt, r.RevisionNumber > 1 ? r.UpdatedAt : null, r.RevisionNumber);

    private static OwnReview ToOwn(Review r) => new(
        r.Id, r.EntityId, r.Status.ToString(), r.IsAnonymous, r.Verification.Level == VerificationLevel.Verified, ToAnswers(r.Answers),
        ScoreCalculator.ReviewOverall(r.Answers), r.Text, r.Language, r.RevisionNumber, r.CreatedAt, r.UpdatedAt);
}
