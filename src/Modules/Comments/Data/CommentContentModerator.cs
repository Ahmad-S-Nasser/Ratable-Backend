using MongoDB.Driver;
using Ratable.Modules.Comments.Models;
using Ratable.Shared.Content;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Comments.Data;

/// <summary>How the Moderation module publishes, hides and reads comments.</summary>
public sealed class CommentContentModerator(CommentRepository comments, TimeProvider time) : IContentModerator
{
    public ContentKind Kind => ContentKind.Comment;

    public async Task<bool> ApplyAsync(string contentId, ModerationOutcome outcome, string? reason, CancellationToken ct = default)
    {
        var comment = await comments.GetAsync(contentId, ct);
        if (comment is null) return false;
        comment.Status = outcome == ModerationOutcome.Publish ? CommentStatus.Published : CommentStatus.Hidden;
        comment.ModerationReason = outcome == ModerationOutcome.Hide ? reason : null;
        comment.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await comments.ReplaceAsync(comment, ct);
        return true;
    }

    public Task<bool> SetUnderReviewAsync(string contentId, bool underReview, CancellationToken ct = default) => Task.FromResult(false);

    public async Task<ModerationContentView?> GetViewAsync(string contentId, CancellationToken ct = default)
    {
        var c = await comments.GetAsync(contentId, ct);
        return c is null ? null : new ModerationContentView(
            c.Id, ContentKind.Comment, c.Text, "", c.Revision, c.PreviousText, null, null, c.Status.ToString(), false, false, null, c.IsAnonymous, c.CreatedAt, c.UpdatedAt);
    }

    public Task<IReadOnlyList<PendingContent>> ListPendingAsync(int limit, CancellationToken ct = default) => comments.ListPendingAsync(limit, ct);
}
