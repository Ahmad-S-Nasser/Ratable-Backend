using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Scoring;

namespace Ratable.Api.Tests.Reviews;

public sealed class ScoreCalculatorTests
{
    private static Review R(ReviewStatus status = ReviewStatus.Published, VerificationLevel level = VerificationLevel.Verified, params (string Criterion, int Score)[] answers) => new()
    {
        Status = status,
        Verification = new ReviewVerification { Level = level },
        Answers = [.. answers.Select(a => new ReviewAnswer { CriterionId = a.Criterion, Score = a.Score })],
    };

    private static Review Verified(params (string Criterion, int Score)[] answers) => R(ReviewStatus.Published, VerificationLevel.Verified, answers);

    // ---- one review ----

    [Fact]
    public void A_reviews_overall_is_the_average_of_the_criteria_it_answered()
    {
        Assert.Equal(4.0, ScoreCalculator.ReviewOverall(Verified(("a", 5), ("b", 3)).Answers));
        Assert.Equal(2.0, ScoreCalculator.ReviewOverall(Verified(("a", 2)).Answers));
    }

    [Fact]
    public void A_review_with_no_answers_has_no_overall() => Assert.Null(ScoreCalculator.ReviewOverall([]));

    // ---- rule 3: only Verified reviews count ----

    [Fact]
    public void Only_published_verified_reviews_count()
    {
        var score = ScoreCalculator.ForEntity(
        [
            Verified(("a", 5)),
            R(ReviewStatus.Published, VerificationLevel.Unverified, ("a", 1)),
            R(ReviewStatus.PendingModeration, VerificationLevel.Verified, ("a", 1)),
            R(ReviewStatus.Hidden, VerificationLevel.Verified, ("a", 1)),
            R(ReviewStatus.Deleted, VerificationLevel.Verified, ("a", 1)),
        ]);

        Assert.Equal(1, score.VerifiedReviewCount);
        Assert.Equal(5.0, score.Overall);
    }

    [Theory]
    [InlineData(ReviewStatus.Published, VerificationLevel.Verified, true)]
    [InlineData(ReviewStatus.Published, VerificationLevel.Unverified, false)]
    [InlineData(ReviewStatus.PendingModeration, VerificationLevel.Verified, false)]
    [InlineData(ReviewStatus.Hidden, VerificationLevel.Verified, false)]
    [InlineData(ReviewStatus.Deleted, VerificationLevel.Verified, false)]
    public void Counts_matches_the_rule(ReviewStatus status, VerificationLevel level, bool expected) =>
        Assert.Equal(expected, ScoreCalculator.Counts(R(status, level, ("a", 3))));

    [Fact]
    public void With_no_verified_reviews_there_is_no_score()
    {
        var score = ScoreCalculator.ForEntity([R(ReviewStatus.Published, VerificationLevel.Unverified, ("a", 5))]);

        Assert.Null(score.Overall);
        Assert.Equal(0, score.VerifiedReviewCount);
        Assert.Empty(score.Criteria);
    }

    // ---- rule 3: a criterion needs 5 answers ----

    [Fact]
    public void A_criterion_score_is_hidden_until_five_answers()
    {
        var four = Enumerable.Range(0, 4).Select(_ => Verified(("security", 5))).ToList();
        var hidden = ScoreCalculator.ForEntity(four).Criteria.Single();
        Assert.Equal(4, hidden.AnswerCount);
        Assert.Null(hidden.Score);

        var five = ScoreCalculator.ForEntity([.. four, Verified(("security", 3))]).Criteria.Single();
        Assert.Equal(5, five.AnswerCount);
        Assert.Equal(4.6, five.Score);
    }

    [Fact]
    public void Unverified_answers_do_not_count_towards_the_five()
    {
        var reviews = Enumerable.Range(0, 4).Select(_ => Verified(("security", 5)))
            .Append(R(ReviewStatus.Published, VerificationLevel.Unverified, ("security", 5)))
            .Append(R(ReviewStatus.Deleted, VerificationLevel.Verified, ("security", 5))).ToList();

        Assert.Null(ScoreCalculator.ForEntity(reviews).Criteria.Single().Score);
    }

    [Fact]
    public void The_minimum_is_five()
    {
        Assert.Equal(5, ScoreCalculator.MinAnswersForCriterion);
    }

    [Fact]
    public void Each_criterion_is_counted_on_its_own()
    {
        var reviews = Enumerable.Range(0, 5).Select(_ => Verified(("security", 4), ("noise", 2))).Append(Verified(("security", 4))).ToList();

        var criteria = ScoreCalculator.ForEntity(reviews).Criteria.ToDictionary(c => c.CriterionId);

        Assert.Equal(6, criteria["security"].AnswerCount);
        Assert.Equal(4.0, criteria["security"].Score);
        Assert.Equal(5, criteria["noise"].AnswerCount);
        Assert.Equal(2.0, criteria["noise"].Score);
    }

    // ---- the overall score of the entity ----

    [Fact]
    public void Every_verified_review_weighs_the_same_however_many_criteria_it_answered()
    {
        // Review 1 answered one criterion with 5; review 2 answered four criteria with 1. Equal weight per review: (5 + 1) / 2 = 3.
        var score = ScoreCalculator.ForEntity([Verified(("a", 5)), Verified(("a", 1), ("b", 1), ("c", 1), ("d", 1))]);

        Assert.Equal(3.0, score.Overall);
    }

    [Fact]
    public void Scores_are_rounded_to_two_decimals() =>
        Assert.Equal(3.67, ScoreCalculator.ForEntity([Verified(("a", 5)), Verified(("a", 5)), Verified(("a", 1))]).Overall);

    [Fact]
    public void Criteria_come_back_in_a_stable_order()
    {
        var ids = ScoreCalculator.ForEntity([Verified(("noise", 1), ("a", 1), ("security", 1))]).Criteria.Select(c => c.CriterionId);

        Assert.Equal(["a", "noise", "security"], ids);
    }

    [Fact]
    public void Nothing_but_the_reviews_answers_feeds_the_score()
    {
        // The score is a pure function of status, verification and answers. This guards against adding other inputs (points, plans, payments).
        var parameters = typeof(ScoreCalculator).GetMethod(nameof(ScoreCalculator.ForEntity))!.GetParameters();

        Assert.Single(parameters);
        Assert.Equal(typeof(IEnumerable<Review>), parameters[0].ParameterType);
        Assert.DoesNotContain(typeof(Review).GetProperties(), p => p.Name.Contains("Points", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Plan", StringComparison.OrdinalIgnoreCase));
    }
}
