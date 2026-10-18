using Microsoft.Extensions.Options;
using Ratable.Modules.Media.Data;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Processing;
using Ratable.Modules.Media.Storage;
using Ratable.Shared.Content;
using Ratable.Shared.Rewards;

namespace Ratable.Modules.Media.Services;

public sealed record CreateMediaRequest(string? Kind, string? ContentType, long? SizeBytes);

public enum MediaFailure
{
    None, ReviewNotFound, InvalidKind, InvalidType, InvalidSize, TooLarge, LimitReached, RateLimited, NotFound, WrongState, OffsetMismatch, WrongFileType, InvalidImage, InvalidCover,
}

public sealed record MediaResult(MediaFailure Failure, MediaItem? Item = null, long ReceivedBytes = 0, string? UploadToken = null, DateTime? ExpiresAt = null)
{
    public bool Ok => Failure == MediaFailure.None;
}

/// <summary>The author's side of media: ask for an upload address, send the file in pieces (and resume), see the state, choose a cover, delete.</summary>
public sealed class MediaService(
    MediaRepository items, IMediaStorage storage, UploadTokens tokens, IEnumerable<IContentDirectory> directories, IOptions<MediaOptions> options, TimeProvider time,
    IEnumerable<IContributionRecorder> recorders)
{
    private MediaOptions Opt => options.Value;

    public int ChunkBytes => Opt.ChunkBytes;
    private IContentDirectory ReviewDirectory => directories.First(d => d.Kind == ContentKind.Review);

    public async Task<MediaResult> CreateAsync(string userId, string reviewId, CreateMediaRequest request, CancellationToken ct = default)
    {
        var review = await ReviewDirectory.GetAsync(reviewId, ct);
        // A review that is not yours looks the same as one that does not exist.
        if (review is null || review.IsDeleted || review.AuthorId != userId) return new(MediaFailure.ReviewNotFound);

        if (!Enum.TryParse<MediaKind>(request.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) return new(MediaFailure.InvalidKind);
        var contentType = (request.ContentType ?? "").Trim().ToLowerInvariant();
        if (!FileTypes.IsAllowedClaim(kind, contentType)) return new(MediaFailure.InvalidType);
        if (request.SizeBytes is not > 0) return new(MediaFailure.InvalidSize);
        if (request.SizeBytes > (kind == MediaKind.Video ? Opt.MaxVideoBytes : Opt.MaxPhotoBytes)) return new(MediaFailure.TooLarge);

        var limit = kind == MediaKind.Video ? Opt.MaxVideosPerReview : Opt.MaxPhotosPerReview;
        if (await items.CountActiveAsync(reviewId, kind, ct) >= limit) return new(MediaFailure.LimitReached);
        var now = time.GetUtcNow().UtcDateTime;
        if (await items.CountCreatedByUserSinceAsync(userId, now.AddHours(-24), ct) >= Opt.MaxUploadsPerDay) return new(MediaFailure.RateLimited);

        var item = new MediaItem
        {
            ReviewId = reviewId,
            UserId = userId,
            Kind = kind,
            ContentType = contentType,
            DeclaredBytes = request.SizeBytes.Value,
            StorageKey = storage.NewKey(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await items.InsertAsync(item, ct);
        var expires = now.AddMinutes(Opt.UploadLinkMinutes);
        return new(MediaFailure.None, item, 0, tokens.Create(item.Id, expires), expires);
    }

    /// <summary>The upload state for a signed address: how many bytes are in, so a phone can resume from there.</summary>
    public async Task<MediaResult> UploadStatusAsync(string token, CancellationToken ct = default)
    {
        var item = await FromTokenAsync(token, ct);
        return item is null ? new(MediaFailure.NotFound) : new(MediaFailure.None, item, storage.UploadLength(item.StorageKey));
    }

    /// <summary>Adds one piece at the given offset. When the last byte arrives the file type is checked from its first bytes.</summary>
    public async Task<MediaResult> UploadPieceAsync(string token, long offset, long total, Stream piece, CancellationToken ct = default)
    {
        var item = await FromTokenAsync(token, ct);
        if (item is null) return new(MediaFailure.NotFound);
        if (item.Status != MediaStatus.Uploading || total != item.DeclaredBytes) return new(MediaFailure.WrongState, item, storage.UploadLength(item.StorageKey));

        long received;
        try
        {
            // The file may never be longer than what was declared (and declared sizes are capped), so a client cannot fill the disk.
            received = await storage.AppendUploadAsync(item.StorageKey, offset, piece, item.DeclaredBytes, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "offset_mismatch")
        {
            return new(MediaFailure.OffsetMismatch, item, storage.UploadLength(item.StorageKey));
        }
        catch (InvalidOperationException ex) when (ex.Message == "too_large")
        {
            await RejectAsync(item, "too_large", ct);
            return new(MediaFailure.TooLarge, item);
        }

        item.ReceivedBytes = received;
        if (received < item.DeclaredBytes)
        {
            await items.ReplaceAsync(item, ct);
            return new(MediaFailure.None, item, received);
        }
        return await FinishAsync(item, ct);
    }

    private async Task<MediaResult> FinishAsync(MediaItem item, CancellationToken ct)
    {
        var folder = storage.WorkDirectory(item.StorageKey);
        var upload = Path.Combine(folder, LocalDiskMediaStorage.UploadFile);
        var head = new byte[16];
        int read;
        await using (var file = File.OpenRead(upload)) read = await file.ReadAsync(head, ct);

        var detected = FileTypes.Sniff(head.AsSpan(0, read));
        var expectedKind = detected is null ? (MediaKind?)null : detected.StartsWith("video/") ? MediaKind.Video : MediaKind.Photo;
        if (detected is null || expectedKind != item.Kind || !FileTypes.Matches(item.ContentType, detected))
        {
            await RejectAsync(item, "wrong_file_type", ct);
            return new(MediaFailure.WrongFileType, item);
        }
        item.ContentType = detected;

        if (item.Kind == MediaKind.Photo)
        {
            var stripped = ImageMetadataStripper.Strip(detected, await File.ReadAllBytesAsync(upload, ct));
            if (stripped is null)
            {
                await RejectAsync(item, "invalid_image", ct);
                return new(MediaFailure.InvalidImage, item);
            }
            var name = $"image.{FileTypes.Extension(detected)}";
            await File.WriteAllBytesAsync(Path.Combine(folder, name), stripped, ct);
            File.Delete(upload); // the original, with its metadata, is not kept
            item.ImageFile = name;
        }

        item.Status = MediaStatus.Uploaded;
        await items.ReplaceAsync(item, ct);
        return new(MediaFailure.None, item, item.ReceivedBytes);
    }

    private async Task RejectAsync(MediaItem item, string reason, CancellationToken ct)
    {
        await storage.DeleteAllAsync(item.StorageKey, ct);
        item.Status = MediaStatus.Rejected;
        item.RejectionReason = reason;
        await items.ReplaceAsync(item, ct);
    }

    private async Task<MediaItem?> FromTokenAsync(string token, CancellationToken ct)
    {
        var id = tokens.Validate(token);
        return id is null ? null : await items.GetAsync(id, ct);
    }

    public Task<List<MediaItem>> MineAsync(string userId, string? reviewId, CancellationToken ct = default) => items.ListForUserAsync(userId, reviewId, ct);

    public async Task<MediaResult> DeleteAsync(string userId, string id, CancellationToken ct = default)
    {
        var item = await items.GetAsync(id, ct);
        if (item is null || item.UserId != userId) return new(MediaFailure.NotFound);
        await storage.DeleteAllAsync(item.StorageKey, ct);
        await items.DeleteAsync(item.Id, ct);
        foreach (var recorder in recorders) await recorder.ReverseReferenceAsync(item.Id, "media_removed", ct);
        return new(MediaFailure.None);
    }

    /// <summary>The author picks the cover of a video: the first frame, one of the candidates, or the automatic choice.</summary>
    public async Task<MediaResult> SetCoverAsync(string userId, string id, string? choice, int? frameIndex, CancellationToken ct = default)
    {
        var item = await items.GetAsync(id, ct);
        if (item is null || item.UserId != userId || item.Kind != MediaKind.Video) return new(MediaFailure.NotFound);
        if (item.CandidateFrames == 0 || item.Status is not (MediaStatus.PendingModeration or MediaStatus.Published)) return new(MediaFailure.WrongState, item);

        switch ((choice ?? "").ToLowerInvariant())
        {
            case "auto": item.Cover = CoverChoice.Auto; break;
            case "first": item.Cover = CoverChoice.First; break;
            case "frame" when frameIndex is >= 0 && frameIndex < item.CandidateFrames: item.Cover = CoverChoice.Frame; item.CoverFrameIndex = frameIndex.Value; break;
            default: return new(MediaFailure.InvalidCover, item);
        }
        MediaPipeline.ApplyCover(item, storage, storage.WorkDirectory(item.StorageKey));
        await items.ReplaceAsync(item, ct);
        return new(MediaFailure.None, item);
    }

    /// <summary>Called when a review is deleted: its media and files go with it.</summary>
    public async Task DeleteForReviewAsync(string reviewId, CancellationToken ct = default)
    {
        foreach (var item in await items.ListForReviewAsync(reviewId, ct))
        {
            await storage.DeleteAllAsync(item.StorageKey, ct);
            await items.DeleteAsync(item.Id, ct);
        }
    }
}
