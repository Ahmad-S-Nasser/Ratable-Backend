using Microsoft.Extensions.Logging;
using Ratable.Modules.Identity.Services;

namespace Ratable.Modules.Identity.PhoneVerification;

/// <summary>
/// Looks at a phone number and says what kind it is. The real one will call a number-intelligence provider; none is chosen yet, so the default only
/// knows the configured blocked prefixes and a test double is used in tests. Virtual and temporary numbers are blocked (CLAUDE.md rule 18).
/// </summary>
public interface INumberTypeChecker
{
    Task<NumberType> CheckAsync(string phoneE164, CancellationToken cancellationToken = default);
}

/// <summary>The default: a number that starts with one of Identity:BlockedPhonePrefixes is treated as virtual, anything else is unknown (and allowed).</summary>
public sealed class PrefixNumberTypeChecker(Microsoft.Extensions.Options.IOptions<IdentityOptions> options) : INumberTypeChecker
{
    public Task<NumberType> CheckAsync(string phoneE164, CancellationToken ct = default) =>
        Task.FromResult(options.Value.BlockedPhonePrefixes.Any(p => !string.IsNullOrWhiteSpace(p) && phoneE164.StartsWith(p.Trim(), StringComparison.Ordinal)) ? NumberType.Virtual : NumberType.Unknown);
}

/// <summary>How a code is delivered on the alternative route. No provider is chosen yet, so only the interface, an unconfigured default and (in tests) a fake exist.</summary>
public interface IVerificationCodeSender
{
    /// <summary>"whatsapp" or "sms".</summary>
    Task SendAsync(string phoneE164, string code, string channel, CancellationToken cancellationToken = default);
}

/// <summary>Refuses to send, so a missing provider is never silent.</summary>
public sealed class UnconfiguredVerificationCodeSender(Microsoft.Extensions.Logging.ILogger<UnconfiguredVerificationCodeSender> logger) : IVerificationCodeSender
{
    public Task SendAsync(string phoneE164, string code, string channel, CancellationToken ct = default)
    {
        logger.LogError("No provider is configured to send verification codes by {Channel}.", channel);
        throw new InvalidOperationException("No verification code provider is configured.");
    }
}

/// <summary>What starting a verification returns to the person. The code is shown once and never stored in the clear.</summary>
public sealed record PhoneVerifyStart(VerifyFailure Failure, VerifyRoute Route = VerifyRoute.Reverse, string? Code = null, string? WhatsAppUrl = null, DateTime? ExpiresAt = null, int? RetryAfterSeconds = null)
{
    public bool Ok => Failure == VerifyFailure.None;
}

public enum VerifyFailure
{
    None, InvalidPhone, NumberNotAllowed, PhoneInUse, AlreadyVerified, RateLimited, TooManyAccountsOnDevice, NotStarted, CodeExpired, InvalidCode, TooManyAttempts, WrongSender,
    ProviderUnavailable, AccountNotFound,
}

/// <summary>The two ways to prove a phone number. Both end in the same place: the account's phone is verified (which never changes any score).</summary>
public interface IPhoneVerifier
{
    VerifyRoute Route { get; }

    Task<PhoneVerifyStart> StartAsync(string userId, string? rawPhone, DeviceContext context, string? channel = null, CancellationToken cancellationToken = default);
}

/// <summary>Where the request came from, as hashes only (never raw): the device (apps send it; the web has none) and the network.</summary>
public sealed record DeviceContext(string? DeviceId, string? Ip);
