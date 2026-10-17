using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Ratable.Modules.Verification.Services;
using Ratable.Modules.Verification.Storage;

namespace Ratable.Api.Tests.Verification;

public sealed class ProofFileInspectorTests
{
    [Fact]
    public void Real_pdf_png_jpeg_and_webp_files_are_recognised_by_their_first_bytes()
    {
        Assert.Equal("application/pdf", ProofFileInspector.ContentTypeOf(TestKit.Pdf()));
        Assert.Equal("image/png", ProofFileInspector.ContentTypeOf(TestKit.Png()));
        Assert.Equal("image/jpeg", ProofFileInspector.ContentTypeOf([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1]));
        Assert.Equal("image/webp", ProofFileInspector.ContentTypeOf(Encoding.ASCII.GetBytes("RIFF1234WEBPVP8 ")));
    }

    [Fact]
    public void Anything_else_is_refused_whatever_it_is_called()
    {
        Assert.Null(ProofFileInspector.ContentTypeOf("MZ this is a windows program renamed to proof.pdf"u8));
        Assert.Null(ProofFileInspector.ContentTypeOf("<html><script>alert(1)</script></html>"u8));
        Assert.Null(ProofFileInspector.ContentTypeOf("GIF89a............"u8));
        Assert.Null(ProofFileInspector.ContentTypeOf("%PDF"u8)); // too short to be anything
        Assert.Null(ProofFileInspector.ContentTypeOf([]));
    }
}

