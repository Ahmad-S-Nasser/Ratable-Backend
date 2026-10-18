using System.Text.RegularExpressions;

namespace Ratable.Shared.Text;

/// <summary>
/// Removes ways to reach someone outside Ratable from free text: phone numbers (Latin and Arabic-Indic digits), emails, links, WhatsApp and Telegram addresses and
/// @handles. It is used when text about a locked profile is shown (CLAUDE.md rule 19), so contact details that were typed into a review, a caption or a profile
/// text never reveal what the owner's plan hides. Pure and deterministic: no AI is involved.
/// </summary>
public static partial class ContactStripper
{
    public const string Mask = "…";

    private const string Digit = "[0-9٠-٩۰-۹]";

    [GeneratedRegex(@"(?:https?://|www\.)\S+|\b(?:wa\.me|t\.me|api\.whatsapp\.com|chat\.whatsapp\.com|instagram\.com|facebook\.com|fb\.com|fb\.me|tiktok\.com|linktr\.ee|linkedin\.com|twitter\.com|x\.com)/\S*", RegexOptions.IgnoreCase)]
    private static partial Regex Links();

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+", RegexOptions.IgnoreCase)]
    private static partial Regex Emails();

    [GeneratedRegex(@"(?<![\w@])@[\w.]{3,}")]
    private static partial Regex Handles();

    // A run of digits that may be spaced or dashed, with an optional + or 00 in front.
    [GeneratedRegex("(?<!" + Digit + @")(?:\+|00)?" + Digit + @"(?:[ \-().]{0,2}" + Digit + @"){6,}(?!" + Digit + ")")]
    private static partial Regex Numbers();

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = Links().Replace(text, Mask);
        s = Emails().Replace(s, Mask);
        s = Handles().Replace(s, Mask);
        // Eight or more digits in a run look like a phone number; shorter runs (prices, years, counts) stay.
        s = Numbers().Replace(s, m => m.Value.Count(c => char.IsDigit(c) || c is >= '٠' and <= '٩' or >= '۰' and <= '۹') >= 8 ? Mask : m.Value);
        return s;
    }

    /// <summary>True when <see cref="Strip"/> would change the text.</summary>
    public static bool HasContact(string? text) => !string.IsNullOrEmpty(text) && Strip(text) != text;
}
