namespace Ratable.Modules.Media.Services;

/// <summary>
/// What a file really is, read from its first bytes. The type the browser claims is never trusted (a renamed file is refused).
/// Videos: MP4 and QuickTime (the formats phones record) and WebM. Photos: JPEG, PNG and WebP.
/// </summary>
public static class FileTypes
{
    public static readonly string[] VideoTypes = ["video/mp4", "video/quicktime", "video/webm"];
    public static readonly string[] PhotoTypes = ["image/jpeg", "image/png", "image/webp"];

    public static bool IsAllowedClaim(Models.MediaKind kind, string contentType) =>
        (kind == Models.MediaKind.Video ? VideoTypes : PhotoTypes).Contains(contentType, StringComparer.OrdinalIgnoreCase);

    /// <summary>The detected content type from the first bytes, or null when the file is not one of the allowed types.</summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
        if (head.Length >= 8 && head[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return "video/webm";
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
            return head.Slice(8, 4).SequenceEqual("qt  "u8) ? "video/quicktime" : "video/mp4";
        return null;
    }

    /// <summary>True when the detected type is a fair match for what the author declared (a QuickTime file may be declared as MP4 and the other way round).</summary>
    public static bool Matches(string declared, string detected)
    {
        if (string.Equals(declared, detected, StringComparison.OrdinalIgnoreCase)) return true;
        string[] family = ["video/mp4", "video/quicktime"];
        return family.Contains(declared, StringComparer.OrdinalIgnoreCase) && family.Contains(detected);
    }

    public static string Extension(string contentType) => contentType switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        _ => "bin",
    };
}
