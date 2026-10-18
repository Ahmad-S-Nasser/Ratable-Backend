using System.Buffers.Binary;

namespace Ratable.Modules.Media.Services;

/// <summary>
/// Removes location, device, time and author data from a photo without touching the picture: EXIF, XMP, IPTC and comments in JPEG; text, EXIF and time chunks
/// in PNG; EXIF and XMP chunks in WebP. The picture data is copied byte for byte (no re-encoding, so no quality loss). Returns null for a file that is not
/// a well-formed image of that type, and the caller refuses it.
/// </summary>
public static class ImageMetadataStripper
{
    public static byte[]? Strip(string contentType, byte[] data) => contentType switch
    {
        "image/jpeg" => StripJpeg(data),
        "image/png" => StripPng(data),
        "image/webp" => StripWebp(data),
        _ => null,
    };

    private static byte[]? StripJpeg(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        using var output = new MemoryStream(d.Length);
        output.Write(d, 0, 2);
        var i = 2;
        while (i + 1 < d.Length)
        {
            if (d[i] != 0xFF) return null;
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            if (marker == 0xDA) // start of scan: the rest is picture data
            {
                output.Write(d, i, d.Length - i);
                return output.ToArray();
            }
            if (marker is 0xD9) { output.Write(d, i, 2); return output.ToArray(); }
            if (i + 3 >= d.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2, 2));
            if (length < 2 || i + 2 + length > d.Length) return null;

            // Keep what the picture needs (JFIF APP0, Adobe APP14, tables, frame header); drop APP1 to APP13 (EXIF, XMP, ICC, IPTC) and comments.
            var drop = marker is >= 0xE1 and <= 0xED || marker == 0xFE;
            if (!drop) output.Write(d, i, 2 + length);
            i += 2 + length;
        }
        return null; // no scan data: not a usable picture
    }

    private static readonly HashSet<string> PngKeep = ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "sRGB", "gAMA", "cHRM"];

    private static byte[]? StripPng(byte[] d)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (d.Length < 12 || !d.AsSpan(0, 8).SequenceEqual(signature)) return null;
        using var output = new MemoryStream(d.Length);
        output.Write(d, 0, 8);
        var i = 8;
        var sawEnd = false;
        while (i + 12 <= d.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i, 4));
            if (length > int.MaxValue - 12 || i + 12L + length > d.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (PngKeep.Contains(type)) output.Write(d, i, (int)length + 12);
            i += (int)length + 12;
            if (type == "IEND") { sawEnd = true; break; }
        }
        return sawEnd ? output.ToArray() : null;
    }

    private static byte[]? StripWebp(byte[] d)
    {
        if (d.Length < 20 || !d.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !d.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return null;
        using var body = new MemoryStream(d.Length);
        var i = 12;
        while (i + 8 <= d.Length)
        {
            var tag = System.Text.Encoding.ASCII.GetString(d, i, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 4, 4));
            var padded = (long)size + (size & 1);
            if (i + 8 + padded > d.Length) return null;
            if (tag is not ("EXIF" or "XMP "))
            {
                var chunk = d.AsSpan(i, 8 + (int)padded).ToArray();
                if (tag == "VP8X" && size >= 10) chunk[8] &= 0xFF ^ 0x0C; // clear the "has EXIF" and "has XMP" flags
                body.Write(chunk);
            }
            i += 8 + (int)padded;
        }
        var result = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(4 + body.Length));
        "WEBP"u8.CopyTo(result.AsSpan(8));
        body.ToArray().CopyTo(result, 12);
        return result;
    }
}
