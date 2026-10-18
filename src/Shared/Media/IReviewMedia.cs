using Ratable.Shared.Data;

namespace Ratable.Shared.Media;

/// <summary>
/// A photo or video of a review as everyone may see it. It carries no author, no file name and no upload details, and its addresses are the
/// API's own (the files live in private storage and are streamed only while the item is approved and its review is public).
/// </summary>
public sealed record PublicMediaItem(
    string Id, string Kind, double? DurationSeconds, int? Width, int? Height, string? PosterUrl, string? PlaylistUrl, string? ImageUrl,
    LocalizedText? Captions);

/// <summary>Implemented by the Media module. The Reviews module asks it for the approved media of the reviews it is about to show.</summary>
public interface IReviewMedia
{
    /// <summary>
    /// The approved media for each review. A video that was not protected for an anonymous author is never returned for an anonymous review,
    /// even if it was approved earlier (the author may have switched to anonymous since).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<PublicMediaItem>>> GetPublicAsync(
        IReadOnlyCollection<(string ReviewId, bool IsAnonymous)> reviews, CancellationToken cancellationToken = default);
}
