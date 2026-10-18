using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;
using Ratable.Shared.Catalog;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;
using Ratable.Shared.Verification;

namespace Ratable.Modules.Reviews.Services;

/// <summary>One answer as sent by a client. Value is a number 1 to 5 for a tap, or the option value for a choice or yes/no.</summary>
public sealed record AnswerInput(string? CriterionId, JsonElement Value);

public sealed record ReviewInput(List<AnswerInput>? Answers, string? Text, string? Language, bool? IsAnonymous, string? Verdict = null);

public enum ReviewFailure
{
    None,
    EntityNotFound,
    NoAnswers,
    InvalidAnswer,
    DuplicateCriterion,
    InvalidText,
    InvalidVerdict,
    AlreadyReviewed,
    NotFound,
    Conflict,
}

public sealed record ReviewResult(ReviewFailure Failure, Review? Review = null, string? Detail = null)
{
    public bool Ok => Failure == ReviewFailure.None;
}

/// <summary>
/// Writing reviews (CLAUDE.md rule 4): one review per account per entity; every edit first saves the version it replaces as a dated
/// revision; deleting hides a review but keeps its revisions. A new or edited review always starts as PendingModeration and Unverified:
/// publishing is the Moderation module's decision and Verified is earned only by proof (rules 3 and 18).
/// </summary>
public sealed class ReviewService(
    ReviewRepository reviews,
    ReviewRevisionRepository revisions,
    IEntityCatalog catalog,
    TimeProvider time,
    IEnumerable<IReviewDeletedHandler> deletedHandlers,
    IModerationIntake moderation,
    ILogger<ReviewService> logger)
{
    public const int MaxTextLength = 4000;

    // ---- create ----

    public async Task<ReviewResult> CreateAsync(string userId, string entityId, ReviewInput input, CancellationToken ct = default)
    {
        var entity = await catalog.GetActiveEntityAsync(entityId, ct);
        var category = entity is null ? null : await catalog.GetCategoryAsync(entity.CategoryId, ct);
        if (entity is null || category is null) return new(ReviewFailure.EntityNotFound);

        var content = Validate(category, input);
        if (content.Failure != ReviewFailure.None) return new(content.Failure, Detail: content.Detail);

        var existing = await reviews.GetByUserAndEntityAsync(userId, entityId, ct);
        if (existing is not null) return new(ReviewFailure.AlreadyReviewed, existing);

        var now = time.GetUtcNow().UtcDateTime;
        var review = new Review
        {
            EntityId = entity.Id,
            CategoryId = category.Id,
            UserId = userId,
            IsAnonymous = input.IsAnonymous ?? category.AnonymousByDefault,
            Answers = content.Answers!,
            Text = content.Text,
            Language = content.Language!,
            Verdict = content.Verdict,
            Status = ReviewStatus.PendingModeration, // P8 decides when it is published
            Verification = new ReviewVerification { Level = VerificationLevel.Unverified },
            RevisionNumber = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await reviews.InsertAsync(review, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(ReviewFailure.AlreadyReviewed, await reviews.GetByUserAndEntityAsync(userId, entityId, ct));
        }
        return new(ReviewFailure.None, await ScreenAsync(review, ct));
    }

    /// <summary>
    /// Hands the review to the Moderation module (AI pre-screen and routing). The module may publish it at once; the stored review is read again so the caller
    /// sees its real state. If moderation cannot be reached the review simply stays PendingModeration and the backfill worker queues it later.
    /// </summary>
    private async Task<Review> ScreenAsync(Review review, CancellationToken ct)
    {
        try
        {
            await moderation.SubmitAsync(new ModerationSubmission(
                ContentKind.Review, review.Id, review.UserId, review.EntityId, review.CategoryId, review.Text, review.Language, review.RevisionNumber,
                review.IsAnonymous, review.Verification.Level == VerificationLevel.Verified), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Moderation intake failed for review {ReviewId}; it stays pending and will be queued by the backfill.", review.Id);
        }
        return await reviews.GetAsync(review.Id, ct) ?? review;
    }

    // ---- edit (and restore) ----

    /// <summary>
    /// Replaces the review's content. The version being replaced is saved first as a revision with its own date, so nothing is ever lost.
    /// Editing a deleted review restores it (the one review the account may have for that entity). The edited review goes back to
    /// PendingModeration, because every edit is moderated again.
    /// </summary>
    public async Task<ReviewResult> EditAsync(string userId, string reviewId, ReviewInput input, CancellationToken ct = default)
    {
        var current = await reviews.GetAsync(reviewId, ct);
        if (current is null || current.UserId != userId) return new(ReviewFailure.NotFound);

        var category = await catalog.GetCategoryAsync(current.CategoryId, ct);
        if (category is null) return new(ReviewFailure.EntityNotFound);

        var content = Validate(category, input);
        if (content.Failure != ReviewFailure.None) return new(content.Failure, Detail: content.Detail);
        var anonymous = input.IsAnonymous ?? current.IsAnonymous;

        if (current.Status != ReviewStatus.Deleted && SameContent(current, content))
        {
            // Nothing in the content changed: no new revision and no new moderation. Only the anonymity switch may differ.
            if (anonymous == current.IsAnonymous) return new(ReviewFailure.None, current);
            return await SetAnonymityAsync(userId, reviewId, anonymous, ct);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var revision = new ReviewRevision
        {
            ReviewId = current.Id,
            RevisionNumber = current.RevisionNumber,
            Answers = current.Answers,
            Text = current.Text,
            Language = current.Language,
            Verdict = current.Verdict,
            ChangedAt = current.UpdatedAt,
            ChangedBy = userId,
            SupersededAt = now,
            StatusWhenSuperseded = current.Status,
        };
        try
        {
            await revisions.AppendAsync(revision, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // The same revision number already exists: either an earlier attempt of this edit stopped half way, or someone else edited first.
            // The compare-and-set below tells the two apart.
        }

        var expected = current.RevisionNumber;
        current.Answers = content.Answers!;
        current.Text = content.Text;
        current.Language = content.Language!;
        current.Verdict = content.Verdict;
        current.IsAnonymous = anonymous;
        current.RevisionNumber = expected + 1;
        current.UpdatedAt = now;
        current.DeletedAt = null;
        current.Status = ReviewStatus.PendingModeration;

        if (!await reviews.TryReplaceAsync(current, expected, ct)) return new(ReviewFailure.Conflict);
        return new(ReviewFailure.None, await ScreenAsync(current, ct));
    }

    // ---- the anonymity switch ----

    /// <summary>
    /// Switches a review between named and anonymous. The flag lives on the review only, never on a revision, and every public response is
    /// built from it when it is asked for, so one switch applies to the current version and to every earlier version at once. It is not a
    /// content change: no revision, no new moderation and the edited-on date stays as it was.
    /// </summary>
    public async Task<ReviewResult> SetAnonymityAsync(string userId, string reviewId, bool isAnonymous, CancellationToken ct = default)
    {
        var current = await reviews.GetAsync(reviewId, ct);
        if (current is null || current.UserId != userId) return new(ReviewFailure.NotFound);
        if (current.IsAnonymous == isAnonymous) return new(ReviewFailure.None, current);

        var expected = current.RevisionNumber;
        current.IsAnonymous = isAnonymous;
        return await reviews.TryReplaceAsync(current, expected, ct) ? new(ReviewFailure.None, current) : new(ReviewFailure.Conflict);
    }

    // ---- delete ----

    /// <summary>Hides the review from everyone. Its revisions are kept for disputes (rule 4). Deleting twice is fine.</summary>
    public async Task<ReviewResult> DeleteAsync(string userId, string reviewId, CancellationToken ct = default)
    {
        var current = await reviews.GetAsync(reviewId, ct);
        if (current is null || current.UserId != userId) return new(ReviewFailure.NotFound);
        if (current.Status == ReviewStatus.Deleted) return new(ReviewFailure.None, current);

        var expected = current.RevisionNumber;
        var now = time.GetUtcNow().UtcDateTime;
        current.Status = ReviewStatus.Deleted;
        current.DeletedAt = now;
        current.UpdatedAt = now;
        if (!await reviews.TryReplaceAsync(current, expected, ct)) return new(ReviewFailure.Conflict);
        foreach (var handler in deletedHandlers) await handler.OnReviewDeletedAsync(current.Id, ct); // kept proof files go too
        return new(ReviewFailure.None, current);
    }

    // ---- validation ----

    private sealed record Content(ReviewFailure Failure, string? Detail, List<ReviewAnswer>? Answers, string? Text, string? Language, string? Verdict = null);

    private static Content Validate(CategoryInfo category, ReviewInput input)
    {
        var answers = new List<ReviewAnswer>();
        if (input.Answers is null || input.Answers.Count == 0) return Fail(ReviewFailure.NoAnswers);

        foreach (var a in input.Answers)
        {
            var criterion = category.Criteria.FirstOrDefault(c => c.Id == a.CriterionId);
            if (criterion is null) return Fail(ReviewFailure.InvalidAnswer, a.CriterionId);
            if (answers.Any(x => x.CriterionId == criterion.Id)) return Fail(ReviewFailure.DuplicateCriterion, criterion.Id);

            switch (criterion.Kind)
            {
                case CriterionKind.Rating5:
                    if (!TryReadScore(a.Value, out var score)) return Fail(ReviewFailure.InvalidAnswer, criterion.Id);
                    answers.Add(new ReviewAnswer { CriterionId = criterion.Id, Score = score });
                    break;
                default:
                    var value = a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString() : null;
                    var option = criterion.Options.FirstOrDefault(o => o.Value == value);
                    if (option is null) return Fail(ReviewFailure.InvalidAnswer, criterion.Id);
                    answers.Add(new ReviewAnswer { CriterionId = criterion.Id, OptionValue = option.Value, Score = option.Score });
                    break;
            }
        }

        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) text = null;
        if (text is not null && (text.Length > MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
            return Fail(ReviewFailure.InvalidText);

        var language = input.Language?.Trim().ToLowerInvariant();
        if (language is not (null or "" or "ar" or "en")) return Fail(ReviewFailure.InvalidText, "language");
        if (string.IsNullOrEmpty(language)) language = GuessLanguage(text);

        // The verdict is optional, and only for categories that have verdicts. An empty value means none.
        var verdict = string.IsNullOrWhiteSpace(input.Verdict) ? null : input.Verdict.Trim().ToLowerInvariant();
        if (verdict is not null && (!category.VerdictEnabled || !Scoring.Verdicts.All.Contains(verdict))) return Fail(ReviewFailure.InvalidVerdict);

        return new Content(ReviewFailure.None, null, answers, text, language, verdict);
    }

    private static Content Fail(ReviewFailure failure, string? detail = null) => new(failure, detail, null, null, null);

    private static bool TryReadScore(JsonElement value, out int score)
    {
        score = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out score) && score is >= 1 and <= 5;
        return false;
    }

    /// <summary>Arabic when more than a third of the letters are Arabic, otherwise English. With no text the default is Arabic.</summary>
    public static string GuessLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "ar";
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return "ar";
        var arabic = letters.Count(c => c is >= '؀' and <= 'ۿ');
        return arabic * 3 > letters.Count ? "ar" : "en";
    }

    private static bool SameContent(Review current, Content content) =>
        current.Text == content.Text &&
        current.Language == content.Language &&
        current.Verdict == content.Verdict &&
        current.Answers.Count == content.Answers!.Count &&
        current.Answers.OrderBy(a => a.CriterionId).Zip(content.Answers.OrderBy(a => a.CriterionId))
            .All(p => p.First.CriterionId == p.Second.CriterionId && p.First.Score == p.Second.Score && p.First.OptionValue == p.Second.OptionValue);
}
