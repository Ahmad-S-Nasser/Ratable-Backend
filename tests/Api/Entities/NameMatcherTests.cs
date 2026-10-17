using Ratable.Modules.Entities.Matching;

namespace Ratable.Api.Tests.Entities;

public sealed class NameMatcherTests
{
    private const double Candidate = 0.6; // EntitiesOptions.CandidateThreshold

    // ---- the same place under spelling variants must match ----

    [Theory]
    [InlineData("أكاديمية الشروق", "اكاديميه الشروق")]
    [InlineData("مدرسة النيل الدولية", "مدرسه النيل الدوليه")]
    [InlineData("مدينتى", "مدينتي")]
    [InlineData("كمبوند الرحاب", "الرحاب")]
    [InlineData("الرحاب", "رحاب")]
    [InlineData("Palm Hills", "palm  hills")]
    [InlineData("Palm Hills Compound", "Palm Hills")]
    [InlineData("Mountain View iCity", "iCity Mountain View")]
    [InlineData("برج ٢٠٢٦", "برج 2026")]
    [InlineData("Mountainview iCity", "Mountainvew iCity")] // one-letter typo in a long word
    [InlineData("فندق النيل", "نيل")]
    public void Spelling_variants_of_the_same_name_score_one(string a, string b)
    {
        Assert.Equal(1.0, NameMatcher.Score(a, b));
        Assert.Equal(1.0, NameMatcher.Score(b, a));
    }

    // ---- different places with similar names must not be treated as the same ----

    [Theory]
    [InlineData("Sodic East", "Sodic West")]
    [InlineData("Palm Hills", "Palm Parks")]
    [InlineData("Zed East", "Zed West")]
    [InlineData("مدينتي", "مدينة نصر")]
    [InlineData("الرحاب", "الربوة")]
    [InlineData("مدرسة النيل", "مدرسة الفرات")]
    [InlineData("سوديك ايست", "سوديك ويست")]
    [InlineData("Compound One", "Compound Two")]
    [InlineData("Hyde Park", "Hyde Park Residences New Cairo East")]
    public void Different_places_with_similar_names_stay_below_the_candidate_threshold(string a, string b)
    {
        Assert.True(NameMatcher.Score(a, b) < Candidate, $"{a} vs {b} = {NameMatcher.Score(a, b)}");
        Assert.True(NameMatcher.Score(b, a) < Candidate);
    }

    [Theory]
    [InlineData("Palm Hills October", "Palm Hills Heights")]   // two of three words: a candidate for the AI judge, not a match
    public void Partly_matching_names_are_candidates_but_not_a_match(string a, string b)
    {
        var score = NameMatcher.Score(a, b);
        Assert.InRange(score, Candidate, 0.99);
    }

    [Theory]
    [InlineData("east", "west")]
    [InlineData("ايست", "ويست")]
    public void Short_words_that_differ_by_one_letter_never_match(string a, string b) => Assert.Equal(0, NameMatcher.Score(a, b));

    // ---- behaviour ----

    [Fact]
    public void Word_order_does_not_matter() => Assert.Equal(1.0, NameMatcher.Score("Nile International School", "International School Nile"));

    [Fact]
    public void A_word_is_matched_only_once()
    {
        Assert.True(NameMatcher.Score("Park Park", "Park Hills") < 1.0);
    }

    [Fact]
    public void Generic_words_alone_have_no_content_to_match()
    {
        Assert.Equal(0, NameMatcher.Score("Compound", "Compound"));
        Assert.Equal(0, NameMatcher.Score("مدرسة", "مدرسة"));
        Assert.Equal(0, NameMatcher.Score("", "Palm Hills"));
        Assert.Equal(0, NameMatcher.Score(null, null));
    }

    [Fact]
    public void The_best_pair_of_names_counts_across_languages_and_alternates()
    {
        var a = new string?[] { "مدينتي", "Madinaty" };
        var b = new string?[] { "", "Madinety Compound", "Madinaty" };

        Assert.Equal(1.0, NameMatcher.BestScore(a, b));
        Assert.Equal(0, NameMatcher.BestScore(["مدينتي"], ["Madinaty"])); // an Arabic and an English name are never compared by spelling
    }

    [Fact]
    public void Tokens_drop_generic_words_and_the_article()
    {
        Assert.Equal(["رحاب"], NameMatcher.Tokens("كمبوند الرحاب"));
        Assert.Equal(["palm", "hills"], NameMatcher.Tokens("The Palm Hills Compound"));
        Assert.Equal(["ال"], NameMatcher.Tokens("ال")); // too short to strip
    }
}

public sealed class GeoTests
{
    [Fact]
    public void Distance_between_known_points_is_about_right()
    {
        // Tahrir Square to the Giza pyramids is roughly 13 km.
        var d = Geo.DistanceMeters(31.2357, 30.0444, 31.1342, 29.9792);
        Assert.InRange(d, 11_000, 15_000);
    }

    [Fact]
    public void Distance_to_the_same_point_is_zero_and_is_symmetric()
    {
        Assert.Equal(0, Geo.DistanceMeters(31.2, 30.0, 31.2, 30.0), 3);
        Assert.Equal(Geo.DistanceMeters(31.2, 30.0, 29.9, 31.2), Geo.DistanceMeters(29.9, 31.2, 31.2, 30.0), 3);
    }
}
