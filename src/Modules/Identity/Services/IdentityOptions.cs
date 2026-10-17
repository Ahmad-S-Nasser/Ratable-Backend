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

    public int WebSessionDays { get; set; } = 30;
    public int AppSessionDays { get; set; } = 90;

    /// <summary>The first admin: the account that signs in with this number gets the Admin role. Leave empty once an admin exists.</summary>
    public string BootstrapAdminPhone { get; set; } = "";
}
