using Microsoft.Extensions.Options;
using Ratable.Modules.Identity.Services;

namespace Ratable.Modules.Identity.PhoneVerification;

/// <summary>
/// The default route (no per-message cost): the app shows a one-time code and opens WhatsApp with it pre-filled in a message to Ratable's number. The
/// WhatsApp Cloud API webhook then matches the code and the sender's number (see <see cref="PhoneVerificationService.HandleIncomingMessageAsync"/>).
/// </summary>
public sealed class ReverseWhatsAppVerifier(PhoneVerificationService service, IOptions<IdentityOptions> options) : IPhoneVerifier
{
    public VerifyRoute Route => VerifyRoute.Reverse;

    public async Task<PhoneVerifyStart> StartAsync(string userId, string? rawPhone, DeviceContext context, string? channel = null, CancellationToken ct = default)
    {
        var (result, _, code) = await service.BeginAsync(userId, rawPhone, VerifyRoute.Reverse, context, ct);
        if (!result.Ok || code is null) return result;

        var number = (options.Value.WhatsAppNumber ?? "").TrimStart('+');
        if (number.Length == 0) return new(VerifyFailure.ProviderUnavailable); // Ratable's WhatsApp number is not configured
        // wa.me opens WhatsApp with the message already typed; the person only has to press send.
        var message = Uri.EscapeDataString($"Ratable {PhoneVerificationService.Display(code)}");
        return result with { WhatsAppUrl = $"https://wa.me/{number}?text={message}" };
    }
}

/// <summary>
/// The alternative route (a small per-message cost): we send the code to the person by WhatsApp or SMS through a provider interface. No provider is
/// chosen yet, so only the interface and a fake exist; without one this route says it is unavailable and the reverse route is used.
/// </summary>
public sealed class CodeSenderVerifier(PhoneVerificationService service, IVerificationCodeSender sender) : IPhoneVerifier
{
    public VerifyRoute Route => VerifyRoute.Code;

    public async Task<PhoneVerifyStart> StartAsync(string userId, string? rawPhone, DeviceContext context, string? channel = null, CancellationToken ct = default)
    {
        var via = string.Equals(channel, "sms", StringComparison.OrdinalIgnoreCase) ? "sms" : "whatsapp";
        var (result, phone, code) = await service.BeginAsync(userId, rawPhone, VerifyRoute.Code, context, ct);
        if (!result.Ok || phone is null || code is null) return result;
        try
        {
            await sender.SendAsync(phone, PhoneVerificationService.Display(code), via, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new(VerifyFailure.ProviderUnavailable);
        }
        // The code is not returned: it goes to the person's phone.
        return result with { Code = null };
    }
}
