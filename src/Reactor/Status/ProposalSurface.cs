using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Reactor.Actions;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Status;

/// <summary>
/// One offer, as a surface reads it.
/// </summary>
/// <remarks>
/// <para>
/// Its own shape rather than the stored row serialized: the row is this leaf's working state and free
/// to change with the code, where this is read by other programs.
/// </para>
/// <para>
/// ⚠ <b>The handle is here, and it is the capability.</b> Anything that can present it can ask for the
/// action — subject to the same authority performing that action directly requires, which is the
/// caller's to enforce and not this leaf's. A surface that put this list in front of a room of players
/// would be handing them the offer, not showing it to them.
/// </para>
/// </remarks>
public sealed record ProposalView
{
    /// <summary>The token this is redeemed with.</summary>
    [JsonPropertyName("handle")]
    public required string Handle { get; init; }

    /// <summary>The rule that offered it.</summary>
    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    /// <summary>
    /// Who had shaped that rule, as <c>provider:name</c>, or null.
    /// </summary>
    /// <remarks>
    /// ⚠ Provenance about the rule, never about the offer. Nobody proposed this; a rule did, and a
    /// person wrote the rule.
    /// </remarks>
    [JsonPropertyName("ruleAuthor")]
    public string? RuleAuthor { get; init; }

    /// <summary>What was judged.</summary>
    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    /// <summary>What sort of thing that is.</summary>
    [JsonPropertyName("subjectKind")]
    public required string SubjectKind { get; init; }

    /// <summary>How loudly the rule speaks.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>The action as a stable name, for a surface deciding what to draw.</summary>
    [JsonPropertyName("actionName")]
    public required string ActionName { get; init; }

    /// <summary>The action in words, as it is offered.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>The server it operates on, or null.</summary>
    [JsonPropertyName("actionInstance")]
    public string? ActionInstance { get; init; }

    /// <summary>
    /// Why the rule concluded it, carrying the figures.
    /// </summary>
    /// <remarks>
    /// <b>The sentence somebody decides on.</b> A confirm dialog without it asks a person to authorise
    /// an action on trust — and it is what was true when the offer was staged, which is not necessarily
    /// what is true now. The re-derivation at confirm time is what closes that gap.
    /// </remarks>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>The decision that staged it, so a reader can find the judgment behind the offer.</summary>
    [JsonPropertyName("decisionId")]
    public required string DecisionId { get; init; }

    /// <summary>When it was offered.</summary>
    [JsonPropertyName("stagedAt")]
    public required DateTimeOffset StagedAt { get; init; }

