using Ratable.Shared.Text;

namespace Ratable.Api.Tests.Text;

public sealed class ArabicNormalizerTests
{
    // ---- each rule on its own ----

    [Theory]
    [InlineData("مُحَمَّد", "محمد")]            // fatha, damma, shadda
    [InlineData("كِتَابٌ", "كتاب")]              // tanwin
    [InlineData("هٰذا", "هذا")]                  // dagger alef U+0670
    [InlineData("الْمَدْرَسَةُ", "المدرسه")]        // sukun, damma, taa marbuta
    public void Diacritics_are_removed(string input, string expected) => Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    [Theory]
    [InlineData("مــدينتي", "مدينتي")]
    [InlineData("الـرحـاب", "الرحاب")]
    public void Tatweel_is_removed(string input, string expected) => Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    [Theory]
    [InlineData("أكاديمية", "اكاديميه")]
    [InlineData("إسكان", "اسكان")]
    [InlineData("آمن", "امن")]
    [InlineData("ٱلله", "الله")]
    public void Alef_variants_become_plain_alef(string input, string expected) => Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    [Theory]
    [InlineData("مستشفى", "مستشفي")]
    [InlineData("هدى", "هدي")]
    public void Alef_maqsura_becomes_yaa(string input, string expected) => Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    [Theory]
    [InlineData("مدرسة", "مدرسه")]
    [InlineData("الجامعة", "الجامعه")]
    public void Taa_marbuta_becomes_haa(string input, string expected) => Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    [Theory]
    [InlineData("مؤسسة", "موسسه")]
    [InlineData("رئيس", "رييس")]
    [InlineData("مسئول", "مسيول")]
    public void Hamza_on_waw_and_yaa_is_dropped(string input, string expected) => Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    [Theory]
    [InlineData("٢٠٢٦", "2026")]
    [InlineData("۲۰۲۶", "2026")]
    [InlineData("فيلا ١٢٣", "فيلا 123")]
    [InlineData("Tower ٧", "tower 7")]
    public void Eastern_and_persian_digits_become_western(string input, string expected) => Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    [Theory]
    [InlineData("  مدينتي   الجديدة  ", "مدينتي الجديده")]
    [InlineData("a\tb\n\nc", "a b c")]
    [InlineData("مدينتي​الجديدة", "مدينتيالجديده")]            // zero-width space joins
    [InlineData("‏مدينتي‎", "مدينتي")]                      // direction marks
    [InlineData("﻿مدينتي", "مدينتي")]                            // byte order mark
    [InlineData("PALM Hills", "palm hills")]
    [InlineData("Mountain View iCity", "mountain view icity")]
    public void Zero_width_characters_and_extra_spaces_are_cleaned_and_latin_is_lowercased(string input, string expected) =>
        Assert.Equal(expected, ArabicNormalizer.Normalize(input));

    // ---- the whole thing ----

    [Theory]
    [InlineData("أكاديمية", "اكاديميه")]
    [InlineData("مدرسة", "مدرسه")]
    [InlineData("مدينتى", "مدينتي")]
    [InlineData("كمبوند الرحاب ٢", "كمبوند الرحاب 2")]
    [InlineData("مَدْرَسَـــة", "مدرسة")]
    [InlineData("المؤسسة الأهلية", "الموسسه الاهليه")]
    public void Spelling_variants_of_one_text_normalize_to_the_same_string(string a, string b) =>
        Assert.Equal(ArabicNormalizer.Normalize(a), ArabicNormalizer.Normalize(b));

    [Theory]
    [InlineData("مدينتي", "مدينة نصر")]
    [InlineData("الرحاب", "الربوة")]
    [InlineData("سوديك ايست", "سوديك ويست")]
    [InlineData("Palm Hills", "Palm Parks")]
    [InlineData("مدرسة النيل", "مدرسة الفرات")]
    public void Different_texts_do_not_become_equal(string a, string b) =>
        Assert.NotEqual(ArabicNormalizer.Normalize(a), ArabicNormalizer.Normalize(b));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("​‏")]
    [InlineData("ً َ ُ")]
    public void Empty_or_only_marks_normalizes_to_empty(string input) => Assert.Equal("", ArabicNormalizer.Normalize(input));

    [Fact]
    public void Null_normalizes_to_empty() => Assert.Equal("", ArabicNormalizer.Normalize(null));

    [Theory]
    [InlineData("مدينتي")]
    [InlineData("palm hills 2026")]
    [InlineData("اكاديميه الشروق")]
    public void Normalizing_twice_changes_nothing(string input)
    {
        var once = ArabicNormalizer.Normalize(input);
        Assert.Equal(once, ArabicNormalizer.Normalize(once));
    }

    [Fact]
    public void The_original_text_is_never_changed()
    {
        const string original = "أكاديمية الشروق ٢٠٢٦";
        var copy = new string(original.ToCharArray());

        _ = ArabicNormalizer.Normalize(original);

        Assert.Equal(original, copy);
    }

    [Fact]
    public void Characters_that_are_not_arabic_or_latin_letters_are_kept()
    {
        Assert.Equal("café 5-star", ArabicNormalizer.Normalize("Café 5-Star"));
    }
}

public sealed class SluggerTests
{
    [Theory]
    [InlineData("Palm Hills October", "palm-hills-october")]
    [InlineData("  Mountain   View -- iCity!  ", "mountain-view-icity")]
    [InlineData("أكاديمية الشروق", "اكاديميه-الشروق")]
    [InlineData("مدينتي ٢٠٢٦", "مدينتي-2026")]
    [InlineData("مَدْرَسَـة النيل", "مدرسه-النيل")]
    public void Slugs_are_built_from_the_normalized_text(string input, string expected) => Assert.Equal(expected, Slugger.Make(input));

    [Fact]
    public void Spelling_variants_give_the_same_slug() => Assert.Equal(Slugger.Make("أكاديمية"), Slugger.Make("اكاديميه"));

    [Fact]
    public void A_long_name_is_cut_without_a_trailing_hyphen()
    {
        var slug = Slugger.Make(string.Join(' ', Enumerable.Repeat("word", 40)), maxLength: 20);
        Assert.True(slug.Length <= 20);
        Assert.False(slug.EndsWith('-'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!! ---")]
    public void Nothing_to_slug_gives_an_empty_string(string? input) => Assert.Equal("", Slugger.Make(input));
}
