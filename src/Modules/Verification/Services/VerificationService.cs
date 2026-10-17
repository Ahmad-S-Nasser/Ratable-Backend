using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Ratable.Modules.Verification.Data;
using Ratable.Modules.Verification.Models;
using Ratable.Modules.Verification.Storage;
using Ratable.Shared.Audit;
using Ratable.Shared.Catalog;
using Ratable.Shared.Content;
using Ratable.Shared.Verification;

namespace Ratable.Modules.Verification.Services;

/// <summary>Decides which approved proofs a person also checks. Replaced in tests so the 5 percent is predictable.</summary>
public interface IProofSampler
{
    bool ShouldAudit(double rate);
}

public sealed class RandomProofSampler : IProofSampler
{
    public bool ShouldAudit(double rate) => rate > 0 && RandomNumberGenerator.GetInt32(0, 1_000_000) < rate * 1_000_000;
}

public sealed record SubmitProofRequest(string? ProofType, byte[]? File, bool Keep, bool KeepConsent, bool DeclaresNoRelationship);

public enum ProofFailure
{
    None, ReviewNotFound, InvalidProofType, UseAppForPresence, DeclarationRequired, ConsentRequired, InvalidFile,
    AlreadyPending, AlreadyVerified, TooManyAttempts, RateLimited, NotFound, WrongState, ReasonRequired, FileNotAvailable,
}

public sealed record ProofResult(ProofFailure Failure, VerificationRecord? Record = null)
{
    public bool Ok => Failure == ProofFailure.None;
}

