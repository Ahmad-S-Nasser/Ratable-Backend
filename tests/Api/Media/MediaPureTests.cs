using System.Buffers.Binary;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Processing;
using Ratable.Modules.Media.Services;
using static Ratable.Api.Tests.Media.MediaKit;

namespace Ratable.Api.Tests.Media;

public sealed class MediaPureTests
{
    private static ProtectedRegion Face(double start = 1, double end = 4) => new() { Kind = "face", StartSeconds = start, EndSeconds = end, X = 0.25, Y = 0.1, Width = 0.2, Height = 0.3 };

    [Fact]
    public void The_stream_command_blurs_the_regions_drops_metadata_and_cuts_adaptive_hls()
    {
        var args = FfmpegCommands.Stream("in.mp4", "out/stream", new ProtectionPlan([Face(), Face(6, 9)], ChangeVoice: false), hasAudio: true);
        var line = string.Join(' ', args);

        Assert.Contains("-map_metadata -1", line);
        Assert.Contains("-map_chapters -1", line);
        Assert.Contains("-f hls", line);
        Assert.Contains("-var_stream_map v:0,a:0 v:1,a:1", line);
        Assert.Contains("-master_pl_name index.m3u8", line);
        Assert.Contains("scale=-2:720", line);
        Assert.Contains("scale=-2:480", line);
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.Equal(2, graph.Split("boxblur").Length - 1); // one blur per region
        Assert.Contains("enable='between(t\\,1\\,4)'", graph);
        Assert.Contains("enable='between(t\\,6\\,9)'", graph);
        Assert.Contains("crop=w=iw*0.2:h=ih*0.3:x=iw*0.25:y=ih*0.1", graph);
        Assert.DoesNotContain("asetrate", graph); // the voice is not changed for a named author
    }

    [Fact]
    public void An_anonymous_video_also_gets_a_changed_voice()
    {
        var args = FfmpegCommands.Stream("in.mp4", "out/stream", new ProtectionPlan([Face()], ChangeVoice: true), hasAudio: true);
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains(FfmpegCommands.VoiceChangeFilter, graph);
    }

    [Fact]
    public void A_video_without_sound_has_no_audio_mapping_and_no_regions_means_no_blur()
    {
        var args = FfmpegCommands.Stream("in.mp4", "out/stream", new ProtectionPlan([], false), hasAudio: false);
        var line = string.Join(' ', args);
        Assert.Contains("-var_stream_map v:0 v:1", line);
        Assert.DoesNotContain("-c:a", line);
        Assert.DoesNotContain("boxblur", line);
        Assert.Contains("[0:v]null[vout]", line);
    }

    [Fact]
    public void Regions_outside_the_picture_are_clamped()
    {
        var graph = FfmpegCommands.BlurGraph([new ProtectedRegion { Kind = "plate", StartSeconds = 0, EndSeconds = 2, X = 0.95, Y = -1, Width = 0.5, Height = 5 }]);
        Assert.Contains("x=iw*0.95", graph);
        Assert.Contains("y=ih*0", graph);
        Assert.Contains("w=iw*0.05", graph); // cannot run past the right edge
        Assert.Contains("h=ih*1", graph);
    }

    [Fact]
    public void Sample_and_poster_commands_never_keep_metadata()
    {
        Assert.Contains("-map_metadata", string.Join(' ', FfmpegCommands.SampleFrames("in.mp4", "f_%d.jpg", 8, 20)));
        Assert.Contains("-map_metadata", string.Join(' ', FfmpegCommands.PosterAt("v0.m3u8", 3, "p.jpg")));
        Assert.Contains("-map_metadata", string.Join(' ', FfmpegCommands.ExtractAudio("in.mp4", "a.mp3")));
    }

