using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Moderation.Services;
using Ratable.Shared.Content;

namespace Ratable.Api.Tests.Moderation;

/// <summary>Who reads a new or edited review first (rule 18, plan update 1). Every branch of the routing rules.</summary>
public sealed class RoutingPolicyTests
{
    private static RoutingInput Input(
        ContentKind kind = ContentKind.Review, bool aiCompleted = true, bool aiDeferred = false, string[]? flags = null,
        bool individual = false, bool proof = false, bool phoneVerified = false) =>
        new(kind, aiCompleted, aiDeferred, flags ?? [], individual, proof, phoneVerified);

    // ---- the three decided rules ----

    [Fact]
    public void An_unverified_accounts_review_without_proof_always_waits_for_a_moderator()
    {
        var decision = RoutingPolicy.Decide(Input(phoneVerified: false, proof: false));

        Assert.Equal(CaseRoute.Human, decision.Route);
        Assert.Equal([RoutingPolicy.NoProofUnverifiedAccount], decision.Reasons);
    }

    [Fact]
    public void A_verified_accounts_review_without_proof_is_published_only_when_the_checks_pass()
    {
        Assert.Equal(CaseRoute.Auto, RoutingPolicy.Decide(Input(phoneVerified: true)).Route);

        Assert.Equal(CaseRoute.Human, RoutingPolicy.Decide(Input(phoneVerified: true, flags: ["abuse"])).Route);
        Assert.Equal(CaseRoute.Human, RoutingPolicy.Decide(Input(phoneVerified: true, aiCompleted: false)).Route);
        Assert.Equal(CaseRoute.Human, RoutingPolicy.Decide(Input(phoneVerified: true, aiCompleted: false, aiDeferred: true)).Route);
    }

    [Fact]
    public void A_review_of_an_individual_always_goes_to_a_moderator_first_whatever_else_is_true()
    {
        foreach (var phone in new[] { true, false })
            foreach (var proof in new[] { true, false })
            {
                var decision = RoutingPolicy.Decide(Input(individual: true, proof: proof, phoneVerified: phone));
                Assert.Equal(CaseRoute.Human, decision.Route);
                Assert.Contains(RoutingPolicy.Individual, decision.Reasons);
            }
    }

    // ---- the rest of the matrix ----

    [Fact]
    public void A_clean_review_with_an_accepted_proof_is_published_even_from_an_unverified_phone() =>
        Assert.Equal(CaseRoute.Auto, RoutingPolicy.Decide(Input(proof: true, phoneVerified: false)).Route);

    [Fact]
    public void Anything_flagged_goes_to_a_moderator_even_with_a_proof_and_a_verified_phone()
    {
        var decision = RoutingPolicy.Decide(Input(proof: true, phoneVerified: true, flags: ["personal_data", "copied_text"]));

        Assert.Equal(CaseRoute.Human, decision.Route);
        Assert.Equal(["personal_data", "copied_text"], decision.Reasons);
    }

    [Theory]
    [InlineData(false, true, "ai_deferred")]
    [InlineData(false, false, "ai_unavailable")]
    public void An_ai_that_is_capped_or_down_is_never_treated_as_clean(bool completed, bool deferred, string reason)
    {
        var decision = RoutingPolicy.Decide(Input(aiCompleted: completed, aiDeferred: deferred, proof: true, phoneVerified: true));

        Assert.Equal(CaseRoute.Human, decision.Route);
        Assert.Equal([reason], decision.Reasons);
    }

    [Fact]
    public void Reasons_add_up_so_the_moderator_sees_all_of_them()
    {
        var decision = RoutingPolicy.Decide(Input(aiCompleted: false, flags: ["abuse", "review_burst"], individual: true));

        Assert.Equal(["ai_unavailable", "abuse", "review_burst", "individual"], decision.Reasons);
    }

    [Fact]
    public void The_individuals_rule_is_for_reviews_not_comments()
    {
        Assert.Equal(CaseRoute.Auto, RoutingPolicy.Decide(Input(kind: ContentKind.Comment, individual: true, phoneVerified: true)).Route);
        Assert.Equal(CaseRoute.Human, RoutingPolicy.Decide(Input(kind: ContentKind.Comment, phoneVerified: false)).Route);
    }

    [Fact]
    public void Only_waiting_for_proof_means_exactly_that_one_reason()
    {
        Assert.True(RoutingPolicy.OnlyWaitingForProof([RoutingPolicy.NoProofUnverifiedAccount]));
        Assert.False(RoutingPolicy.OnlyWaitingForProof([]));
        Assert.False(RoutingPolicy.OnlyWaitingForProof([RoutingPolicy.NoProofUnverifiedAccount, "abuse"]));
        Assert.False(RoutingPolicy.OnlyWaitingForProof(["individual"]));
    }

    [Fact]
    public void Nothing_but_the_listed_facts_feeds_the_routing()
    {
        // Payment, points and rank are not inputs: the routing is a function of exactly these facts (rule 6).
        var names = typeof(RoutingInput).GetProperties().Select(p => p.Name).Order().ToList();

        Assert.Equal(["AiCompleted", "AiDeferred", "AuthorPhoneVerified", "FlagReasons", "HasVerifiedProof", "Kind", "RatesIndividuals"], names);
    }
}
