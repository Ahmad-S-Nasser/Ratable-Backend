using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ratable.Modules.Verification.Models;
using Ratable.Modules.Verification.Services;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Verification.Endpoints;

public sealed record ErrorBody(string Error);

/// <summary>The author's view of a proof. Automatic flag reasons are not shown (they would teach how to get past the checks); a person's rejection message is.</summary>
public sealed record OwnProof(
    string Id, string ReviewId, string ProofType, string Status, string? RejectionReason, DateTime CreatedAt, DateTime? DecidedAt,
    string FileRetention, bool FileStored, DateTime? FileExpiresAt);

/// <summary>A proof in the moderator queue. The file hash and storage key are never part of it.</summary>
public sealed record QueueProof(
    string Id, string ReviewId, string ProofType, string Kind, List<string> Reasons, string? DocumentType, string? ReadEntityName,
    string? ReadDate, double? Confidence, bool FileStored, DateTime CreatedAt);

public sealed record RejectProofBody(string? Reason);

public static class VerificationEndpoints
{
    public static IEndpointRouteBuilder MapVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        // The upload is a multipart form posted with the session cookie (SameSite=Lax, so other sites cannot post it). Anti-forgery tokens for web forms
        // are tracked as an open decision for the web build.
        app.MapPost("/api/reviews/{reviewId}/proof", Upload).WithTags("Verification").RequireAuthorization(PolicyNames.SignedIn).DisableAntiforgery();
        app.MapGet("/api/me/proofs", Mine).WithTags("Verification").RequireAuthorization(PolicyNames.SignedIn);
        app.MapDelete("/api/me/proofs/{id}/file", DeleteMyFile).WithTags("Verification").RequireAuthorization(PolicyNames.SignedIn);

        var queue = app.MapGroup("/api/moderation/proofs").WithTags("Moderation").RequireAuthorization(PolicyNames.Moderator);
        queue.MapGet("", ListQueue);
        queue.MapGet("/{id}/file", OpenFile);
        queue.MapPost("/{id}/approve", Approve);
        queue.MapPost("/{id}/reject", Reject);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static async Task<IResult> Upload(string reviewId, HttpRequest request, ClaimsPrincipal user, VerificationService service, IOptions<VerificationOptions> options, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new ErrorBody("multipart_required"));
        var form = await request.ReadFormAsync(ct);
        var upload = form.Files.GetFile("file");
        if (upload is null || upload.Length == 0 || upload.Length > options.Value.MaxFileBytes) return Results.BadRequest(new ErrorBody("invalid_file"));

        byte[] bytes;
        await using (var stream = upload.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        var retention = form["retention"].ToString().ToLowerInvariant();
        if (retention is not ("" or "delete" or "keep")) return Results.BadRequest(new ErrorBody("invalid_retention"));
        static bool Flag(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

        var result = await service.SubmitAsync(UserId(user), reviewId,
            new SubmitProofRequest(form["proofType"].ToString(), bytes, retention == "keep", Flag(form["keepConsent"].ToString()), Flag(form["declaration"].ToString())), ct);

        return result.Failure switch
        {
            ProofFailure.None => Results.Created("/api/me/proofs?reviewId=" + reviewId, ToOwn(result.Record!)),
            ProofFailure.ReviewNotFound => Results.NotFound(new ErrorBody("review_not_found")),
            ProofFailure.InvalidProofType => Results.BadRequest(new ErrorBody("invalid_proof_type")),
            ProofFailure.UseAppForPresence => Results.BadRequest(new ErrorBody("use_app_for_presence_proof")),
            ProofFailure.DeclarationRequired => Results.BadRequest(new ErrorBody("declaration_required")),
            ProofFailure.ConsentRequired => Results.BadRequest(new ErrorBody("keep_consent_required")),
            ProofFailure.InvalidFile => Results.BadRequest(new ErrorBody("invalid_file")),
            ProofFailure.AlreadyPending => Results.Conflict(new ErrorBody("proof_already_pending")),
            ProofFailure.AlreadyVerified => Results.Conflict(new ErrorBody("already_verified")),
            ProofFailure.TooManyAttempts => Results.Conflict(new ErrorBody("too_many_attempts")),
            _ => Results.Json(new ErrorBody("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests),
        };
    }

    private static async Task<IResult> Mine(string? reviewId, ClaimsPrincipal user, VerificationService service, CancellationToken ct) =>
        Results.Ok((await service.MineAsync(UserId(user), reviewId, ct)).Select(ToOwn));

    private static async Task<IResult> DeleteMyFile(string id, ClaimsPrincipal user, VerificationService service, CancellationToken ct)
    {
        var result = await service.DeleteMyFileAsync(UserId(user), id, ct);
        return result.Failure switch
        {
            ProofFailure.None => Results.NoContent(),
            ProofFailure.WrongState => Results.Conflict(new ErrorBody("proof_not_decided_yet")),
            _ => Results.NotFound(new ErrorBody("proof_not_found")),
        };
    }

    private static async Task<IResult> ListQueue(VerificationService service, CancellationToken ct) =>
        Results.Ok((await service.QueueAsync(ct)).Select(v => new QueueProof(
            v.Id, v.ReviewId!, v.ProofType!, v.Result == VerificationResult.Flagged ? "flagged" : "audit", v.Reasons,
            v.Reading?.DocumentType, v.Reading?.EntityName, v.Reading?.DocumentDate, v.Reading?.Confidence, v.StorageKey is not null, v.CreatedAt)));

    private static async Task<IResult> OpenFile(string id, ClaimsPrincipal user, HttpContext http, VerificationService service, CancellationToken ct)
    {
        var (result, data, contentType) = await service.OpenAsync(UserId(user), id, ct);
        if (!result.Ok) return Results.NotFound(new ErrorBody("file_not_available"));
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(data!, contentType ?? "application/octet-stream");
    }

    private static async Task<IResult> Approve(string id, ClaimsPrincipal user, VerificationService service, CancellationToken ct) =>
        ToDecision(await service.DecideAsync(UserId(user), id, approve: true, reason: null, ct));

    private static async Task<IResult> Reject(string id, RejectProofBody body, ClaimsPrincipal user, VerificationService service, CancellationToken ct) =>
        ToDecision(await service.DecideAsync(UserId(user), id, approve: false, body.Reason, ct));

    private static IResult ToDecision(ProofResult r) => r.Failure switch
    {
        ProofFailure.None => Results.Ok(ToOwn(r.Record!)),
        ProofFailure.NotFound => Results.NotFound(new ErrorBody("proof_not_found")),
        ProofFailure.WrongState => Results.Conflict(new ErrorBody("wrong_state")),
        _ => Results.BadRequest(new ErrorBody("reason_required")),
    };

    private static OwnProof ToOwn(VerificationRecord v) => new(
        v.Id, v.ReviewId ?? "", v.ProofType ?? "",
        v.Result is VerificationResult.Pending or VerificationResult.Flagged or VerificationResult.Deferred ? "UnderReview" : v.Result.ToString(),
        v.Result == VerificationResult.Rejected ? v.RejectionReason : null,
        v.CreatedAt, v.DecidedAt, v.Retention.Mode.ToString(), v.StorageKey is not null, v.Retention.Mode == RetentionMode.Keep ? v.Retention.ExpiresAt : null);
}
