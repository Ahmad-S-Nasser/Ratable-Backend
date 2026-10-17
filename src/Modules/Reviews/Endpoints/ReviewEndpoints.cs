using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Modules.Reviews.Services;
using Ratable.Shared.Auth;
using Ratable.Shared.Authors;
using Ratable.Shared.Catalog;
using Ratable.Shared.Data;

namespace Ratable.Modules.Reviews.Endpoints;

public sealed record ErrorBody(string Error, string? Detail = null);

public sealed record AnonymityBody(bool? IsAnonymous);

public sealed record AnswerDto(string CriterionId, int Score, string? OptionValue);

/// <summary>
/// A review as everyone sees it. <see cref="Author"/> is null when the review is anonymous (or the author has no public name); it is built
/// only by <see cref="IAuthorPresenter"/>, never from the stored review, so there is no author id here to leak (rule 1).
/// </summary>
public sealed record PublicReview(
    string Id, bool Anonymous, PublicAuthor? Author, List<AnswerDto> Answers, double? Overall, string? Text, string Language, bool Verified,
    DateTime CreatedAt, DateTime? EditedOn, int RevisionNumber, bool UnderReview);

/// <summary>An earlier version. It has no author field: the author is the review's, and is shown (or not) with the current version.</summary>
public sealed record PublicRevision(int RevisionNumber, List<AnswerDto> Answers, double? Overall, string? Text, string Language, DateTime WrittenAt, DateTime ReplacedAt);

public sealed record ReviewHistory(PublicReview Current, List<PublicRevision> EarlierVersions);

/// <summary>The author's own view of their review, including what state it is in.</summary>
public sealed record OwnReview(
    string Id, string EntityId, string Status, bool IsAnonymous, bool Verified, List<AnswerDto> Answers, double? Overall, string? Text,
    string Language, int RevisionNumber, DateTime CreatedAt, DateTime UpdatedAt, bool UnderReview, string? ModerationReason);

public sealed record CriterionScoreDto(string CriterionId, int Answers, double? Score);
public sealed record ScoreResponse(double? Overall, int VerifiedReviews, int PublishedReviews, int MinAnswersForCriterion, List<CriterionScoreDto> Criteria);

/// <summary>A place in a category listing: its verified score and how many verified reviews that is built from. Nothing here depends on payment.</summary>
public sealed record PlaceCard(string Id, LocalizedText Name, Dictionary<string, string> Slugs, string? City, double? Overall, int VerifiedReviews);

public sealed record PlaceList(List<PlaceCard> Places, int Total);

