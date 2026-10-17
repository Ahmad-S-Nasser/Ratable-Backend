using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;

namespace Ratable.Modules.Identity.Services;

public enum OtpFailure
{
    None,
    InvalidPhone,
    BlockedNumber,
    RateLimited,
    InvalidCode,
    CodeExpired,
    TooManyAttempts,
    AccountSuspended,
}

public sealed record OtpRequestResult(OtpFailure Failure, DateTime? ExpiresAt = null, int? RetryAfterSeconds = null)
{
    public bool Ok => Failure == OtpFailure.None;
}

public sealed record OtpVerifyResult(OtpFailure Failure, User? User = null)
{
    public bool Ok => Failure == OtpFailure.None;
}

/// <summary>
/// Phone sign-in with a one-time code. Signing in this way proves the person controls the number, but it does NOT set
/// User.PhoneVerified: that flag changes only through IPhoneVerifier (rule 18, plan update 2).
/// </summary>
public sealed class OtpService(
    OtpChallengeRepository challenges,
    UserRepository users,
    PhoneProtector protector,
    IOtpSender sender,
    IOptions<IdentityOptions> options,
    TimeProvider time)
{
    private IdentityOptions Opt => options.Value;

    public async Task<OtpRequestResult> RequestAsync(string? rawPhone, string? ip, CancellationToken ct = default)
    {
        var phone = PhoneNumber.Normalize(rawPhone);
        if (phone is null) return new(OtpFailure.InvalidPhone);
        if (Opt.BlockedPhonePrefixes.Any(p => !string.IsNullOrWhiteSpace(p) && phone.StartsWith(p.Trim(), StringComparison.Ordinal)))
            return new(OtpFailure.BlockedNumber);

        var now = time.GetUtcNow().UtcDateTime;
        var phoneHash = protector.Hash(phone);
        var ipHash = protector.Hash("ip:" + (ip ?? "unknown"));

        var last = await challenges.GetLatestAsync(phoneHash, ct);
        if (last is not null && (now - last.CreatedAt).TotalSeconds < Opt.OtpResendSeconds)
            return new(OtpFailure.RateLimited, RetryAfterSeconds: Opt.OtpResendSeconds - (int)(now - last.CreatedAt).TotalSeconds);

        var since = now.AddHours(-1);
        if (await challenges.CountForPhoneSinceAsync(phoneHash, since, ct) >= Opt.OtpPerPhonePerHour ||
            await challenges.CountForIpSinceAsync(ipHash, since, ct) >= Opt.OtpPerIpPerHour)
            return new(OtpFailure.RateLimited, RetryAfterSeconds: 3600);

        var challenge = new OtpChallenge
        {
            PhoneHash = phoneHash,
            IpHash = ipHash,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Opt.OtpLifetimeMinutes),
        };
        var code = NewCode(Opt.OtpLength);
        challenge.CodeHash = HashCode(challenge.Id, code);
        await challenges.InsertAsync(challenge, ct);
        await sender.SendAsync(phone, code, ct);
        return new(OtpFailure.None, ExpiresAt: challenge.ExpiresAt);
    }

    public async Task<OtpVerifyResult> VerifyAsync(string? rawPhone, string? code, CancellationToken ct = default)
    {
        var phone = PhoneNumber.Normalize(rawPhone);
        if (phone is null || string.IsNullOrWhiteSpace(code)) return new(OtpFailure.InvalidCode);

        var now = time.GetUtcNow().UtcDateTime;
        var phoneHash = protector.Hash(phone);

        var open = await challenges.GetLatestOpenAsync(phoneHash, ct);
        if (open is null) return new(OtpFailure.InvalidCode);
        if (open.ExpiresAt <= now) return new(OtpFailure.CodeExpired);

        // Each try counts, right or wrong, so a code cannot be guessed by retrying.
        var counted = await challenges.TryCountAttemptAsync(open.Id, Opt.OtpMaxAttempts, ct);
        if (counted is null) return new(OtpFailure.TooManyAttempts);

        var expected = Encoding.UTF8.GetBytes(counted.CodeHash);
        var actual = Encoding.UTF8.GetBytes(HashCode(counted.Id, code.Trim()));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return new(OtpFailure.InvalidCode);

        // Exactly one caller wins the consume, so a code works once even with concurrent requests.
        if (!await challenges.TryConsumeAsync(counted.Id, now, ct)) return new(OtpFailure.InvalidCode);

        var user = await FindOrCreateUserAsync(phone, phoneHash, ct);
        if (user.Status == UserStatus.Suspended) return new(OtpFailure.AccountSuspended);
        return new(OtpFailure.None, user);
    }

    private async Task<User> FindOrCreateUserAsync(string phone, string phoneHash, CancellationToken ct)
    {
        var user = await users.GetByPhoneHashAsync(phoneHash, ct);
        if (user is null)
        {
            user = new User { PhoneHash = phoneHash, PhoneEncrypted = protector.Encrypt(phone), PhoneVerified = false, CreatedAt = time.GetUtcNow().UtcDateTime };
            try
            {
                await users.InsertAsync(user, ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Two first sign-ins at once: the unique phone index keeps one account per number.
                user = (await users.GetByPhoneHashAsync(phoneHash, ct))!;
            }
        }

        if (!string.IsNullOrWhiteSpace(Opt.BootstrapAdminPhone) &&
            PhoneNumber.Normalize(Opt.BootstrapAdminPhone) is { } adminPhone &&
            protector.Hash(adminPhone) == phoneHash &&
            !user.Roles.Contains(UserRole.Admin))
        {
            user.Roles.Add(UserRole.Admin);
            await users.ReplaceAsync(user, ct);
        }
        return user;
    }

    private string HashCode(string challengeId, string code) => protector.Hash($"otp:{challengeId}:{code}");

    private static string NewCode(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(RandomNumberGenerator.GetInt32(0, 10));
        return sb.ToString();
    }
}