public sealed class LocalDiskProofStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ratable-storage-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private LocalDiskProofStorage Make(string? key = null) => new(Options.Create(new VerificationOptions
    {
        StoragePath = _dir,
        StorageKey = key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    }));

    [Fact]
    public async Task A_saved_file_reads_back_the_same_but_is_not_readable_on_disk()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var storage = Make(key);
        var secret = Encoding.UTF8.GetBytes("Receipt for Palm Hills unit 14, owner Sara Ahmed, card 4111 1111 1111 1111");
        var id = LocalDiskProofStorage.NewKey();

        await storage.SaveAsync(id, secret);

        Assert.Equal(secret, await storage.ReadAsync(id));
        var onDisk = await File.ReadAllBytesAsync(Directory.GetFiles(_dir, id, SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("Sara", Encoding.UTF8.GetString(onDisk));
        Assert.DoesNotContain("4111", Encoding.UTF8.GetString(onDisk));
    }

    [Fact]
    public async Task The_same_content_saved_twice_looks_different_on_disk()
    {
        var storage = Make();
        var content = TestKit.Png();
        var a = LocalDiskProofStorage.NewKey();
        var b = LocalDiskProofStorage.NewKey();

        await storage.SaveAsync(a, content);
        await storage.SaveAsync(b, content);

        Assert.NotEqual(
            await File.ReadAllBytesAsync(Directory.GetFiles(_dir, a, SearchOption.AllDirectories).Single()),
            await File.ReadAllBytesAsync(Directory.GetFiles(_dir, b, SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task A_different_key_or_a_changed_file_cannot_be_read()
    {
        var id = LocalDiskProofStorage.NewKey();
        await Make().SaveAsync(id, TestKit.Png());

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Make().ReadAsync(id)); // another key

        var path = Directory.GetFiles(_dir, id, SearchOption.AllDirectories).Single();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var storage = Make(key);
        var id2 = LocalDiskProofStorage.NewKey();
        await storage.SaveAsync(id2, TestKit.Png());
        var path2 = Directory.GetFiles(_dir, id2, SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(path2);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path2, bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => storage.ReadAsync(id2));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_deleting_twice_is_fine()
    {
        var storage = Make();
        var id = LocalDiskProofStorage.NewKey();
        await storage.SaveAsync(id, TestKit.Png());
        Assert.True(await storage.ExistsAsync(id));

        await storage.DeleteAsync(id);
        await storage.DeleteAsync(id);

        Assert.False(await storage.ExistsAsync(id));
        Assert.Null(await storage.ReadAsync(id));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system.ini")]
    [InlineData("proof.pdf")]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public async Task Only_generated_keys_are_accepted_so_no_path_can_be_smuggled_in(string key)
    {
        var storage = Make();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(key, TestKit.Png()));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(key));
    }

    [Fact]
    public async Task Without_a_storage_key_nothing_is_stored_in_the_clear()
    {
        var storage = new LocalDiskProofStorage(Options.Create(new VerificationOptions { StoragePath = _dir, StorageKey = "" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync(LocalDiskProofStorage.NewKey(), TestKit.Png()));
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public void Generated_keys_are_random_hex()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => LocalDiskProofStorage.NewKey()).ToList();

        Assert.Equal(50, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches("^[a-f0-9]{32}$", k));
    }
}

public sealed class ProofDateAndSamplerTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static List<string> Dates(string? date, string category, VerificationOptions? options = null)
    {
        var checker = new ProofChecker(null!, null!, Options.Create(options ?? new VerificationOptions()));
        return [.. checker.CheckDate(date, category, Now)];
    }

    [Theory]
    [InlineData("2026-10-01", "hotels-resorts")]
    [InlineData("2025-10-18", "hotels-resorts")]   // just inside 12 months
    [InlineData("2025-01-01", "appliances")]       // inside 24 months
    [InlineData("2026-10-18", "hotels-resorts")]   // one day ahead is tolerated (time zones)
    [InlineData("2019-01-01", "restaurants-cafes")] // no limit for this category
    public void A_date_in_range_is_accepted(string date, string category) => Assert.Empty(Dates(date, category));

    [Theory]
    [InlineData("2025-10-16", "hotels-resorts")]
    [InlineData("2024-10-01", "appliances")]
    [InlineData("2020-01-01", "compounds")]
    public void A_date_older_than_the_limit_is_flagged(string date, string category) => Assert.Equal(["date_out_of_range"], Dates(date, category));

    [Theory]
    [InlineData("2026-10-19", "hotels-resorts")]
    [InlineData("2027-01-01", "restaurants-cafes")]
    public void A_date_in_the_future_is_flagged_even_without_a_limit(string date, string category) => Assert.Equal(["date_in_future"], Dates(date, category));

    [Theory]
    [InlineData(null, "hotels-resorts", "date_missing")]
    [InlineData("", "appliances", "date_missing")]
    [InlineData("17/10/2026", "compounds", "date_missing")]
    [InlineData("not a date", "hotels-resorts", "date_missing")]
    public void A_missing_or_unreadable_date_is_flagged_where_the_category_has_a_limit(string? date, string category, string expected) =>
        Assert.Equal([expected], Dates(date, category));

    [Fact]
    public void A_missing_date_is_fine_where_the_category_has_no_limit() => Assert.Empty(Dates(null, "restaurants-cafes"));

    [Fact]
    public void The_limits_come_from_configuration()
    {
        var options = new VerificationOptions { MaxAgeMonths = new() { ["cars"] = 3 } };

        Assert.Equal(["date_out_of_range"], Dates("2026-05-01", "cars", options));
        Assert.Empty(Dates("2026-09-01", "cars", options));
    }

    [Fact]
    public void The_default_audit_rate_is_five_percent() => Assert.Equal(0.05, new VerificationOptions().AuditSampleRate);

    [Fact]
    public void The_sampler_picks_about_the_configured_share_and_never_or_always_at_the_ends()
    {
        var sampler = new RandomProofSampler();

        var picked = Enumerable.Range(0, 100_000).Count(_ => sampler.ShouldAudit(0.05));

        Assert.InRange(picked, 4_000, 6_000);
        Assert.DoesNotContain(true, Enumerable.Range(0, 1_000).Select(_ => sampler.ShouldAudit(0)));
        Assert.DoesNotContain(false, Enumerable.Range(0, 1_000).Select(_ => sampler.ShouldAudit(1)));
    }
}
