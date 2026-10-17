using System.Text;

namespace Ratable.Shared.Text;

public static class Slugger
{
    /// <summary>
    /// A URL slug built from the normalized form: letters and digits (Arabic included) joined by single hyphens.
    /// The id in the URL is what identifies a page; the slug only has to read well, so it may change and then redirects.
    /// </summary>
    public static string Make(string? text, int maxLength = 80)
    {
        var normalized = ArabicNormalizer.Normalize(text);
        var sb = new StringBuilder(normalized.Length);
        var pendingHyphen = false;
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingHyphen && sb.Length > 0) sb.Append('-');
                pendingHyphen = false;
                sb.Append(ch);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var slug = sb.ToString();
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd('-');
        return slug;
    }
}