    [Fact]
    public void Sniffing_reads_the_real_type_from_the_first_bytes()
    {
        Assert.Equal("image/jpeg", FileTypes.Sniff(Jpeg()));
        Assert.Equal("video/mp4", FileTypes.Sniff(Mp4()));
        Assert.Equal("image/png", FileTypes.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
        Assert.Equal("video/webm", FileTypes.Sniff([0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0]));
        Assert.Null(FileTypes.Sniff("MZ program"u8));
        Assert.Null(FileTypes.Sniff("<html><script>"u8));
        Assert.True(FileTypes.Matches("video/mp4", "video/quicktime"));
        Assert.False(FileTypes.Matches("video/mp4", "image/jpeg"));
    }

    [Fact]
    public void A_jpeg_loses_exif_xmp_and_comments_but_keeps_the_picture()
    {
        var xmp = SegmentOf(0xE1, "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>PhoneModel</x:xmpmeta>");
        var comment = SegmentOf(0xFE, "taken by Sara");
        var jfif = SegmentOf(0xE0, "JFIF\0\u0001\u0001");
        var input = Jpeg(jfif, ExifSegment("GPSLatitude=30"), xmp, comment);

        var output = ImageMetadataStripper.Strip("image/jpeg", input)!;
        var text = System.Text.Encoding.Latin1.GetString(output);

        Assert.DoesNotContain("GPS", text);
        Assert.DoesNotContain("PhoneModel", text);
        Assert.DoesNotContain("Sara", text);
        Assert.Contains("JFIF", text); // what the picture needs stays
        Assert.True(output.AsSpan(^9).SequenceEqual(input.AsSpan(^9))); // scan data and end marker are byte for byte
        Assert.Null(ImageMetadataStripper.Strip("image/jpeg", [1, 2, 3, 4]));
    }

    [Fact]
    public void A_png_loses_text_exif_and_time_chunks()
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var png = signature.Concat(Chunk("IHDR", new byte[13])).Concat(Chunk("tEXt", System.Text.Encoding.ASCII.GetBytes("Author\0Sara")))
            .Concat(Chunk("eXIf", System.Text.Encoding.ASCII.GetBytes("GPSInfo"))).Concat(Chunk("tIME", new byte[7]))
            .Concat(Chunk("IDAT", [1, 2, 3])).Concat(Chunk("IEND", [])).ToArray();

        var output = ImageMetadataStripper.Strip("image/png", png)!;
        var text = System.Text.Encoding.Latin1.GetString(output);

        Assert.DoesNotContain("Sara", text);
        Assert.DoesNotContain("GPSInfo", text);
        Assert.DoesNotContain("tIME", text);
        Assert.Contains("IHDR", text);
        Assert.Contains("IDAT", text);
        Assert.Contains("IEND", text);
        Assert.Null(ImageMetadataStripper.Strip("image/png", signature)); // no end chunk: not a usable picture
    }

    [Fact]
    public void A_webp_loses_exif_and_xmp_chunks_and_the_flags_that_announce_them()
    {
        var vp8x = new byte[10];
        vp8x[0] = 0x0C | 0x10; // has EXIF, has XMP, plus another flag
        var body = Riff("VP8X", vp8x).Concat(Riff("VP8 ", [1, 2, 3, 4])).Concat(Riff("EXIF", System.Text.Encoding.ASCII.GetBytes("GPSInfo"))).Concat(Riff("XMP ", System.Text.Encoding.ASCII.GetBytes("Phone"))).ToArray();
        var file = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + body.Length));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 12);

        var output = ImageMetadataStripper.Strip("image/webp", file)!;
        var text = System.Text.Encoding.Latin1.GetString(output);

        Assert.DoesNotContain("GPSInfo", text);
        Assert.DoesNotContain("Phone", text);
        Assert.Contains("VP8 ", text);
        Assert.Equal(0x10, output[20]); // the EXIF and XMP flags are cleared, the other flag stays
        Assert.Equal((uint)(output.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4, 4)));
    }

    private static byte[] SegmentOf(byte marker, string text)
    {
        var body = System.Text.Encoding.Latin1.GetBytes(text);
        var length = body.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. body];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var bytes = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        data.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Riff(string tag, byte[] data)
    {
        var padded = data.Length + (data.Length & 1);
        var bytes = new byte[8 + padded];
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length);
        data.CopyTo(bytes, 8);
        return bytes;
    }
}