/// <summary>
/// Evidence (CLAUDE.md rules 3 and 5). A review becomes Verified only through an accepted proof. A proof file lives in private storage, is deleted after
/// the decision unless the author chose to keep it (with a separate consent), and is never shown to other users or businesses. The record that stays
/// holds the proof type, the date, the result and a one-way hash.
/// </summary>
public sealed class VerificationService(
    VerificationRepository records,
    ConsentRepository consents,
    IProofStorage storage,
    ProofChecker checker,
    IProofSampler sampler,
    IEnumerable<IContentDirectory> directories,
    IEntityCatalog catalog,
    IReviewVerifier reviewVerifier,
    IAuditLog audit,
    IOptions<VerificationOptions> options,
    TimeProvider time)
{
    private VerificationOptions Opt => options.Value;

    // ---- the author uploads a proof ----

    public async Task<ProofResult> SubmitAsync(string userId, string reviewId, SubmitProofRequest request, CancellationToken ct = default)
    {
        var review = await directories.First(d => d.Kind == ContentKind.Review).GetAsync(reviewId, ct);
        if (review is null || review.AuthorId != userId || review.IsDeleted || review.EntityId is null || review.CategoryId is null) return new(ProofFailure.ReviewNotFound);
        var entity = await catalog.GetActiveEntityAsync(review.EntityId, ct);
        var category = await catalog.GetCategoryAsync(review.CategoryId, ct);
        if (entity is null || category is null) return new(ProofFailure.ReviewNotFound);

        var proofType = request.ProofType?.Trim();
        if (proofType == Opt.PresenceProofType) return new(ProofFailure.UseAppForPresence);
        if (proofType is null || !category.AcceptedProofTypes.Contains(proofType)) return new(ProofFailure.InvalidProofType);
        if (!request.DeclaresNoRelationship) return new(ProofFailure.DeclarationRequired);
        if (request.Keep && !request.KeepConsent) return new(ProofFailure.ConsentRequired);

        var file = request.File;
        var contentType = file is null ? null : ProofFileInspector.ContentTypeOf(file);
        if (file is null || file.Length == 0 || file.Length > Opt.MaxFileBytes || contentType is null) return new(ProofFailure.InvalidFile);

        var existing = await records.ListForReviewAsync(reviewId, ct);
        if (existing.Any(r => r.Result is VerificationResult.Approved)) return new(ProofFailure.AlreadyVerified);
        if (existing.Any(r => r.Result is VerificationResult.Pending or VerificationResult.Flagged)) return new(ProofFailure.AlreadyPending);
        if (existing.Count(r => r.Type == VerificationType.Proof) >= Opt.MaxAttemptsPerReview) return new(ProofFailure.TooManyAttempts);

        var now = time.GetUtcNow().UtcDateTime;
        if (await records.CountProofsByUserSinceAsync(userId, now.AddDays(-1), ct) >= Opt.MaxUploadsPerDay) return new(ProofFailure.RateLimited);

        var record = new VerificationRecord
        {
            UserId = userId,
            ReviewId = reviewId,
            EntityId = entity.Id,
            Type = VerificationType.Proof,
            ProofType = proofType,
            Result = VerificationResult.Pending,
            ProofHash = Convert.ToHexString(SHA256.HashData(file)),
            StorageKey = LocalDiskProofStorage.NewKey(),
            ContentType = contentType,
            DeclaredAt = now,
            CreatedAt = now,
            Retention = new ProofRetention
            {
                Mode = request.Keep ? RetentionMode.Keep : RetentionMode.Delete,
                ExpiresAt = request.Keep ? now.AddMonths(Opt.KeepMonths) : null,
            },
        };
        await storage.SaveAsync(record.StorageKey, file, ct);
        await records.InsertAsync(record, ct);
        if (request.Keep)
            await consents.InsertAsync(new ConsentRecord { UserId = userId, VerificationId = record.Id, GrantedAt = now }, ct);

        var assessment = await checker.AssessAsync(record.Id, entity, category, proofType, file, contentType, record.ProofHash!, now, ct);
        record.Reading = assessment.Reading;
        record.Reasons = assessment.Reasons;

        if (assessment.Clean)
        {
            record.Result = VerificationResult.Approved;
            record.DecidedAt = now;
            record.DecidedBy = "auto";
            record.AuditSampled = sampler.ShouldAudit(Opt.AuditSampleRate);
            record.AuditPending = record.AuditSampled;
            await reviewVerifier.SetVerifiedAsync(reviewId, proofType, now, ct);
        }
        else
        {
            record.Result = VerificationResult.Flagged; // a person decides within 48 hours; the review stays Unverified meanwhile
        }

        await records.ReplaceAsync(record, ct);
        await DeleteFileIfDueAsync(record, now, ct);
        return new(ProofFailure.None, record);
    }

    // ---- a moderator decides ----

    /// <summary>
    /// Approves or rejects a proof that is flagged, or confirms/overturns one picked for the random audit. A rejection needs a reason, which the author
    /// is shown. Overturning an audited proof takes the Verified level back from the review.
    /// </summary>
    public async Task<ProofResult> DecideAsync(string moderatorId, string verificationId, bool approve, string? reason, CancellationToken ct = default)
    {
        var record = await records.GetAsync(verificationId, ct);
        if (record is null || record.Type != VerificationType.Proof) return new(ProofFailure.NotFound);
        var flagged = record.Result == VerificationResult.Flagged;
        if (!flagged && !record.AuditPending) return new(ProofFailure.WrongState);
        var why = reason?.Trim();
        if (!approve && (string.IsNullOrEmpty(why) || why.Length > 500)) return new(ProofFailure.ReasonRequired);

        var now = time.GetUtcNow().UtcDateTime;
        string action;
        if (approve)
        {
            if (flagged)
            {
                record.Result = VerificationResult.Approved;
                record.DecidedAt = now;
                record.DecidedBy = moderatorId;
                await reviewVerifier.SetVerifiedAsync(record.ReviewId!, record.ProofType!, now, ct);
                action = "proof.approved";
            }
            else
            {
                action = "proof.audit_confirmed";
            }
        }
        else
        {
            record.Result = VerificationResult.Rejected;
            record.DecidedAt = now;
            record.DecidedBy = moderatorId;
            record.RejectionReason = why;
            if (!flagged) await reviewVerifier.SetUnverifiedAsync(record.ReviewId!, ct); // the audit found it was not valid after all
            action = flagged ? "proof.rejected" : "proof.audit_failed";
        }
        record.AuditPending = false;

        await records.ReplaceAsync(record, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, action, "verification", record.Id, why ?? "Decision from the proof queue"), ct);
        await DeleteFileIfDueAsync(record, now, ct);
        return new(ProofFailure.None, record);
    }

    /// <summary>A moderator opens the file. The audit entry is written first, so every view is on record; nobody else can read a proof.</summary>
    public async Task<(ProofResult Result, byte[]? Data, string? ContentType)> OpenAsync(string moderatorId, string verificationId, CancellationToken ct = default)
    {
        var record = await records.GetAsync(verificationId, ct);
        if (record is null || record.Type != VerificationType.Proof) return (new(ProofFailure.NotFound), null, null);
        if (record.StorageKey is null) return (new(ProofFailure.FileNotAvailable), null, null);

        await audit.AppendAsync(new AuditRecord(moderatorId, "proof.viewed", "verification", record.Id, "Opened the proof file"), ct);
        var data = await storage.ReadAsync(record.StorageKey, ct);
        return data is null ? (new(ProofFailure.FileNotAvailable), null, null) : (new(ProofFailure.None, record), data, record.ContentType);
    }

    // ---- the author's control over a kept file ----

    /// <summary>
    /// The author asks for their file to be deleted now. The record (type, date, result, hash) stays, so a review that was Verified stays Verified
    /// on the recorded result. A proof still waiting for a person cannot be deleted yet: they need the file to decide.
    /// </summary>
    public async Task<ProofResult> DeleteMyFileAsync(string userId, string verificationId, CancellationToken ct = default)
    {
        var record = await records.GetAsync(verificationId, ct);
        if (record is null || record.UserId != userId || record.Type != VerificationType.Proof) return new(ProofFailure.NotFound);
        if (record.Result is VerificationResult.Pending or VerificationResult.Flagged || record.AuditPending) return new(ProofFailure.WrongState);

        await DeleteFileAsync(record, time.GetUtcNow().UtcDateTime, ct);
        return new(ProofFailure.None, record);
    }

    /// <summary>Deletes every file that is due: authors who chose delete and their proof is decided, and kept files past their expiry date.</summary>
    public async Task<int> PurgeDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await records.ListDueForDeletionAsync(now, ct);
        foreach (var record in due) await DeleteFileAsync(record, now, ct);
        return due.Count;
    }

    /// <summary>When a review is deleted, any proof file kept for it is deleted too.</summary>
    public async Task DeleteFilesForReviewAsync(string reviewId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var record in await records.ListWithFilesForReviewAsync(reviewId, ct)) await DeleteFileAsync(record, now, ct);
    }

    public async Task<List<VerificationRecord>> QueueAsync(CancellationToken ct = default) => await records.ListQueueAsync(ct: ct);

    public async Task<List<VerificationRecord>> MineAsync(string userId, string? reviewId, CancellationToken ct = default) => await records.ListForUserAsync(userId, reviewId, ct);

    // ---- helpers ----

    private async Task DeleteFileIfDueAsync(VerificationRecord record, DateTime now, CancellationToken ct)
    {
        var decided = record.Result is VerificationResult.Approved or VerificationResult.Rejected && !record.AuditPending;
        if (decided && record.Retention.Mode == RetentionMode.Delete && record.StorageKey is not null) await DeleteFileAsync(record, now, ct);
    }

    private async Task DeleteFileAsync(VerificationRecord record, DateTime now, CancellationToken ct)
    {
        if (record.StorageKey is not null) await storage.DeleteAsync(record.StorageKey, ct);
        record.StorageKey = null;
        record.Retention.DeletedAt = now;
        await records.ReplaceAsync(record, ct);
        await consents.WithdrawAsync(record.Id, now, ct);
    }
}

/// <summary>Lets the Reviews module tell Verification a review was deleted, so kept proof files go with it.</summary>
public sealed class ProofCleanupOnReviewDeleted(VerificationService service) : IReviewDeletedHandler
{
    public Task OnReviewDeletedAsync(string reviewId, CancellationToken ct = default) => service.DeleteFilesForReviewAsync(reviewId, ct);
}
