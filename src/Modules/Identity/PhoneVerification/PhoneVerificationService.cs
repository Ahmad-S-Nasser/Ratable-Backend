using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Services;

namespace Ratable.Modules.Identity.PhoneVerification;

public sealed record VerifyOutcome(VerifyFailure Failure, string? UserId = null)
{
    public bool Ok => Failure == VerifyFailure.None;
}

public sealed record VerifyStatus(string State, DateTime? ExpiresAt);

/// <summary>
/// Optional phone verification (CLAUDE.md rule 18, "Verify via WhatsApp"). It proves the person controls a number; it sets <c>User.PhoneVerified</c>, which
/// lets a review without proof skip the human queue when the automatic checks pass. It never makes a review Verified and never changes any score.
/// Codes are single-use, expire after ten minutes, are stored hashed and are rate limited per number, device and network. Phone numbers are stored
/// encrypted, never shown, and the number of verified accounts per phone and per device is limited.
/// </summary>
public sealed partial class PhoneVerificationService(
    PhoneVerificationRepository verifications, UserRepository users, SessionRepository sessions, PhoneProtector protector, INumberTypeChecker numberTypes,
    IOptions<IdentityOptions> options, TimeProvider time)
{
    private IdentityOptions Opt => options.Value;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0, 1, I or O

    [GeneratedRegex("(?<![A-Z0-9])[A-Z2-9]{4}-?[A-Z2-9]{4}(?![A-Z0-9])")]
    private static partial Regex CodePattern();

    public static string NewCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>The code as shown to people: XXXX-XXXX.</summary>
    public static string Display(string code) => $"{code[..4]}-{code[4..]}";

    private static string Normalize(string code) => code.Replace("-", "").Replace(" ", "").ToUpperInvariant();

    private string HashCode(string normalized) => protector.Hash($"pv:{normalized}");

    /// <summary>Starts a verification on either route (the route decides how the code reaches the person). The code itself is returned once, never stored.</summary>
    public async Task<(PhoneVerifyStart Result, string? Phone, string? Code)> BeginAsync(string userId, string? rawPhone, VerifyRoute route, DeviceContext context, CancellationToken ct = default)
    {
        var user = await users.GetAsync(userId, ct);
        if (user is null) return (new(VerifyFailure.AccountNotFound), null, null);
        if (user.PhoneVerified) return (new(VerifyFailure.AlreadyVerified), null, null);

        // Which number: the one entered, or the one the account already has (it signed in with it).
        var phone = rawPhone is not null ? PhoneNumber.Normalize(rawPhone) : user.PhoneEncrypted is null ? null : protector.Decrypt(user.PhoneEncrypted);
        if (phone is null) return (new(VerifyFailure.InvalidPhone), null, null);
        var phoneHash = protector.Hash(phone);

        // A number that belongs to another account cannot be verified on this one.
        var owner = await users.GetByPhoneHashAsync(phoneHash, ct);
        if (owner is not null && owner.Id != user.Id) return (new(VerifyFailure.PhoneInUse), null, null);

        // Virtual and temporary numbers are not allowed.
        if (await numberTypes.CheckAsync(phone, ct) is NumberType.Virtual or NumberType.Temporary) return (new(VerifyFailure.NumberNotAllowed), null, null);

        var now = time.GetUtcNow().UtcDateTime;
        var deviceHash = string.IsNullOrWhiteSpace(context.DeviceId) ? null : protector.Hash("device:" + context.DeviceId.Trim());
        var ipHash = protector.Hash("ip:" + (context.Ip ?? "unknown"));

        if (await verifications.CountForPhoneSinceAsync(phoneHash, now.AddHours(-1), ct) >= Opt.VerifyStartsPerPhonePerHour ||
            await verifications.CountForIpSinceAsync(ipHash, now.AddHours(-1), ct) >= Opt.VerifyStartsPerIpPerHour ||
            (deviceHash is not null && await verifications.CountForDeviceSinceAsync(deviceHash, now.AddDays(-1), ct) >= Opt.VerifyStartsPerDevicePerDay))
            return (new(VerifyFailure.RateLimited, RetryAfterSeconds: 3600), null, null);

        // Only so many verified accounts may share a device. The web has no device id, and a network address says little about a device (a whole mobile
        // carrier or office can share one), so the device rule applies only where the app sends a device id; the web is limited per number and per network.
        if (deviceHash is not null && await CountVerifiedAccountsOnDeviceAsync(deviceHash, user.Id, ct) >= Opt.VerifiedAccountsPerDevice)
            return (new(VerifyFailure.TooManyAccountsOnDevice), null, null);

        var code = NewCode();
        var record = new PhoneVerification
        {
            UserId = user.Id,
            PhoneHash = phoneHash,
            PhoneEncrypted = protector.Encrypt(phone),
            Route = route,
            CodeHash = HashCode(code),
            DeviceHash = deviceHash,
            IpHash = ipHash,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Opt.VerifyCodeLifetimeMinutes),
        };
        await verifications.InsertAsync(record, ct);
        return (new PhoneVerifyStart(VerifyFailure.None, route, Display(code), null, record.ExpiresAt), phone, code);
    }

    /// <summary>Verified accounts whose latest session came from this device (or network, for the web, which has no device id).</summary>
    private async Task<int> CountVerifiedAccountsOnDeviceAsync(string deviceKey, string exceptUserId, CancellationToken ct)
    {
        var userIds = await sessions.ListUserIdsForDeviceAsync(deviceKey, ct);
        var others = userIds.Where(id => id != exceptUserId).Distinct().ToList();
        if (others.Count == 0) return 0;
        return (await users.ListByIdsAsync(others, ct)).Count(u => u.PhoneVerified);
    }

    // ---- finishing ----

    /// <summary>The alternative route: the person types the code we sent. Wrong tries are counted; after the limit the code is burned.</summary>
    public async Task<VerifyOutcome> ConfirmCodeAsync(string userId, string? code, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var open = await verifications.GetLatestOpenAsync(userId, VerifyRoute.Code, now, ct);
        if (open is null)
        {
            var last = await verifications.GetLatestAsync(userId, ct);
            return new(last is null ? VerifyFailure.NotStarted : last.ConsumedAt is null ? VerifyFailure.CodeExpired : VerifyFailure.NotStarted);
        }
        if (open.Attempts >= Opt.VerifyMaxAttempts) return new(VerifyFailure.TooManyAttempts);

        var normalized = Normalize(code ?? "");
        var expected = Encoding.UTF8.GetBytes(open.CodeHash);
        var actual = Encoding.UTF8.GetBytes(HashCode(normalized));
        if (normalized.Length != 8 || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            await verifications.AddAttemptAsync(open.Id, ct);
            return new(VerifyFailure.InvalidCode);
        }
        return await CompleteAsync(open, ct);
    }

    /// <summary>
    /// The default route: Ratable's WhatsApp number received a message. The sender's number must be the number being verified. A code in a message from
    /// anyone else is refused (and counted against the code), and a code works once.
    /// </summary>
    public async Task<VerifyOutcome> HandleIncomingMessageAsync(string senderRaw, string? text, CancellationToken ct = default)
    {
        var sender = PhoneNumber.Normalize(senderRaw.StartsWith('+') ? senderRaw : "+" + senderRaw);
        if (sender is null || string.IsNullOrWhiteSpace(text)) return new(VerifyFailure.InvalidCode);
        var senderHash = protector.Hash(sender);
        var now = time.GetUtcNow().UtcDateTime;

        VerifyOutcome last = new(VerifyFailure.InvalidCode);
        foreach (Match match in CodePattern().Matches(text.ToUpperInvariant()))
        {
            var record = await verifications.FindOpenByCodeAsync(HashCode(Normalize(match.Value)), VerifyRoute.Reverse, now, ct);
            if (record is null) continue;
            if (record.Attempts >= Opt.VerifyMaxAttempts) { last = new(VerifyFailure.TooManyAttempts); continue; }
            if (record.PhoneHash != senderHash)
            {
                await verifications.AddAttemptAsync(record.Id, ct); // somebody else sent the code: it does not verify anything
                last = new(VerifyFailure.WrongSender);
                continue;
            }
            return await CompleteAsync(record, ct);
        }
        return last;
    }

    private async Task<VerifyOutcome> CompleteAsync(PhoneVerification record, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var consumed = await verifications.ConsumeAsync(record.Id, now, ct); // only one caller gets past this
        if (consumed is null) return new(VerifyFailure.CodeExpired);

        var user = await users.GetAsync(record.UserId, ct);
        if (user is null) return new(VerifyFailure.AccountNotFound);
        if (user.PhoneHash is null)
        {
            user.PhoneHash = record.PhoneHash;
            user.PhoneEncrypted = record.PhoneEncrypted;
        }
        else if (user.PhoneHash != record.PhoneHash) return new(VerifyFailure.PhoneInUse);
        user.PhoneVerified = true;
        try
        {
            await users.ReplaceAsync(user, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(VerifyFailure.PhoneInUse); // another account took the number in the meantime
        }
        await verifications.ClearPhoneAsync(record.Id, ct);
        return new(VerifyFailure.None, user.Id);
    }

    public async Task<VerifyStatus> StatusAsync(string userId, CancellationToken ct = default)
    {
        var user = await users.GetAsync(userId, ct);
        if (user?.PhoneVerified == true) return new("verified", null);
        var last = await verifications.GetLatestAsync(userId, ct);
        if (last is null) return new("none", null);
        return time.GetUtcNow().UtcDateTime < last.ExpiresAt && last.ConsumedAt is null ? new("pending", last.ExpiresAt) : new("expired", last.ExpiresAt);
    }
}
