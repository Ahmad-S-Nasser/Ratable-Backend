using Ratable.Modules.Reviews.Models;

namespace Ratable.Modules.Reviews.Scoring;

public sealed record CriterionScore(string CriterionId, int AnswerCount, double? Score);

public sealed record EntityScore(double? Overall, int VerifiedReviewCount, IReadOnlyList<CriterionScore> Criteria);

/// <summary>
/// How scores are worked out (CLAUDE.md rule 3). Only Published reviews with Verification Level Verified count: unverified, pending, hidden and
/// deleted reviews are ignored. A per-criterion score appears only after <see cref="MinAnswersForCriterion"/> answers. Nothing else, points and
/// payments included, may influence a score.
/// </summary>
public static class ScoreCalculator
{
    /// <summary>A per-criterion score is shown only after this many answers (rule 3).</summary>
    public const int MinAnswersForCriterion = 5;

    /// <summary>The overall score of ONE review: the average of the criteria it answered. Null when it answered none.</summary>
    public static double? ReviewOverall(IReadOnlyCollection<ReviewAnswer> answers) =>
        answers.Count == 0 ? null : answers.Average(a => (double)a.Score);

    public static bool Counts(Review review) =>
        review is { Status: ReviewStatus.Published, Verification.Level: VerificationLevel.Verified };

    /// <summary>
    /// The entity's score. The overall score is the average of the reviews' overall scores, so every verified review weighs the same
    /// however many criteria it answered. Scores are rounded to two decimals.
    /// </summary>
    public static EntityScore ForEntity(IEnumerable<Review> reviews)
    {
        var counted = reviews.Where(Counts).ToList();
        var overalls = counted.Select(r => ReviewOverall(r.Answers)).Where(o => o.HasValue).Select(o => o!.Value).ToList();

        var criteria = counted
            .SelectMany(r => r.Answers)
            .GroupBy(a => a.CriterionId)
            .Select(g => new CriterionScore(g.Key, g.Count(), g.Count() >= MinAnswersForCriterion ? Math.Round(g.Average(a => (double)a.Score), 2) : null))
            .OrderBy(c => c.CriterionId, StringComparer.Ordinal)
            .ToList();

        return new EntityScore(overalls.Count == 0 ? null : Math.Round(overalls.Average(), 2), counted.Count, criteria);
    }
}
