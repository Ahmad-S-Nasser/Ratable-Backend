using Microsoft.Extensions.Logging;

namespace Ratable.Modules.Identity.Services;

/// <summary>Delivers a sign-in code. No provider is chosen yet (see the plan); the real one is added behind this interface.</summary>
public interface IOtpSender
{
    Task SendAsync(string phoneE164, string code, CancellationToken cancellationToken = default);
}

/// <summary>Development only (Identity:OtpSender = console). Writes the code to the console with the phone number masked.</summary>
public sealed class ConsoleOtpSender : IOtpSender
{
    public Task SendAsync(string phoneE164, string code, CancellationToken cancellationToken = default)
    {
        var masked = phoneE164.Length > 4 ? new string('*', phoneE164.Length - 4) + phoneE164[^4..] : "****";
        Console.WriteLine($"[DEV OTP] code {code} for {masked}");
        return Task.CompletedTask;
    }
}

/// <summary>The placeholder for the real SMS or WhatsApp provider. It refuses to send, so a missing provider is never silent.</summary>
public sealed class UnconfiguredOtpSender(ILogger<UnconfiguredOtpSender> logger) : IOtpSender
{
    public Task SendAsync(string phoneE164, string code, CancellationToken cancellationToken = default)
    {
        logger.LogError("No OTP provider is configured. Set Identity__OtpSender=console for development or add a provider.");
        throw new InvalidOperationException("No OTP provider is configured.");
    }
}
