using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Services;
using Ratable.Shared.Auth;
using Ratable.Shared.Data;
using Ratable.Shared.Media;

namespace Ratable.Modules.Media.Endpoints;

public sealed record ErrorBody(string Error, string? Detail = null);
public sealed record CoverBody(string? Choice, int? FrameIndex);
public sealed record RejectMediaBody(string? Reason);

public sealed record UploadInfo(string MediaId, string UploadUrl, int ChunkBytes, DateTime ExpiresAt);
public sealed record UploadProgress(string MediaId, string Status, long ReceivedBytes, long DeclaredBytes, string? Error = null);

/// <summary>The author's view of their media: the state and what to do next. Analysis details are not shown (they could teach how to get past the checks).</summary>
public sealed record OwnMedia(
    string Id, string ReviewId, string Kind, string Status, string? RejectionReason, string? ModeratorReason, string? FailureReason, long ReceivedBytes, long DeclaredBytes,
    double? DurationSeconds, int CandidateFrames, string Cover, int CoverFrameIndex, bool BlurApplied, bool VoiceChanged, string? PreviewPosterUrl);

/// <summary>A media item in the moderator queue, with what the automatic analysis found.</summary>
public sealed record QueueMedia(
    string Id, string ReviewId, string Kind, double? DurationSeconds, string AnalysisStatus, int FacesFound, int ChildrenFound, int PlatesFound, bool Nudity, bool Violence,
    bool PersonalDataOnScreen, bool AbusiveSpeech, bool PersonalDataInSpeech, double? QualityScore, List<string> Concerns, bool BlurApplied, bool VoiceChanged,
    bool ProtectedForAnonymous, int CandidateFrames, string? PosterUrl, string? PlaylistUrl, string? ImageUrl, LocalizedText? Captions, DateTime CreatedAt);

public static partial class MediaEndpoints
{
    [GeneratedRegex(@"^bytes (\d+)-(\d+)/(\d+)$")]
    private static partial Regex ContentRange();

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // The author: ask for an upload address, send the pieces, see the state.
        app.MapPost("/api/reviews/{reviewId}/media", Create).WithTags("Media").RequireAuthorization(PolicyNames.SignedIn)
            .Produces<UploadInfo>(201).Produces<ErrorBody>(400).Produces<ErrorBody>(404).Produces<ErrorBody>(409).Produces<ErrorBody>(429);
        app.MapGet("/api/me/media", Mine).WithTags("Media").RequireAuthorization(PolicyNames.SignedIn).Produces<List<OwnMedia>>();
        app.MapDelete("/api/me/media/{id}", DeleteMine).WithTags("Media").RequireAuthorization(PolicyNames.SignedIn).Produces(204).Produces(404);
        app.MapPut("/api/me/media/{id}/cover", SetCover).WithTags("Media").RequireAuthorization(PolicyNames.SignedIn).Produces<OwnMedia>().Produces<ErrorBody>(400).Produces(404).Produces<ErrorBody>(409);
        app.MapGet("/api/me/media/{id}/preview/{file}", OwnPreview).WithTags("Media").RequireAuthorization(PolicyNames.SignedIn).Produces(200, contentType: "image/jpeg").Produces(404);

        // The signed address: it carries its own permission, like a signed object-store URL, so it needs no cookie.
        app.MapGet("/api/media/upload/{token}", UploadStatus).WithTags("Media").Produces<UploadProgress>().Produces(404);
        app.MapPut("/api/media/upload/{token}", UploadPiece).WithTags("Media").DisableAntiforgery().Produces<UploadProgress>().Produces<UploadProgress>(409).Produces<ErrorBody>(400).Produces(404);

        // The public: only approved media of public reviews.
        app.MapGet("/api/media/feed", Feed).WithTags("Media").Produces<List<FeedItem>>();
        app.MapGet("/api/media/{id}/poster", (string id, MediaPublicService service, CancellationToken ct) => Public(service, id, "poster", null, ct)).WithTags("Media").Produces(200, contentType: "image/jpeg").Produces(404);
        app.MapGet("/api/media/{id}/image", (string id, MediaPublicService service, CancellationToken ct) => Public(service, id, "image", null, ct)).WithTags("Media").Produces(200, contentType: "image/jpeg").Produces(404);
        app.MapGet("/api/media/{id}/stream/{file}", (string id, string file, MediaPublicService service, CancellationToken ct) => Public(service, id, "stream", file, ct)).WithTags("Media").Produces(200).Produces(404);

