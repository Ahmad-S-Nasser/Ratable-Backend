using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Content;

namespace Ratable.Modules.Moderation.Services;

public sealed record RoutingInput(
    ContentKind Kind,
    /// <summary>The AI pre-screen ran and answered (or there was no text to screen).</summary>
    bool AiCompleted,
    /// <summary>The AI did not run because the daily cap was reached.</summary>
    bool AiDeferred,
    /// <summary>Reason codes from the pre-screen and the behaviour signals. Any of them means a person looks first.</summary>
    IReadOnlyList<string> FlagReasons,
    bool RatesIndividuals,
    bool HasVerifiedProof,
    bool AuthorPhoneVerified);

public sealed record RoutingDecision(CaseRoute Route, List<string> Reasons);

/// <summary>
/// Who looks at a new or edited review or comment first (CLAUDE.md rule 18, plan update 1). Pure rules, so every branch is tested:
/// anything the checks flag, an AI that is capped or down, and every review of an individual (a service provider) go to a moderator; otherwise a
/// review with an accepted proof is published; a review without proof is published only for an account with a verified phone, and waits for a
/// moderator for any other account. Verification never changes a score: this only decides who reads it first.
/// </summary>
public static class RoutingPolicy
{
    public const string NoProofUnverifiedAccount = "no_proof_unverified_account";
    public const string Individual = "individual";

    public static RoutingDecision Decide(RoutingInput input)
    {
        var reasons = new List<string>();
        if (!input.AiCompleted) reasons.Add(input.AiDeferred ? "ai_deferred" : "ai_unavailable");
        reasons.AddRange(input.FlagReasons);
        if (input.Kind == ContentKind.Review && input.RatesIndividuals) reasons.Add(Individual);

        if (reasons.Count > 0) return new(CaseRoute.Human, reasons);
        if (input.HasVerifiedProof) return new(CaseRoute.Auto, []);
        if (input.AuthorPhoneVerified) return new(CaseRoute.Auto, []);
        return new(CaseRoute.Human, [NoProofUnverifiedAccount]);
    }

    /// <summary>True when the only reason a person has to look is that there was no proof. A proof accepted later can then publish it.</summary>
    public static bool OnlyWaitingForProof(IReadOnlyCollection<string> reasons) => reasons.Count == 1 && reasons.Single() == NoProofUnverifiedAccount;
}
