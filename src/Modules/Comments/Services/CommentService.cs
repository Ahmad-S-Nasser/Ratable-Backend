using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Comments.Models;
using Ratable.Shared.Catalog;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Comments.Services;

public sealed class CommentsOptions
{
    public const string Section = "Comments";

    public int MaxLength { get; set; } = 1000;
    public int MaxPerHour { get; set; } = 10;
}

public sealed record CommentInput(string? Text, string? ParentId, bool? IsAnonymous);

public enum CommentFailure { None, ReviewNotFound, InvalidText, InvalidParent, RateLimited, NotFound }

public sealed record CommentResult(CommentFailure Failure, Comment? Comment = null)
{
    public bool Ok => Failure == CommentFailure.None;
}

/// <summary>
/// Comments on reviews, one level deep. They start as PendingModeration (the Moderation module publishes them, with the same pre-screen as
/// reviews) and an edit sends them back there. They never touch a score. Like reviews they store the real author and an anonymous flag;
/// what the public sees is decided when the response is built.
/// </summary>
public sealed class CommentService(
    CommentRepository comments,
    IEnumerable<IContentDirectory> directories,
    IEntityCatalog catalog,
    IOptions<CommentsOptions> options,
    TimeProvider time,
    IModerationIntake moderation,
    ILogger<CommentService> logger)
{
    private CommentsOptions Opt => options.Value;

    public async Task<CommentResult> CreateAsync(string userId, string reviewId, CommentInput input, CancellationToken ct = default)
    {
        var review = await directories.First(d => d.Kind == ContentKind.Review).GetAsync(reviewId, ct);
        if (review is not { IsPublic: true }) return new(CommentFailure.ReviewNotFound);

        var text = CleanText(input.Text);
        if (text is null) return new(CommentFailure.InvalidText);

        if (input.ParentId is not null)
        {
            var parent = await comments.GetAsync(input.ParentId, ct);
            // One level deep: the parent must be a public top-level comment on the same review.
            if (parent is not { Status: CommentStatus.Published, ParentId: null } || parent.ReviewId != review.Id) return new(CommentFailure.InvalidParent);
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (await comments.CountByUserSinceAsync(userId, now.AddHours(-1), ct) >= Opt.MaxPerHour) return new(CommentFailure.RateLimited);

        var anonymous = input.IsAnonymous ?? (review.CategoryId is null ? false : (await catalog.GetCategoryAsync(review.CategoryId, ct))?.AnonymousByDefault ?? false);
        var comment = new Comment
        {
            ReviewId = review.Id,
            ParentId = input.ParentId,
            UserId = userId,
            IsAnonymous = anonymous,
            Text = text,
            Status = CommentStatus.PendingModeration,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await comments.InsertAsync(comment, ct);
        return new(CommentFailure.None, await ScreenAsync(comment, ct));
    }

    /// <summary>Hands the comment to moderation (same pre-screen and routing as reviews) and reads it back, since it may have been published at once.</summary>
    private async Task<Comment> ScreenAsync(Comment comment, CancellationToken ct)
    {
        try
        {
            await moderation.SubmitAsync(new ModerationSubmission(
                ContentKind.Comment, comment.Id, comment.UserId, null, null, comment.Text, GuessLanguage(comment.Text),
                comment.Revision, comment.IsAnonymous, false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Moderation intake failed for comment {CommentId}; it stays pending and will be queued by the backfill.", comment.Id);
        }
        return await comments.GetAsync(comment.Id, ct) ?? comment;
    }

    private static string GuessLanguage(string text) => text.Count(c => c is >= '\u0600' and <= '\u06FF') * 3 > text.Count(char.IsLetter) ? "ar" : "en";

    /// <summary>Changes the text. The comment is labelled edited and goes back to moderation. An unchanged text changes nothing.</summary>
    public async Task<CommentResult> EditAsync(string userId, string commentId, string? newText, CancellationToken ct = default)
    {
        var comment = await comments.GetAsync(commentId, ct);
        if (comment is null || comment.UserId != userId || comment.Status == CommentStatus.Deleted) return new(CommentFailure.NotFound);

        var text = CleanText(newText);
        if (text is null) return new(CommentFailure.InvalidText);
        if (text == comment.Text) return new(CommentFailure.None, comment);

        var now = time.GetUtcNow().UtcDateTime;
        comment.PreviousText = comment.Text;
        comment.Revision++;
        comment.Text = text;
        comment.EditedAt = now;
        comment.UpdatedAt = now;
        comment.Status = CommentStatus.PendingModeration;
        await comments.ReplaceAsync(comment, ct);
        return new(CommentFailure.None, await ScreenAsync(comment, ct));
    }

    public async Task<CommentResult> SetAnonymityAsync(string userId, string commentId, bool isAnonymous, CancellationToken ct = default)
    {
        var comment = await comments.GetAsync(commentId, ct);
        if (comment is null || comment.UserId != userId) return new(CommentFailure.NotFound);
        if (comment.IsAnonymous != isAnonymous)
        {
            comment.IsAnonymous = isAnonymous; // not a content change: no edited label, no new moderation
            await comments.ReplaceAsync(comment, ct);
        }
        return new(CommentFailure.None, comment);
    }

    public async Task<CommentResult> DeleteAsync(string userId, string commentId, CancellationToken ct = default)
    {
        var comment = await comments.GetAsync(commentId, ct);
        if (comment is null || comment.UserId != userId) return new(CommentFailure.NotFound);
        if (comment.Status != CommentStatus.Deleted)
        {
            comment.Status = CommentStatus.Deleted;
            comment.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await comments.ReplaceAsync(comment, ct);
        }
        return new(CommentFailure.None, comment);
    }

    private string? CleanText(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > Opt.MaxLength) return null;
        return t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? null : t;
    }
}