    /// <summary>When an unanswered offer stops being redeemable.</summary>
    [JsonPropertyName("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Where it has got to: <c>open</c>, or one of the four resolutions.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>When it ended, or null while it is open.</summary>
    [JsonPropertyName("answeredAt")]
    public DateTimeOffset? AnsweredAt { get; init; }

    /// <summary>Who ended it, or null when nobody did — which is the whole content of a lapse.</summary>
    [JsonPropertyName("answeredBy")]
    public string? AnsweredBy { get; init; }

    /// <summary>Whether the action succeeded, or null when none was attempted.</summary>
    /// <remarks>
    /// ⚠ Null is not false. Three of the four resolutions attempt nothing, and rendering a missing
    /// answer as a failure reports a person working as intended as a broken action.
    /// </remarks>
    [JsonPropertyName("ok")]
    public bool? Ok { get; init; }

    /// <summary>What the action produced — a backup id — or null.</summary>
    [JsonPropertyName("artifact")]
    public string? Artifact { get; init; }

    /// <summary>What went wrong, or the fresh verdict that made it inapplicable.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    internal static ProposalView Of(Proposal proposal) => new()
    {
        Handle = proposal.Handle,
        Rule = proposal.RuleId,
        RuleAuthor = proposal.RuleAuthor,
        Subject = proposal.Subject,
        SubjectKind = proposal.SubjectKind.ToString().ToLowerInvariant(),
        // The same spelling the journal carries, written out rather than lowercased from a name: a
        // surface classifies on this, and a rename would silently move every offer to the fallback
        // tone rather than failing.
        Severity = proposal.Severity.ToWire(),
        ActionName = proposal.ActionName,
        Action = proposal.Action,
        ActionInstance = proposal.ActionInstance,
        Reason = proposal.Reason,
        DecisionId = proposal.DecisionId,
        StagedAt = proposal.StagedAt,
        ExpiresAt = proposal.ExpiresAt,
        State = ProposalStore.Wire(proposal.State),
        AnsweredAt = proposal.AnsweredAt,
        AnsweredBy = proposal.AnsweredBy,
        Ok = proposal.Ok,
        Artifact = proposal.Artifact,
        Detail = proposal.Detail,
    };
}

/// <summary>
/// What this host is currently offering, and what it recently offered.
/// </summary>
/// <remarks>
/// Both halves in one answer because a surface needs both and asking twice would show them a moment
/// apart — an offer that lapsed between the two calls would appear in neither.
/// </remarks>
public sealed record ProposalBoard
{
    /// <summary>The most authority this build will honour, so a surface knows whether to expect any.</summary>
    /// <remarks>
    /// An empty board on a build that observes means nothing is being offered <em>by design</em>; the
    /// same board on a build that proposes means nothing has come up. A surface cannot tell those apart
    /// without this, and would render "nothing to answer" for a host that will never offer anything.
    /// </remarks>
    [JsonPropertyName("honours")]
    public required string Honours { get; init; }

    /// <summary>Offers waiting for an answer, soonest to expire first.</summary>
    [JsonPropertyName("open")]
    public required IReadOnlyList<ProposalView> Open { get; init; }

    /// <summary>
    /// Offers staged in the window asked for, whatever became of them, newest first.
    /// </summary>
    /// <remarks>
    /// Includes the open ones, so this is the history rather than the complement of it. A caller that
    /// wants only the ended ones filters on <c>state</c>, which it has to be able to read anyway.
    /// </remarks>
    [JsonPropertyName("recent")]
    public required IReadOnlyList<ProposalView> Recent { get; init; }

    /// <summary>How many days back <see cref="Recent"/> reaches.</summary>
    [JsonPropertyName("days")]
    public required int Days { get; init; }
}

/// <summary>
/// What came of redeeming a handle.
/// </summary>
/// <remarks>
/// ⚠ <b>Every field here is needed and none of them can be derived from another.</b> The outcome says
/// what happened to the offer; <c>ok</c> says what happened to the action; the detail says why, in the
/// words a person is shown. A caller that renders only the status code turns "the server came back up
/// on its own, so nothing was done" into a bare failure.
/// </remarks>
public sealed record RedemptionResult
{
    /// <summary>
    /// What came of it: <c>performed</c>, <c>failed</c>, <c>dismissed</c>, <c>no_longer_applicable</c>,
    /// <c>unknown</c>, <c>expired</c>, <c>already_answered</c>, <c>unattributable</c>,
    /// <c>unreadable</c>.
    /// </summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>Why, in words. Always present when anything other than the action happened.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>The offer as it now stands, or null when the handle named none.</summary>
    [JsonPropertyName("proposal")]
    public ProposalView? Proposal { get; init; }
}

/// <summary>Who is answering.</summary>
/// <remarks>
/// ⚠ <b>Required, and the leaf refuses a confirmation without it.</b> This is the one path where a
/// person authorises something, so the record it produces has to name them — and there is no fallback
/// to the OS user the daemon runs as. <b>Whether they are <em>allowed</em> to is the caller's
/// question:</b> the leaf holds no identity system and no tiers, so it checks the shape and trusts the
/// surface that authenticated them, which is the same split every other write on this host uses.
/// </remarks>
public sealed record RedemptionRequest
{
    /// <summary>The stable <c>provider:name</c> username, never a display name.</summary>
    [JsonPropertyName("by")]
    public string? By { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ProposalBoard))]
[JsonSerializable(typeof(RedemptionResult))]
[JsonSerializable(typeof(RedemptionRequest))]
public partial class ProposalJsonContext : JsonSerializerContext;
