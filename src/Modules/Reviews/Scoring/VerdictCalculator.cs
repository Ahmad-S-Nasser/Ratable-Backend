using Ratable.Modules.Reviews.Models;

namespace Ratable.Modules.Reviews.Scoring;

/// <summary>Settings of the verdict sticker, in configuration ("Reviews:Verdicts") so they can change without code.</summary>
public sealed class VerdictOptions
{
    public const string Section = "Reviews:Verdicts";

    /// <summary>A verdict is shown only after this many verified reviews chose one (the same minimum as a criterion score).</summary>
    public int MinVotes { get; set; } = 5;

    /// <summary>The leading choice must have at least this share of the verified votes, or no sticker is shown (opinions are mixed).</summary>
    public double MinShare { get; set; } = 0.5;

    /// <summary>A Hidden gem needs a high score ...</summary>
    public double HiddenGemMinScore { get; set; } = 4.3;

    /// <summary>... and few verified reviews.</summary>
    public int HiddenGemMaxReviews { get; set; } = 30;
}

public static class Verdicts
{
    public const string Worth = "worth";
    public const string NotWorth = "not_worth";
    public const string HiddenGem = "hidden_gem";

    public static readonly string[] All = [Worth, NotWorth, HiddenGem];
}

/// <summary>The sticker: which verdict, what share of the verified reviewers who gave one chose it, and how many gave one.</summary>
public sealed record VerdictResult(string Kind, int SharePercent, int Votes);

/// <summary>
/// The verdict of a place (CLAUDE.md rule 13), worked out only from Published, Verified reviews, so an unverified, hidden, pending or deleted review
/// never counts. It sits next to the score and is never part of it: this class reads the reviews' verdicts and the finished score, and cannot change
/// either. Payment is not an input.
/// </summary>
public static class VerdictCalculator
{
    public static VerdictResult? ForEntity(IEnumerable<Review> reviews, double? overallScore, int verifiedReviewCount, VerdictOptions options)
    {
        var votes = reviews.Where(ScoreCalculator.Counts).Select(r => r.Verdict).Where(v => v is not null && Verdicts.All.Contains(v)).ToList();
        if (votes.Count < options.MinVotes) return null;

        var worth = votes.Count(v => v == Verdicts.Worth);
        var notWorth = votes.Count(v => v == Verdicts.NotWorth);
        var gem = votes.Count(v => v == Verdicts.HiddenGem);

        // A hidden gem needs a high score and few reviews. A place that does not qualify keeps the votes as plain "worth it": someone who calls it a gem
        // certainly thinks it is worth it.
        var gemQualifies = overallScore >= options.HiddenGemMinScore && verifiedReviewCount <= options.HiddenGemMaxReviews;
        var tally = gemQualifies
            ? new Dictionary<string, int> { [Verdicts.HiddenGem] = gem, [Verdicts.Worth] = worth, [Verdicts.NotWorth] = notWorth }
            : new Dictionary<string, int> { [Verdicts.Worth] = worth + gem, [Verdicts.NotWorth] = notWorth };

        var leader = tally.OrderByDescending(kv => kv.Value).First();
        if (tally.Count(kv => kv.Value == leader.Value) > 1) return null; // a tie is not a verdict
        var share = (double)leader.Value / votes.Count;
        return share >= options.MinShare ? new VerdictResult(leader.Key, (int)Math.Round(share * 100), votes.Count) : null;
    }
}
