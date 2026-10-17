using System.Text;

namespace Ratable.Modules.Identity.Services;

public static class PhoneNumber
{
    /// <summary>
    /// Turns user input into +E.164 (a plus and 8 to 15 digits), or null when it cannot be a phone number.
    /// Accepts Arabic-Indic and Persian digits, spaces and dashes, 00 as +, and Egyptian local numbers (01x...).
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var digits = new StringBuilder();
        var plus = false;
        foreach (var ch in input.Trim())
        {
            if (ch == '+' && digits.Length == 0) plus = true;
            else if (ch is >= '0' and <= '9') digits.Append(ch);
            else if (ch is >= '٠' and <= '٩') digits.Append((char)('0' + (ch - '٠')));
            else if (ch is >= '۰' and <= '۹') digits.Append((char)('0' + (ch - '۰')));
            else if (ch is ' ' or '-' or '(' or ')' or '.' or '‎' or '‏' or '‪' or '‬') continue;
            else return null;
        }

        var d = digits.ToString();
        if (!plus && d.StartsWith("00")) { d = d[2..]; plus = true; }
        if (!plus && d.Length == 11 && d.StartsWith("01")) { d = "20" + d[1..]; plus = true; } // Egyptian mobile
        if (!plus) return null;
        if (d.Length is < 8 or > 15 || d[0] == '0') return null;
        return "+" + d;
    }
}
