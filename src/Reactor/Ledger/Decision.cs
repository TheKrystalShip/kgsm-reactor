using System.Security.Cryptography;
using System.Text;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Rules;

namespace TheKrystalShip.Kgsm.Reactor.Ledger;

/// <summary>What an evaluation came to.</summary>
/// <remarks>
/// <b>Every one of these is recorded, not just the ones that fired</b> (invariant 6). The evaluations
/// that decided <em>not</em> to act are the data the windows and the ceiling are tuned from, and
/// without them the review at P3 would be reading a success log rather than a decision record.
/// </remarks>
internal enum DecisionOutcome
{
    /// <summary>The condition held and the gate let it through.</summary>
    Fired,

    /// <summary>The condition had resolved itself by the time it was evaluated.</summary>
    Settled,

    /// <summary>It fired too recently for this rule and subject.</summary>
    Suppressed,

    /// <summary>The host has already acted as many times this hour as it may.</summary>
    Ceilinged,

    /// <summary>A more severe rule already spoke for this episode.</summary>
    Superseded,

    /// <summary>No judgment could be formed — the world, or the history, would not say.</summary>
    Unreadable,
}

/// <summary>
/// What recording a decision did to the record.
/// </summary>
/// <remarks>
/// The ledger folds every re-evaluation of one episode into a single row, so "recorded" says nothing
/// about whether anything is new. This is what separates a judgment that changed from a sweep that
/// found the world exactly as it left it — and therefore what decides whether a <c>reactor_decided</c>
/// line is written. Without it a condition open for six hours would append a line every thirty
/// seconds and the journal would read as how often the reactor looked rather than what it concluded.
/// </remarks>
internal enum DecisionChange
{
    /// <summary>The same verdict as last sweep. Nothing to announce.</summary>
    Unchanged,

    /// <summary>The first evaluation of this episode.</summary>
    Opened,

    /// <summary>The verdict on an episode already on record is not what it was.</summary>
    Changed,
}

/// <summary>How far the action got.</summary>
internal enum ActionState
{
    /// <summary>Nothing was dispatched, because the mode does not permit it.</summary>
    None,

    /// <summary>Staged for a human to confirm.</summary>
    Proposed,

    /// <summary>Handed to the engine.</summary>
    Dispatched,

    Succeeded,

    Failed,
}

/// <summary>
/// The journal line a decision came from.
/// </summary>
/// <remarks>
/// <b>Invariant 1 as a column rather than a promise.</b> A decision is derived; the journal is the
/// record. Carrying the position means anything reading a decision later can go and read the line it
/// was made from, instead of having to trust that this leaf described it correctly.
/// </remarks>
/// <param name="Producer">Whose journal.</param>
/// <param name="Segment">Which segment file.</param>
/// <param name="Offset">The byte offset in it.</param>
/// <param name="EventId">
/// The id the line's producer minted for it, or null when the line carries none.
/// </param>
internal readonly record struct EventSource(
    string Producer, string Segment, long Offset, string? EventId)
{
    /// <summary>
    /// What makes two evaluations of one episode the same decision.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The id is deliberately not in here.</b> A decision's own id is derived from this key, so
    /// changing the format renumbers every decision already on disk and in every published
    /// <c>reactor_decided</c> line. The id is the same string for the same line anyway, so including
    /// it would buy nothing and cost a migration.
    /// </remarks>
    public string Key => $"{Producer}:{Segment}:{Offset}";
}

/// <summary>One evaluation, as recorded.</summary>
/// <param name="Id">
/// Derived from the rule, the subject and the episode, so re-evaluating one episode refines a row
/// rather than growing a new one every sweep.
/// </param>
/// <param name="RuleId">Which rule. This is the actor string an audit row would carry.</param>
/// <param name="Subject">What it was about.</param>
/// <param name="SubjectKind">
/// What sort of thing that is. Carried from where the subject was observed rather than derived from
/// the name, because a consumer deriving it does so by looking the name up and seeing what it finds.
/// </param>
/// <param name="EpisodeKey">
/// The journal position of the event that opened the condition — what makes two evaluations of one
/// episode the same decision, and two separate failures two decisions.
/// </param>
/// <param name="Severity">The rule's severity, for composition.</param>
/// <param name="Mode">The mode it ran in.</param>
/// <param name="Outcome">What was decided.</param>
/// <param name="Reason">Why, in words. Always present.</param>
/// <param name="Action">What it would do, described rather than performed.</param>
/// <param name="ActionName">The same action as a stable name other programs compare against.</param>
/// <param name="ActionInstance">The server the action would operate on, or null when it operates on none.</param>
/// <param name="ActionState">How far that got.</param>
/// <param name="OpenedAt">When the condition began.</param>
/// <param name="DecidedAt">When this evaluation ran.</param>
/// <param name="Source">The journal line the whole thing traces back to.</param>
internal sealed record Decision(
    string Id,
    string RuleId,
    string Subject,
    SubjectKind SubjectKind,
    string EpisodeKey,
    Severity Severity,
    RuleMode Mode,
    DecisionOutcome Outcome,
    string Reason,
    string Action,
    string ActionName,
    string? ActionInstance,
    ActionState ActionState,
    DateTimeOffset OpenedAt,
    DateTimeOffset DecidedAt,
    EventSource Source)
{
    /// <summary>
    /// The deterministic id for one rule's verdict on one episode.
    /// </summary>
    /// <remarks>
    /// Content-derived on purpose here, unlike an observation — an observation is a distinct line of a
    /// file and must never be collapsed, where an episode re-evaluated every sweep is one decision
    /// being refined and must never be duplicated.
    /// </remarks>
    public static string IdFor(string ruleId, string subject, string episodeKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ruleId} {subject} {episodeKey}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
