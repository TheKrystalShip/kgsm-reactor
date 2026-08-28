using System.Security.Cryptography;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Actions;

/// <summary>
/// Where a staged proposal has got to.
/// </summary>
/// <remarks>
/// <para>
/// One open state and four ends, and the ends are <see cref="ReactorResolutions"/> — the same
/// vocabulary the journal carries, so a row and the line written about it cannot disagree about what
/// happened.
/// </para>
/// <para>
/// <b>Every proposal reaches exactly one end.</b> Nothing is deleted and nothing stays open forever:
/// the sweep lapses whatever has run out, which is what makes "how did this rule's offers end" a
/// question the ledger can answer over a week rather than a count of what is still lying around.
/// </para>
/// </remarks>
internal enum ProposalState
{
    /// <summary>Staged, unanswered, and inside its lifetime.</summary>
    Open,

    /// <summary>A person said yes and the action was attempted.</summary>
    Confirmed,

    /// <summary>A person said no.</summary>
    Dismissed,

    /// <summary>Nobody answered before its lifetime ran out.</summary>
    Lapsed,

    /// <summary>
    /// Somebody tried to confirm it and the condition had gone by then.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>This is the safety property working, not a fault.</b> The rule is re-evaluated at
    /// redemption rather than trusted from staging time, so a server that came back up on its own turns
    /// a confirmed restore into this instead of overwriting a running world. It is what lets the
    /// lifetime be hours.
    /// </remarks>
    NoLongerApplicable,
}

/// <summary>
/// An action a rule staged for a person to confirm.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reactor holds this, and it has to.</b> Confirming re-derives the condition, which means
/// re-evaluating the rule — and the reactor is the only thing on the host that can. Holding it here
/// also keeps the leaf standalone: with kgsm-api absent the proposals still exist and are still
/// redeemable, because the API is a surface onto this rather than the place it lives.
/// </para>
/// <para>
/// ⚠ <b>It carries the sentence, not a reference to it.</b> <see cref="Reason"/> is copied from the
/// decision that staged it for the same reason the decision copies its rule's author: a proposal a
/// person reads at seven in the morning has to say what was true when it was staged, and resolving
/// that through a rule somebody has since edited would show them a sentence no rule ever produced.
/// </para>
/// </remarks>
/// <param name="Handle">The token this is redeemed with.</param>
/// <param name="DecisionId">The decision that staged it, which is stable per rule, subject and episode.</param>
/// <param name="RuleId">The rule that decided. Provenance on the resulting action, never its actor.</param>
/// <param name="RuleAuthor">Who had shaped that rule, as <c>provider:name</c>, or null.</param>
/// <param name="Subject">What was judged.</param>
/// <param name="SubjectKind">What sort of thing that is.</param>
/// <param name="EpisodeKey">The journal position of the line that opened the condition.</param>
/// <param name="Severity">How loudly the rule speaks.</param>
/// <param name="ActionName">The action as a stable name.</param>
/// <param name="Action">The action in words, as it is offered.</param>
/// <param name="ActionInstance">The server it operates on, or null.</param>
/// <param name="Reason">Why the rule concluded it, carrying the figures.</param>
/// <param name="OpenedAt">
/// When the condition began, or null when nothing observed it beginning.
/// <para>
/// Carried so the sentence a person is shown when they confirm dates the condition from the same
/// instant the offer did. Re-deriving it then would date a crash loop from the second look rather than
/// the first, and an offer answered in the morning would read as a fault that had just started.
/// </para>
/// </param>
/// <param name="StagedAt">When it was offered.</param>
/// <param name="ExpiresAt">When an unanswered offer stops being redeemable.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="AnsweredAt">When it ended, or null while it is open.</param>
/// <param name="AnsweredBy">Who ended it, as <c>provider:name</c>, or null when nobody did.</param>
/// <param name="Ok">Whether the action succeeded, or null when none was attempted.</param>
/// <param name="Artifact">What the action produced — a backup id — or null.</param>
/// <param name="Detail">What went wrong, or the fresh verdict that made it inapplicable.</param>
internal sealed record Proposal(
    string Handle,
    string DecisionId,
    string RuleId,
    string? RuleAuthor,
    string Subject,
    SubjectKind SubjectKind,
    string EpisodeKey,
    EventSeverity Severity,
    string ActionName,
    string Action,
    string? ActionInstance,
    string Reason,
    DateTimeOffset? OpenedAt,
    DateTimeOffset StagedAt,
    DateTimeOffset ExpiresAt,
    ProposalState State = ProposalState.Open,
    DateTimeOffset? AnsweredAt = null,
    string? AnsweredBy = null,
    bool? Ok = null,
    string? Artifact = null,
    string? Detail = null)
{
    /// <summary>Whether it is still open and still inside its lifetime.</summary>
    public bool IsRedeemableAt(DateTimeOffset now) => State == ProposalState.Open && now < ExpiresAt;

    /// <summary>
    /// A fresh handle.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Cryptographic randomness, because the handle is the capability.</b> Anything holding one
    /// can ask for the action it names — subject to the same authority performing it directly requires
    /// — so a guessable handle would be a way around the authority rather than a way to it. Sixteen
    /// bytes, spelled as thirty-two lower-case hex characters, matching the shape the assistant's own
    /// confirmations use so one surface can render both.
    /// </remarks>
    public static string NewHandle() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