        var queue = app.MapGroup("/api/moderation/media").WithTags("Moderation").RequireAuthorization(PolicyNames.Moderator);
        queue.MapGet("", ListQueue).Produces<List<QueueMedia>>();
        queue.MapGet("/{id}/file/{**file}", OpenPreview).Produces(200).Produces(404);
        queue.MapPost("/{id}/approve", Approve).Produces<QueueMedia>().Produces<ErrorBody>(404).Produces<ErrorBody>(409);
        queue.MapPost("/{id}/reject", Reject).Produces<QueueMedia>().Produces<ErrorBody>(400).Produces<ErrorBody>(404).Produces<ErrorBody>(409);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static async Task<IResult> Create(string reviewId, CreateMediaRequest body, ClaimsPrincipal user, MediaService service, CancellationToken ct)
    {
        var result = await service.CreateAsync(UserId(user), reviewId, body, ct);
        return result.Failure switch
        {
            MediaFailure.None => Results.Created($"/api/me/media?reviewId={reviewId}", new UploadInfo(result.Item!.Id, $"/api/media/upload/{result.UploadToken}", service.ChunkBytes, result.ExpiresAt!.Value)),
            MediaFailure.ReviewNotFound => Results.NotFound(new ErrorBody("review_not_found")),
            MediaFailure.InvalidKind => Results.BadRequest(new ErrorBody("invalid_kind")),
            MediaFailure.InvalidType => Results.BadRequest(new ErrorBody("invalid_type")),
            MediaFailure.InvalidSize => Results.BadRequest(new ErrorBody("invalid_size")),
            MediaFailure.TooLarge => Results.BadRequest(new ErrorBody("too_large")),
            MediaFailure.LimitReached => Results.Conflict(new ErrorBody("limit_reached")),
            _ => Results.Json(new ErrorBody("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests),
        };
    }

    private static OwnMedia ToOwn(MediaItem m) => new(
        m.Id, m.ReviewId, m.Kind.ToString(), m.Status.ToString(), m.RejectionReason, m.ModeratorReason, m.FailureReason, m.ReceivedBytes, m.DeclaredBytes, m.DurationSeconds,
        m.CandidateFrames, m.Cover.ToString(), m.CoverFrameIndex, m.BlurApplied, m.VoiceChanged,
        m.PosterFile is not null && m.Status is MediaStatus.PendingModeration or MediaStatus.Published ? $"/api/me/media/{m.Id}/preview/{m.PosterFile}" : null);

    private static async Task<IResult> Mine(string? reviewId, ClaimsPrincipal user, MediaService service, CancellationToken ct) =>
        Results.Ok((await service.MineAsync(UserId(user), reviewId, ct)).Select(ToOwn));

    private static async Task<IResult> DeleteMine(string id, ClaimsPrincipal user, MediaService service, CancellationToken ct) =>
        (await service.DeleteAsync(UserId(user), id, ct)).Ok ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> SetCover(string id, CoverBody body, ClaimsPrincipal user, MediaService service, CancellationToken ct)
    {
        var result = await service.SetCoverAsync(UserId(user), id, body.Choice, body.FrameIndex, ct);
        return result.Failure switch
        {
            MediaFailure.None => Results.Ok(ToOwn(result.Item!)),
            MediaFailure.InvalidCover => Results.BadRequest(new ErrorBody("invalid_cover")),
            MediaFailure.WrongState => Results.Conflict(new ErrorBody("wrong_state")),
            _ => Results.NotFound(),
        };
    }

    /// <summary>The author may look at their own protected cover candidates (never the original upload).</summary>
    private static async Task<IResult> OwnPreview(string id, string file, ClaimsPrincipal user, MediaService service, Ratable.Modules.Media.Storage.IMediaStorage storage, CancellationToken ct)
    {
        var own = (await service.MineAsync(UserId(user), null, ct)).FirstOrDefault(m => m.Id == id);
        if (own is null || !PosterName().IsMatch(file)) return Results.NotFound();
        var stream = storage.OpenRead(own.StorageKey, file);
        return stream is null ? Results.NotFound() : Results.Stream(stream, "image/jpeg");
    }

    [GeneratedRegex(@"^(poster(_[0-9])?\.jpg|image\.[a-z0-9]+)$")]
    private static partial Regex PosterName();

    private static async Task<IResult> UploadStatus(string token, MediaService service, CancellationToken ct)
    {
        var result = await service.UploadStatusAsync(token, ct);
        return result.Ok ? Results.Ok(Progress(result)) : Results.NotFound();
    }

    private static UploadProgress Progress(MediaResult r, string? error = null) =>
        new(r.Item!.Id, r.Item.Status.ToString(), r.ReceivedBytes, r.Item.DeclaredBytes, error);

    private static async Task<IResult> UploadPiece(string token, HttpRequest request, MediaService service, IOptions<MediaOptions> options, CancellationToken ct)
    {
        var header = request.Headers.ContentRange.ToString();
        var match = ContentRange().Match(header);
        if (!match.Success) return Results.BadRequest(new ErrorBody("content_range_required", "Send Content-Range: bytes start-end/total"));
        var start = long.Parse(match.Groups[1].Value);
        var end = long.Parse(match.Groups[2].Value);
        var total = long.Parse(match.Groups[3].Value);
        if (end < start || end >= total || end - start + 1 > options.Value.ChunkBytes * 2L) return Results.BadRequest(new ErrorBody("invalid_range"));

        var result = await service.UploadPieceAsync(token, start, total, request.Body, ct);
        return result.Failure switch
        {
            MediaFailure.None => Results.Ok(Progress(result)),
            MediaFailure.NotFound => Results.NotFound(),
            // The client resumes from the byte count in the answer.
            MediaFailure.OffsetMismatch or MediaFailure.WrongState => Results.Json(Progress(result, result.Failure == MediaFailure.WrongState ? "wrong_state" : "offset_mismatch"), statusCode: StatusCodes.Status409Conflict),
            MediaFailure.TooLarge => Results.BadRequest(new ErrorBody("too_large")),
            MediaFailure.WrongFileType => Results.BadRequest(new ErrorBody("wrong_file_type")),
            _ => Results.BadRequest(new ErrorBody("invalid_image")),
        };
    }

    private static async Task<IResult> Public(MediaPublicService service, string id, string kind, string? file, CancellationToken ct)
    {
        var found = await service.OpenAsync(id, kind, file, ct);
        if (found is null) return Results.NotFound();
        // Approved media does not change, so it can be cached by the browser and a CDN in front of the API.
        return Results.Stream(found.Content, found.ContentType, enableRangeProcessing: true);
    }

    private static async Task<IResult> Feed(int? limit, int? skip, MediaPublicService service, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Ok(await service.FeedAsync(Math.Clamp(limit ?? 10, 1, 30), Math.Max(0, skip ?? 0), ct));
    }

    private static QueueMedia ToQueue(MediaItem m) => new(
        m.Id, m.ReviewId, m.Kind.ToString(), m.DurationSeconds, m.Analysis.Status.ToString(), m.Analysis.FacesFound, m.Analysis.ChildrenFound, m.Analysis.PlatesFound,
        m.Analysis.Nudity, m.Analysis.Violence, m.Analysis.PersonalDataOnScreen, m.Analysis.AbusiveSpeech, m.Analysis.PersonalDataInSpeech, m.Analysis.QualityScore, m.Analysis.Concerns,
        m.BlurApplied, m.VoiceChanged, m.ProtectedForAnonymous, m.CandidateFrames,
        m.PosterFile is null ? null : $"/api/moderation/media/{m.Id}/file/{m.PosterFile}",
        m.HasStream ? $"/api/moderation/media/{m.Id}/file/stream/index.m3u8" : null,
        m.Kind == MediaKind.Photo && m.ImageFile is not null ? $"/api/moderation/media/{m.Id}/file/{m.ImageFile}" : null,
        m.Captions, m.CreatedAt);

    private static async Task<IResult> ListQueue(MediaModerationService service, CancellationToken ct) => Results.Ok((await service.QueueAsync(ct)).Select(ToQueue));

    private static async Task<IResult> OpenPreview(string id, string file, ClaimsPrincipal user, MediaModerationService service, CancellationToken ct)
    {
        var (result, found) = await service.OpenPreviewAsync(UserId(user), id, file, ct);
        return result.Ok && found is not null ? Results.Stream(found.Content, found.ContentType, enableRangeProcessing: true) : Results.NotFound();
    }

    private static IResult Decision(ModerationResult r) => r.Failure switch
    {
        ModerationFailure.None => Results.Ok(ToQueue(r.Item!)),
        ModerationFailure.NotFound => Results.NotFound(new ErrorBody("media_not_found")),
        ModerationFailure.ReasonRequired => Results.BadRequest(new ErrorBody("reason_required")),
        ModerationFailure.AnalysisRequired => Results.Conflict(new ErrorBody("analysis_required")),
        ModerationFailure.NotProtectedForAnonymous => Results.Conflict(new ErrorBody("not_protected_for_anonymous")),
        ModerationFailure.FacesOrPlatesVisible => Results.Conflict(new ErrorBody("faces_or_plates_visible")),
        _ => Results.Conflict(new ErrorBody("wrong_state")),
    };

    private static async Task<IResult> Approve(string id, ClaimsPrincipal user, MediaModerationService service, CancellationToken ct) => Decision(await service.ApproveAsync(UserId(user), id, ct));

    private static async Task<IResult> Reject(string id, RejectMediaBody body, ClaimsPrincipal user, MediaModerationService service, CancellationToken ct) =>
        Decision(await service.RejectAsync(UserId(user), id, body.Reason, ct));
}
