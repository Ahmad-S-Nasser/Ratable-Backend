namespace Ratable.Modules.Identity.Services;

/// <summary>Bound from the "Identity" configuration section. Keys and the bootstrap admin come from environment variables, never from the repository.</summary>
public sealed class IdentityOptions
{
    public const string Section = "Identity";

    /// <summary>Base64 of 32 random bytes. Encrypts phone numbers at rest (AES-256-GCM). Identity__PhoneEncryptionKey.</summary>
    public string PhoneEncryptionKey { get; set; } = "";

    /// <summary>Base64 of at least 32 random bytes. Keys the one-way hashes of phone numbers, codes and IPs. Identity__PhoneHashKey.</summary>
    public string PhoneHashKey { get; set; } = "";

    /// <summary>"console" prints codes to the API console (development only). Anything else uses the unconfigured sender, which refuses to send.</summary>
    public string OtpSender { get; set; } = "";

    /// <summary>Number prefixes of temporary or virtual numbers that cannot sign in, for example "+1555". Matched against the normalized +E.164 number.</summary>
    public List<string> BlockedPhonePrefixes { get; set; } = [];

    public int OtpLength { get; set; } = 6;
    public int OtpLifetimeMinutes { get; set; } = 10;
    public int OtpMaxAttempts { get; set; } = 5;
    public int OtpPerPhonePerHour { get; set; } = 5;
    public int OtpPerIpPerHour { get; set; } = 20;
    public int OtpResendSeconds { get; set; } = 60;

    // ---- Verify via WhatsApp (rule 18) ----

    /// <summary>Ratable's own WhatsApp number in international form, for the reverse route (the person messages this number). Identity__WhatsAppNumber.</summary>
    public string WhatsAppNumber { get; set; } = "";

    /// <summary>The token set in the WhatsApp Cloud API webhook set-up screen. Identity__WhatsAppVerifyToken.</summary>
    public string WhatsAppVerifyToken { get; set; } = "";

    /// <summary>The WhatsApp app secret. Every webhook call is signed with it, and unsigned calls are refused. Identity__WhatsAppAppSecret.</summary>
    public string WhatsAppAppSecret { get; set; } = "";

    public int VerifyCodeLifetimeMinutes { get; set; } = 10;
    public int VerifyMaxAttempts { get; set; } = 5;
    public int VerifyStartsPerPhonePerHour { get; set; } = 3;
    public int VerifyStartsPerIpPerHour { get; set; } = 30;
    public int VerifyStartsPerDevicePerDay { get; set; } = 5;

    /// <summary>How many accounts with a verified phone may share one device. Applies where the app sends a device id.</summary>
    public int VerifiedAccountsPerDevice { get; set; } = 3;

    // ---- Google and Apple sign-in: the client ids the ID tokens must be made for. Leave empty to turn a provider off. ----
    public string GoogleClientId { get; set; } = "";
    public string AppleClientId { get; set; } = "";

    public int WebSessionDays { get; set; } = 30;
    public int AppSessionDays { get; set; } = 90;

    /// <summary>The first admin: the account that signs in with this number gets the Admin role. Leave empty once an admin exists.</summary>
    public string BootstrapAdminPhone { get; set; } = "";
}
