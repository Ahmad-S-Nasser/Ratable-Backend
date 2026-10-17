using Ratable.Shared.Text;

namespace Ratable.Modules.Entities.Matching;

/// <summary>
/// Decides how alike two names are, after Arabic normalization. It finds duplicate CANDIDATES; it never decides a duplicate by itself.
/// Names are compared word by word, so spelling variants of one name score 1.0 while names that only share a word ("Sodic East" and
/// "Sodic West", "Palm Hills" and "Palm Parks") stay below the candidate threshold.
/// </summary>
public static class NameMatcher
{
    /// <summary>Words that say what a place is rather than which place it is. Normalized forms; ignored when comparing.</summary>
    private static readonly HashSet<string> GenericWords =
    [
        "compound", "resort", "hotel", "school", "nursery", "restaurant", "cafe", "the", "and", "of",
        "كمبوند", "كومباوند", "منتجع", "فندق", "مدرسه", "حضانه", "مطعم", "كافيه", "كافي",
    ];

    public static List<string> Tokens(string? name)
    {
        var normalized = ArabicNormalizer.Normalize(name);
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var isWordChar = i < normalized.Length && char.IsLetterOrDigit(normalized[i]);
            if (isWordChar && start < 0) start = i;
            if (!isWordChar && start >= 0)
            {
                var token = normalized[start..i];
                start = -1;
                if (GenericWords.Contains(token)) continue;
                // "الرحاب" and "رحاب" are the same word.
                if (token.Length > 3 && token.StartsWith("ال", StringComparison.Ordinal)) token = token[2..];
                tokens.Add(token);
            }
        }
        return tokens;
    }

    /// <summary>0 to 1: the share of words that match. 1 means every word matches, in any order.</summary>
    public static double Score(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var (small, large) = ta.Count <= tb.Count ? (ta, tb) : (tb, ta);
        var used = new bool[large.Count];
        var matched = 0;
        foreach (var token in small)
        {
            // Prefer an exact match over a typo match so one word is not matched twice.
            var index = FindUnused(large, used, token, exact: true);
            if (index < 0) index = FindUnused(large, used, token, exact: false);
            if (index < 0) continue;
            used[index] = true;
            matched++;
        }
        return (double)matched / large.Count;
    }

    /// <summary>The best score over every pair of names (Arabic, English, alternates) of two places.</summary>
    public static double BestScore(IEnumerable<string?> namesA, IEnumerable<string?> namesB)
    {
        var best = 0.0;
        var listB = namesB.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        foreach (var a in namesA.Where(n => !string.IsNullOrWhiteSpace(n)))
            foreach (var b in listB)
            {
                best = Math.Max(best, Score(a, b));
                if (best >= 1) return 1;
            }
        return best;
    }

    private static int FindUnused(List<string> tokens, bool[] used, string token, bool exact)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (used[i]) continue;
            if (tokens[i] == token) return i;
            // A one-letter typo is forgiven only in longer words, so "east" and "west" never match.
            if (!exact && token.Length >= 6 && tokens[i].Length >= 6 && Math.Abs(token.Length - tokens[i].Length) <= 1 && WithinOneEdit(token, tokens[i])) return i;
        }
        return -1;
    }

    private static bool WithinOneEdit(string a, string b)
    {
        if (a == b) return true;
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (a.Length < b.Length) j++;
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }
}
