using System.Text;

namespace Ratable.Shared.Text;

/// <summary>
/// The one Arabic normalizer (CLAUDE.md rule 7). Matching, search, duplicate detection and slugs use the normalized copy of a text;
/// anything shown to a user is always the original. Rules, applied to both the stored value and the query:
/// 1 remove diacritics and the dagger alef; 2 remove tatweel; 3 alef variants to plain alef; 4 alef maqsura to yaa;
/// 5 taa marbuta to haa; 6 hamza on waw and yaa to the plain letter; 7 Eastern Arabic and Persian digits to 0-9;
/// 8 remove zero-width and direction marks, collapse whitespace, trim, lowercase Latin letters.
/// Changing any rule changes how existing data matches, so indexes must be rebuilt (see the arabic-text skill).
/// </summary>
public static class ArabicNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = true; // drops leading whitespace
        foreach (var ch in text)
        {
            // 1. diacritics U+064B..U+065F and the dagger alef U+0670; 2. tatweel U+0640
            if (ch is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ') continue;

            // 8. zero-width characters and direction marks
            if (ch is '​' or '‌' or '‍' or '‎' or '‏' or '⁠' or '﻿' or '؜'
                or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) continue;

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', // 3. أ إ آ ٱ -> ا
                'ى' => 'ي',                                       // 4. ى -> ي
                'ة' => 'ه',                                       // 5. ة -> ه
                'ؤ' => 'و',                                       // 6. ؤ -> و
                'ئ' => 'ي',                                       // 6. ئ -> ي
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')), // 7. ٠-٩
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')), // 7. ۰-۹
                >= 'A' and <= 'Z' => char.ToLowerInvariant(ch),
                _ => ch,
            });
        }

        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }
}
