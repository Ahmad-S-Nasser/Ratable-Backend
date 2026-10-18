using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ratable.Modules.Entities.Import;

/// <summary>One place from the file, before any checking. Line is the row number in the file (the first data row is 1).</summary>
public sealed record ImportRow(
    int Line, string? Category, string? NameAr, string? NameEn, List<string> AltNames, string? Address, string? City, string? Country,
    double? Latitude, double? Longitude, string? GooglePlaceId, string? Phone, string? WhatsApp, string? Website,
    Dictionary<string, string> ExternalIds, Dictionary<string, string> Fields);

public sealed class ImportFormatException(string message) : Exception(message);

/// <summary>
/// Reads the seed file, as CSV (one header row, commas or semicolons, quotes for text with commas) or JSON (an array of objects). Column names are matched ignoring case,
/// spaces, dashes and underscores, so "name_ar", "nameAr" and "Name AR" are the same. Lists (alt_names) are separated by |, and key-value lists (external_ids, fields) are
/// written key:value|key:value. See docs/seed-import.md.
/// </summary>
public static class ImportParser
{
    public static List<ImportRow> Parse(string text, string? format)
    {
        var body = text.TrimStart('﻿').TrimStart();
        var json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) || (format is null && body.StartsWith('['));
        return json ? ParseJson(body) : ParseCsv(body);
    }

    private static string Key(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // ---- CSV ----

    private static List<ImportRow> ParseCsv(string text)
    {
        var records = ReadCsv(text);
        if (records.Count < 1) throw new ImportFormatException("empty_file");
        var header = records[0].Select(Key).ToList();
        var rows = new List<ImportRow>();
        for (var i = 1; i < records.Count; i++)
        {
            var cells = records[i];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var values = new Dictionary<string, string>();
            for (var c = 0; c < header.Count && c < cells.Count; c++) values[header[c]] = cells[c];
            rows.Add(ToRow(i, values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)));
        }
        return rows;
    }

    private static List<List<string>> ReadCsv(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var records = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == delimiter) { row.Add(cell.ToString().Trim()); cell.Clear(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString().Trim());
                cell.Clear();
                records.Add(row);
                row = [];
            }
            else cell.Append(ch);
        }
        if (quoted) throw new ImportFormatException("unclosed_quote");
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().Trim());
            records.Add(row);
        }
        return records;
    }

    // ---- JSON ----

    private static List<ImportRow> ParseJson(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new ImportFormatException("invalid_json");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ImportFormatException("json_must_be_an_array");
            var rows = new List<ImportRow>();
            var line = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                line++;
                if (item.ValueKind != JsonValueKind.Object) throw new ImportFormatException("json_items_must_be_objects");
                var values = new Dictionary<string, object?>();
                foreach (var p in item.EnumerateObject()) values[Key(p.Name)] = FromJson(p.Value);
                rows.Add(ToRow(line, values));
            }
            return rows;
        }
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString()),
        _ => null,
    };

    // ---- both ----

    private static string? Text(Dictionary<string, object?> v, params string[] keys)
    {
        foreach (var k in keys)
            if (v.TryGetValue(k, out var o) && o is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        return null;
    }

    private static double? Number(Dictionary<string, object?> v, params string[] keys) =>
        Text(v, keys) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static List<string> List(Dictionary<string, object?> v, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!v.TryGetValue(k, out var o)) continue;
            if (o is List<string> list) return [.. list.Select(x => x.Trim()).Where(x => x.Length > 0)];
            if (o is string s) return [.. s.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0)];
        }
        return [];
    }

    private static Dictionary<string, string> Pairs(Dictionary<string, object?> v, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!v.TryGetValue(k, out var o)) continue;
            if (o is Dictionary<string, string> d) return d;
            if (o is string s)
                return s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => p.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Length > 0).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        }
        return [];
    }

    private static ImportRow ToRow(int line, Dictionary<string, object?> v) => new(
        line, Text(v, "category", "categoryid"), Text(v, "namear", "arabicname", "ar"), Text(v, "nameen", "englishname", "en"), List(v, "altnames", "alternatenames", "aliases"),
        Text(v, "address"), Text(v, "city"), Text(v, "country"), Number(v, "lat", "latitude"), Number(v, "lng", "lon", "long", "longitude"),
        Text(v, "googleplaceid", "placeid"), Text(v, "phone", "telephone"), Text(v, "whatsapp"), Text(v, "website", "url"), Pairs(v, "externalids"), Pairs(v, "fields"));
}