public sealed record SitemapPlace(string Id, string CategoryId, Dictionary<string, string> Slugs, DateTime LastModified);

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var onEntity = app.MapGroup("/api/entities/{entityId}").WithTags("Reviews");
        onEntity.MapPost("/reviews", Create).RequireAuthorization(PolicyNames.SignedIn).Produces<OwnReview>(201).Produces<ErrorBody>(400).Produces<ErrorBody>(404).Produces(409);
        onEntity.MapGet("/reviews", List).Produces<List<PublicReview>>().Produces(404);
        onEntity.MapGet("/score", Score).Produces<ScoreResponse>().Produces(404);

        var reviews = app.MapGroup("/api/reviews").WithTags("Reviews");
        reviews.MapGet("/{id}/history", History).Produces<ReviewHistory>().Produces(404);
        reviews.MapPut("/{id}", Edit).RequireAuthorization(PolicyNames.SignedIn).Produces<OwnReview>().Produces<ErrorBody>(400).Produces<ErrorBody>(404).Produces(409);
        reviews.MapPut("/{id}/anonymity", SetAnonymity).RequireAuthorization(PolicyNames.SignedIn).Produces<OwnReview>().Produces<ErrorBody>(400).Produces<ErrorBody>(404);
        reviews.MapDelete("/{id}", Delete).RequireAuthorization(PolicyNames.SignedIn);

        app.MapGet("/api/categories/{categoryId}/places", Places).WithTags("Reviews").Produces<PlaceList>().Produces(404);
        app.MapGet("/api/sitemap/places", SitemapPlaces).WithTags("Reviews").Produces<List<SitemapPlace>>();
        app.MapGet("/api/me/reviews", Mine).WithTags("Reviews").RequireAuthorization(PolicyNames.SignedIn).Produces<List<OwnReview>>();
        return app;
    }

    /// <summary>
    /// The places of a category ranked by verified score first, then by number of verified reviews, then by name; places with no verified score come
    /// last. Nothing paid changes the order (rule 6). Scores are worked out per request over at most 500 places; storing them is a later optimisation.
    /// </summary>
    private static async Task<IResult> Places(string categoryId, int? limit, int? skip, string? lang, ReviewRepository repository, IEntityCatalog catalog, CancellationToken ct)
    {
        if (await catalog.GetCategoryAsync(categoryId, ct) is null) return Results.NotFound();
        var entries = await catalog.ListActiveByCategoryAsync(categoryId, 500, ct);
        var reviews = (await repository.ListScoringForEntitiesAsync([.. entries.Select(e => e.Id)], ct)).ToLookup(r => r.EntityId);
        var key = lang == "en" ? "en" : "ar";
        var ranked = entries
            .Select(e =>
            {
                var score = ScoreCalculator.ForEntity(reviews[e.Id]);
                return new PlaceCard(e.Id, e.Name, new Dictionary<string, string>(e.Slugs), e.City, score.Overall, score.VerifiedReviewCount);
            })
            .OrderByDescending(p => p.Overall.HasValue).ThenByDescending(p => p.Overall).ThenByDescending(p => p.VerifiedReviews)
            .ThenBy(p => key == "en" ? p.Name.En ?? p.Name.Ar : p.Name.Ar ?? p.Name.En, StringComparer.CurrentCulture)
            .ToList();
        var page = ranked.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(limit ?? 20, 1, 50)).ToList();
        return Results.Ok(new PlaceList(page, ranked.Count));
    }

    /// <summary>
    /// Places for the sitemap: only those with at least one verified review (the others are not offered to search), with the date of the latest
    /// change to the place or its reviews as lastmod.
    /// </summary>
    private static async Task<IResult> SitemapPlaces(int? skip, int? limit, ReviewRepository repository, IEntityCatalog catalog, CancellationToken ct)
    {
        var changed = await repository.ListVerifiedEntitiesAsync(Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 1000, 1, 5000), ct);
        var entries = (await catalog.GetActiveEntriesAsync([.. changed.Select(c => c.EntityId)], ct)).ToDictionary(e => e.Id);
        return Results.Ok(changed.Where(c => entries.ContainsKey(c.EntityId)).Select(c =>
        {
            var e = entries[c.EntityId];
            return new SitemapPlace(e.Id, e.CategoryId, new Dictionary<string, string>(e.Slugs), e.UpdatedAt > c.LastChange ? e.UpdatedAt : c.LastChange);
        }).ToList());
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static async Task<IResult> Create(string entityId, ReviewInput body, ClaimsPrincipal user, ReviewService service, CancellationToken ct) =>
        ToResult(await service.CreateAsync(UserId(user), entityId, body, ct), created: true);

    private static async Task<IResult> Edit(string id, ReviewInput body, ClaimsPrincipal user, ReviewService service, CancellationToken ct) =>
        ToResult(await service.EditAsync(UserId(user), id, body, ct), created: false);

    private static async Task<IResult> SetAnonymity(string id, AnonymityBody body, ClaimsPrincipal user, ReviewService service, CancellationToken ct) =>
        body.IsAnonymous is null
            ? Results.BadRequest(new ErrorBody("is_anonymous_required"))
            : ToResult(await service.SetAnonymityAsync(UserId(user), id, body.IsAnonymous.Value, ct), created: false);

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

    private static async Task<IResult> List(
        string entityId, int? limit, int? skip, ReviewRepository repository, IEntityCatalog catalog, IAuthorPresenter authors, CancellationToken ct)
    {
        if (await catalog.GetActiveEntityAsync(entityId, ct) is null) return Results.NotFound();
        var found = await repository.ListPublishedAsync(entityId, Math.Clamp(limit ?? 20, 1, 50), Math.Max(0, skip ?? 0), ct);
        return Results.Ok(await ToPublic(found, authors, ct));
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
    private static async Task<IResult> History(
        string id, ReviewRepository repository, ReviewRevisionRepository revisions, IAuthorPresenter authors, CancellationToken ct)
    {
        var review = await repository.GetAsync(id, ct);
        if (review is not { Status: ReviewStatus.Published }) return Results.NotFound();
        var earlier = (await revisions.ListAsync(review.Id, ct)).Where(r => r.StatusWhenSuperseded == ReviewStatus.Published);
        var current = (await ToPublic([review], authors, ct)).Single();
        return Results.Ok(new ReviewHistory(current,
            [.. earlier.Select(r => new PublicRevision(r.RevisionNumber, ToAnswers(r.Answers), ScoreCalculator.ReviewOverall(r.Answers), r.Text, r.Language, r.ChangedAt, r.SupersededAt))]));
    }

    private static async Task<IResult> Mine(string? entityId, ClaimsPrincipal user, ReviewRepository repository, CancellationToken ct) =>
        Results.Ok((await repository.ListByUserAsync(UserId(user), entityId, ct)).Select(ToOwn));

    private static List<AnswerDto> ToAnswers(IEnumerable<ReviewAnswer> answers) => [.. answers.Select(a => new AnswerDto(a.CriterionId, a.Score, a.OptionValue))];

    /// <summary>The one place a review becomes a public response. The author comes from the presenter and nowhere else.</summary>
    private static async Task<List<PublicReview>> ToPublic(IReadOnlyCollection<Review> reviews, IAuthorPresenter authors, CancellationToken ct)
    {
        var presented = await authors.PresentAsync([.. reviews.Select(r => new AuthorSlot(r.Id, r.UserId, r.IsAnonymous))], ct);
        return [.. reviews.Select(r => new PublicReview(
            r.Id, presented[r.Id] is null, presented[r.Id], ToAnswers(r.Answers), ScoreCalculator.ReviewOverall(r.Answers), r.Text, r.Language,
            r.Verification.Level == VerificationLevel.Verified, r.CreatedAt, r.RevisionNumber > 1 ? r.UpdatedAt : null, r.RevisionNumber, r.UnderReview))];
    }

    private static OwnReview ToOwn(Review r) => new(
        r.Id, r.EntityId, r.Status.ToString(), r.IsAnonymous, r.Verification.Level == VerificationLevel.Verified, ToAnswers(r.Answers),
        ScoreCalculator.ReviewOverall(r.Answers), r.Text, r.Language, r.RevisionNumber, r.CreatedAt, r.UpdatedAt, r.UnderReview,
        r.Status == ReviewStatus.Hidden ? r.ModerationReason : null);
}
