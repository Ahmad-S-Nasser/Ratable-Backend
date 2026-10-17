namespace Ratable.Modules.Verification.Services;

/// <summary>Bound from the "Verification" configuration section. The time limits are placeholders to test (launch plan).</summary>
public sealed class VerificationOptions
{
    public const string Section = "Verification";

    /// <summary>Folder for proof files (local-disk storage). Must be outside any web root.</summary>
    public string StoragePath { get; set; } = "App_Data/proofs";

    /// <summary>Base64 of 32 random bytes. Encrypts stored proofs. Verification__StorageKey; never in the repository.</summary>
    public string StorageKey { get; set; } = "";

    public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxUploadsPerDay { get; set; } = 10;
    public int MaxAttemptsPerReview { get; set; } = 3;

    /// <summary>How long a file the author chose to keep is held before it is deleted anyway.</summary>
    public int KeepMonths { get; set; } = 12;

    /// <summary>Share of automatically approved proofs that a person also checks (5 percent).</summary>
    public double AuditSampleRate { get; set; } = 0.05;

    /// <summary>The AI must be at least this sure of what it read, or a person decides.</summary>
    public double MinConfidence { get; set; } = 0.7;

    /// <summary>Per category: how old a document may be, in months. A category with no entry has no age limit (a document dated in the future is still flagged).</summary>
    public Dictionary<string, int> MaxAgeMonths { get; set; } = new() { ["hotels-resorts"] = 12, ["appliances"] = 24, ["compounds"] = 12 };

    /// <summary>The proof type that means "a live photo taken in the app at the place". It is never uploaded as a file.</summary>
    public string PresenceProofType { get; set; } = "photo_of_visit";

    /// <summary>For presence proof: the review must be started within this many hours of the visit.</summary>
    public int PresenceMaxHoursAfterVisit { get; set; } = 48;
}
