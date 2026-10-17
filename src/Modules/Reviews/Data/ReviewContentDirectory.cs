using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Content;

namespace Ratable.Modules.Reviews.Data;

/// <summary>Lets the Moderation module (reports, the audited anonymous-author look-up) and the Comments module see the little they need about a review.</summary>
public sealed class ReviewContentDirectory(ReviewRepository reviews) : IContentDirectory
{
    public ContentKind Kind => ContentKind.Review;

    public async Task<ContentInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        var review = await reviews.GetAsync(id, ct);
        return review is null ? null : new ContentInfo(review.Id, review.UserId, review.IsAnonymous, review.Status == ReviewStatus.Published, review.CategoryId);
    }
}
