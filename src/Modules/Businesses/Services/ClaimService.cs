using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Businesses.Models;
using Ratable.Shared.Audit;
using Ratable.Shared.Businesses;
using Ratable.Shared.Catalog;
using Ratable.Shared.Moderation;
using Ratable.Shared.Storage;

namespace Ratable.Modules.Businesses.Services;

public enum ClaimFailure
{
    None, NotFound, PhoneNotVerified, EntityNotFound, AlreadyOwned, AlreadyOwnerHere, ClaimOpen, TooManyClaims, InvalidEmail, InvalidRole, InvalidDocumentType,
    InvalidDocument, DocumentTooLarge, WrongState, ReasonRequired, DocumentMissing, OwnerConflict,
    EmailNotVerified, InvalidCode, CodeExpired, TooManyAttempts, EmailUnavailable, TooSoon,
}

public sealed record ClaimInput(string? CompanyEmail, string? Role, string? Website, string? Note);

public sealed record ClaimResult(ClaimFailure Failure, BusinessClaim? Claim = null)
{
    public bool Ok => Failure == ClaimFailure.None;
}

/// <summary>
/// Claiming a place. Needs a verified phone (CLAUDE.md rule 18) and a company email, then a document (commercial register, tax card or licence) that a moderator
/// reads once. The document lives in private storage, is deleted after the decision, and only its hash is kept (rule 5). Approval makes the account the place's
/// owner: it unlocks the dashboard and replies and nothing else. It cannot change a score, a ranking or a removal (rule 6).
/// </summary>
public sealed class ClaimService(
    ClaimRepository claims,
    OwnershipRepository ownerships,
    IEntityCatalog catalog,
    IAccountSignals accounts,
    IBusinessRoleGranter roles,
    IPrivateFileStore files,
    IEmailSender email,
    IAuditLog audit,
    IOptions<BusinessesOptions> options,
    TimeProvider time)
{
    private static readonly HashSet<string> Roles = ["owner", "manager", "representative"];
    private static readonly HashSet<string> DocumentTypes = ["commercial_register", "tax_card", "license", "other"];

    private BusinessesOptions Opt => options.Value;

    private const int MaxCodeAttempts = 5;
    private const int MaxCodeSends = 5;
    private static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResendGap = TimeSpan.FromSeconds(60);

    public async Task<ClaimResult> CreateAsync(string userId, string entityId, ClaimInput input, CancellationToken ct = default)
    {
        if (!(await accounts.GetAsync(userId, ct)).PhoneVerified) return new(ClaimFailure.PhoneNotVerified);

        var email = NormalizeEmail(input.CompanyEmail);
        if (email is null) return new(ClaimFailure.InvalidEmail);
        var role = (input.Role ?? "owner").Trim().ToLowerInvariant();
        if (!Roles.Contains(role)) return new(ClaimFailure.InvalidRole);

        var entries = await catalog.GetActiveEntriesAsync([entityId], ct);
        var entry = entries.FirstOrDefault();
        if (entry is null) return new(ClaimFailure.EntityNotFound);

        if (await ownerships.ExistsAsync(userId, entityId, ct)) return new(ClaimFailure.AlreadyOwnerHere);
        if (await ownerships.GetForEntityAsync(entityId, ct) is not null) return new(ClaimFailure.AlreadyOwned);
        if (await claims.HasOpenForEntityAsync(userId, entityId, ct)) return new(ClaimFailure.ClaimOpen);

        var now = time.GetUtcNow().UtcDateTime;
        if (await claims.CountOpenAsync(userId, ct) >= Opt.MaxOpenClaimsPerUser || await claims.CountSinceAsync(userId, now.AddDays(-1), ct) >= Opt.MaxClaimsPerUserPerDay)
            return new(ClaimFailure.TooManyClaims);

        var domain = email[(email.IndexOf('@') + 1)..];
        var claim = new BusinessClaim
        {
            UserId = userId,
            EntityId = entityId,
            CompanyEmail = email,
            EmailDomain = domain,
            Role = role,
            Website = Clean(input.Website, 200),
            Note = Clean(input.Note, 1000),
            FreeEmail = Opt.FreeEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase),
            DomainMatchesPlace = entry.Fields?.Values.Any(v => v.Contains(domain, StringComparison.OrdinalIgnoreCase)) == true,
            Status = ClaimStatus.Draft,
            CreatedAt = now,
        };
        // The code goes out before anything is stored, so a failed send leaves no half-made claim behind.
        if (await SendCodeAsync(claim, now, ct) is { } failed) return new(failed);
        await claims.InsertAsync(claim, ct);
        return new(ClaimFailure.None, claim);
    }

    /// <summary>Sends a new one-time code to the company email: single use, valid 10 minutes, kept only as a hash.</summary>
    private async Task<ClaimFailure?> SendCodeAsync(BusinessClaim claim, DateTime now, CancellationToken ct)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        try
        {
            await email.SendAsync(claim.CompanyEmail, "Ratable: confirm your company email",
                $"Your Ratable code is {code}. It works once and expires in 10 minutes. If you did not ask for it, ignore this message.", ct);
        }
        catch (EmailUnavailableException)
        {
            return ClaimFailure.EmailUnavailable;
        }
        claim.EmailCodeHash = HashCode(claim.Id, code);
        claim.EmailCodeExpiresAt = now + CodeLife;
        claim.EmailAttempts = 0;
        claim.EmailSends++;
        claim.EmailLastSentAt = now;
        return null;
    }

    private static string HashCode(string claimId, string code) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{claimId}:{code}")));

    public async Task<ClaimResult> ResendCodeAsync(string userId, string claimId, CancellationToken ct = default)
    {
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.UserId != userId) return new(ClaimFailure.NotFound);
        if (claim.Status != ClaimStatus.Draft || claim.EmailVerified) return new(ClaimFailure.WrongState);
        var now = time.GetUtcNow().UtcDateTime;
        if (claim.EmailSends >= MaxCodeSends) return new(ClaimFailure.TooManyClaims);
        if (claim.EmailLastSentAt is { } last && now - last < ResendGap) return new(ClaimFailure.TooSoon);
        if (await SendCodeAsync(claim, now, ct) is { } failed) return new(failed);
        await claims.ReplaceAsync(claim, ct);
        return new(ClaimFailure.None, claim);
    }

    /// <summary>Checks the code the claimant typed. Single use, 10 minutes, five wrong tries and the code is dead.</summary>
    public async Task<ClaimResult> ConfirmEmailAsync(string userId, string claimId, string? code, CancellationToken ct = default)
    {
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.UserId != userId) return new(ClaimFailure.NotFound);
        if (claim.Status != ClaimStatus.Draft) return new(ClaimFailure.WrongState);
        if (claim.EmailVerified) return new(ClaimFailure.None, claim);
        var now = time.GetUtcNow().UtcDateTime;
        if (claim.EmailCodeHash is null || claim.EmailCodeExpiresAt is null || claim.EmailCodeExpiresAt < now) return new(ClaimFailure.CodeExpired);
        if (claim.EmailAttempts >= MaxCodeAttempts) return new(ClaimFailure.TooManyAttempts);

        var typed = new string((code ?? "").Where(char.IsDigit).ToArray());
        claim.EmailAttempts++;
        var match = typed.Length == 6 && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(HashCode(claim.Id, typed)), System.Text.Encoding.ASCII.GetBytes(claim.EmailCodeHash));
        if (!match)
        {
            await claims.ReplaceAsync(claim, ct);
            return new(ClaimFailure.InvalidCode);
        }
        claim.EmailVerified = true;
        claim.EmailCodeHash = null;
        claim.EmailCodeExpiresAt = null;
        await claims.ReplaceAsync(claim, ct);
        return new(ClaimFailure.None, claim);
    }

    /// <summary>The document check. Only the claimant can upload; a new upload replaces the old file. The claim then waits for a moderator.</summary>
    public async Task<ClaimResult> UploadDocumentAsync(string userId, string claimId, string? documentType, byte[] content, CancellationToken ct = default)
    {
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.UserId != userId) return new(ClaimFailure.NotFound);
        if (claim.Status is not (ClaimStatus.Draft or ClaimStatus.Pending)) return new(ClaimFailure.WrongState);
        if (!claim.EmailVerified) return new(ClaimFailure.EmailNotVerified);
        var type = (documentType ?? "").Trim().ToLowerInvariant();
        if (!DocumentTypes.Contains(type)) return new(ClaimFailure.InvalidDocumentType);
        if (content.Length == 0) return new(ClaimFailure.InvalidDocument);
        if (content.Length > Opt.MaxDocumentBytes) return new(ClaimFailure.DocumentTooLarge);
        if (!LooksLikeDocument(content)) return new(ClaimFailure.InvalidDocument);

        if (claim.DocumentKey is not null) await files.DeleteAsync(claim.DocumentKey, ct);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await files.SaveAsync(key, content, ct);

        claim.DocumentKey = key;
        claim.DocumentType = type;
        claim.DocumentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        claim.DocumentUploadedAt = time.GetUtcNow().UtcDateTime;
        claim.Status = ClaimStatus.Pending;
        await claims.ReplaceAsync(claim, ct);
        return new(ClaimFailure.None, claim);
    }

    public Task<List<BusinessClaim>> MineAsync(string userId, CancellationToken ct = default) => claims.ListByUserAsync(userId, ct);

    // ---- moderators ----

    public Task<List<BusinessClaim>> QueueAsync(CancellationToken ct = default) => claims.ListPendingAsync(100, ct);

    /// <summary>Reads the document for a moderator. The audit entry is written first, so a read is never unlogged.</summary>
    public async Task<(ClaimFailure Failure, byte[]? Content, string? ContentType)> ReadDocumentAsync(string moderatorId, string claimId, CancellationToken ct = default)
    {
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null) return (ClaimFailure.NotFound, null, null);
        if (claim.DocumentKey is null) return (ClaimFailure.DocumentMissing, null, null);
        await audit.AppendAsync(new AuditRecord(moderatorId, "claim.document_read", "business_claim", claim.Id, "Document check"), ct);
        var bytes = await files.ReadAsync(claim.DocumentKey, ct);
        return bytes is null ? (ClaimFailure.DocumentMissing, null, null) : (ClaimFailure.None, bytes, ContentTypeOf(bytes));
    }

    public async Task<ClaimResult> DecideAsync(string moderatorId, string claimId, bool approve, string? reason, CancellationToken ct = default)
    {
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null) return new(ClaimFailure.NotFound);
        if (claim.Status != ClaimStatus.Pending) return new(ClaimFailure.WrongState);
        var why = reason?.Trim();
        if (!approve && (string.IsNullOrEmpty(why) || why.Length > 500)) return new(ClaimFailure.ReasonRequired);

        var now = time.GetUtcNow().UtcDateTime;
        if (approve)
        {
            var created = await ownerships.TryInsertAsync(new BusinessOwnership { EntityId = claim.EntityId, UserId = claim.UserId, ClaimId = claim.Id, ApprovedAt = now, LastSeenAt = now }, ct);
            if (!created) return new(ClaimFailure.OwnerConflict);
            await roles.SetBusinessOwnerAsync(claim.UserId, true, ct);
        }

        // The document is deleted once the decision is made (rule 5); the hash stays.
        if (claim.DocumentKey is not null) await files.DeleteAsync(claim.DocumentKey, ct);
        claim.DocumentKey = null;
        claim.Status = approve ? ClaimStatus.Approved : ClaimStatus.Rejected;
        claim.DecisionReason = why;
        claim.DecidedBy = moderatorId;
        claim.DecidedAt = now;
        await claims.ReplaceAsync(claim, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, approve ? "claim.approved" : "claim.rejected", "business_claim", claim.Id, why ?? "Claim approved"), ct);
        return new(ClaimFailure.None, claim);
    }

    /// <summary>Takes a place away from its owner (a wrong or abused claim). The account stays a business owner only while it runs another place.</summary>
    public async Task<ClaimFailure> RevokeAsync(string moderatorId, string entityId, string? reason, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > 500) return ClaimFailure.ReasonRequired;
        var owner = await ownerships.GetForEntityAsync(entityId, ct);
        if (owner is null) return ClaimFailure.NotFound;
        await ownerships.DeleteForEntityAsync(entityId, ct);
        if (await ownerships.CountByUserAsync(owner.UserId, ct) == 0) await roles.SetBusinessOwnerAsync(owner.UserId, false, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, "ownership.revoked", "entity", entityId, why), ct);
        return ClaimFailure.None;
    }

    // ---- helpers ----

    private static string? NormalizeEmail(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v) || v.Length > 200) return null;
        try
        {
            var address = new MailAddress(v);
            return address.Address == v && address.Host.Contains('.') ? v : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Clean(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }

    private static bool LooksLikeDocument(byte[] b) => ContentTypeOf(b) is not null;

    /// <summary>Judged from the first bytes, never from the name or the type the client sent.</summary>
    private static string? ContentTypeOf(byte[] b) => b switch
    {
        [0x25, 0x50, 0x44, 0x46, ..] => "application/pdf",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        _ => null,
    };
}
