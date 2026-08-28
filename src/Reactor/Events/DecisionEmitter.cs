using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Reactor.Actions;
using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Core.Interfaces;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Events;

/// <summary>
/// Puts what the reactor concluded, offered and did on the journal, where anything on this host can
/// read it.
/// </summary>
/// <remarks>
/// ⚠ <b>Every method here returns whether the line landed, and none of them throws.</b> A journal that
/// will not take a line costs downstream its notice; it is never a reason to stop judging, and it is
/// never a reason to undo something already performed. The ledger is the record either way.
/// </remarks>
internal interface IDecisionEmitter
{
    /// <summary>
    /// Write <paramref name="decision"/> as a <c>reactor.decided</c> line.
    /// </summary>
    /// <returns>
    /// True when the line was appended. <b>False is not an error to throw on</b> — a journal that
    /// cannot be written is a reason to keep judging and say so, not a reason to stop.
    /// </returns>
    ValueTask<bool> EmitAsync(Decision decision, CancellationToken token = default);

    /// <summary>Write <paramref name="proposal"/> as a <c>reactor.proposed</c> line.</summary>
    ValueTask<bool> EmitProposedAsync(Proposal proposal, CancellationToken token = default);

    /// <summary>
    /// Write an ended proposal as a <c>reactor.resolved</c> line.
    /// </summary>
    /// <remarks>
    /// Takes the proposal as it stands <em>after</em> ending, so the line and the row carry one answer
    /// rather than the line carrying arguments the row was built from.
    /// </remarks>
    ValueTask<bool> EmitResolvedAsync(Proposal proposal, CancellationToken token = default);

    /// <summary>
    /// Write an autonomous action as a <c>reactor.acted</c> line.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Only for act mode.</b> An action a person confirmed is a resolution carrying their name;
    /// writing one here too would double-count what this host did on its own.
    /// </remarks>
    ValueTask<bool> EmitActedAsync(Decision decision, ActionResult result, CancellationToken token = default);
}

/// <summary>
/// The <c>reactor.decided</c> payload, as it is written.
/// </summary>
/// <remarks>
/// A record of its own rather than the <see cref="Decision"/> serialized directly: the ledger row is
/// this leaf's working state and is free to change with the code, where this is read by other
/// programs. Keeping them apart means a column can be added tomorrow without silently widening a
/// contract.
/// </remarks>
internal sealed record DecidedPayload
{
    [JsonPropertyName(ReactorEventFields.Rule)]
    public required string Rule { get; init; }

    /// <summary>Null when nobody is known to have shaped the rule — never a host or a daemon name.</summary>
    [JsonPropertyName(ReactorEventFields.RuleAuthor)]
    public string? RuleAuthor { get; init; }

    [JsonPropertyName(ReactorEventFields.Subject)]
    public required string Subject { get; init; }

    [JsonPropertyName(ReactorEventFields.SubjectKind)]
    public required string SubjectKind { get; init; }

    [JsonPropertyName(ReactorEventFields.Severity)]
    public required string Severity { get; init; }

    [JsonPropertyName(ReactorEventFields.Mode)]
    public required string Mode { get; init; }

    [JsonPropertyName(ReactorEventFields.Outcome)]
    public required string Outcome { get; init; }

    [JsonPropertyName(ReactorEventFields.Reason)]
    public required string Reason { get; init; }

    [JsonPropertyName(ReactorEventFields.Action)]
    public required string Action { get; init; }

    /// <summary>Null when the action operates on no server — never an empty string standing in.</summary>
    [JsonPropertyName(ReactorEventFields.ActionInstance)]
    public string? ActionInstance { get; init; }

    [JsonPropertyName(ReactorEventFields.DecisionId)]
    public required string DecisionId { get; init; }

    [JsonPropertyName(ReactorEventFields.OpenedAt)]
    public required DateTimeOffset OpenedAt { get; init; }

    [JsonPropertyName(ReactorEventFields.SourceProducer)]
    public required string SourceProducer { get; init; }

    [JsonPropertyName(ReactorEventFields.SourceSegment)]
    public required string SourceSegment { get; init; }

    [JsonPropertyName(ReactorEventFields.SourceOffset)]
    public required long SourceOffset { get; init; }

    /// <summary>Null when the originating line carries no id — never an empty string standing in.</summary>
    [JsonPropertyName(ReactorEventFields.SourceEventId)]
    public string? SourceEventId { get; init; }
}

/// <summary>
/// The <c>reactor.proposed</c> payload, as it is written.
/// </summary>
/// <remarks>
/// It carries the sentence and the figures rather than a pointer to the decision, for the same reason
/// <see cref="DecidedPayload"/> does: a consumer has to be able to render an offer from the one line,
/// and a join is a second read that can fail while the first succeeded.
/// </remarks>
internal sealed record ProposedPayload
{
    [JsonPropertyName(ReactorEventFields.Rule)]
    public required string Rule { get; init; }

    /// <summary>Null when nobody is known to have shaped the rule — never a host or a daemon name.</summary>
    [JsonPropertyName(ReactorEventFields.RuleAuthor)]
    public string? RuleAuthor { get; init; }

    [JsonPropertyName(ReactorEventFields.Subject)]
    public required string Subject { get; init; }

    [JsonPropertyName(ReactorEventFields.SubjectKind)]
    public required string SubjectKind { get; init; }

    [JsonPropertyName(ReactorEventFields.Severity)]
    public required string Severity { get; init; }

    [JsonPropertyName(ReactorEventFields.Reason)]
    public required string Reason { get; init; }

    [JsonPropertyName(ReactorEventFields.Action)]
    public required string Action { get; init; }

    /// <summary>Null when the action operates on no server — never an empty string standing in.</summary>
    [JsonPropertyName(ReactorEventFields.ActionInstance)]
    public string? ActionInstance { get; init; }

    [JsonPropertyName(ReactorEventFields.DecisionId)]
    public required string DecisionId { get; init; }

    [JsonPropertyName(ReactorEventFields.ProposalHandle)]
    public required string ProposalHandle { get; init; }

    [JsonPropertyName(ReactorEventFields.ExpiresAt)]
    public required DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName(ReactorEventFields.OpenedAt)]
    public required DateTimeOffset OpenedAt { get; init; }
}

/// <summary>
/// The <c>reactor.resolved</c> payload, as it is written.
/// </summary>
/// <remarks>
/// ⚠ <b><see cref="Ok"/> is nullable and stays nullable.</b> Three of the four resolutions attempt
/// nothing, and a <c>false</c> written for them would report a person working as intended as a broken
/// action.
/// </remarks>
internal sealed record ResolvedPayload
{
    [JsonPropertyName(ReactorEventFields.Rule)]
    public required string Rule { get; init; }

    [JsonPropertyName(ReactorEventFields.Subject)]
    public required string Subject { get; init; }

    [JsonPropertyName(ReactorEventFields.Action)]
    public required string Action { get; init; }

    [JsonPropertyName(ReactorEventFields.ActionInstance)]
    public string? ActionInstance { get; init; }

    [JsonPropertyName(ReactorEventFields.DecisionId)]
    public required string DecisionId { get; init; }

    [JsonPropertyName(ReactorEventFields.ProposalHandle)]
    public required string ProposalHandle { get; init; }

    [JsonPropertyName(ReactorEventFields.Resolution)]
    public required string Resolution { get; init; }

    /// <summary>Null when nobody answered, which is the whole content of a lapse.</summary>
    [JsonPropertyName(ReactorEventFields.AnsweredBy)]
    public string? AnsweredBy { get; init; }

    /// <summary>Null when no action was attempted.</summary>
    [JsonPropertyName(ReactorEventFields.Ok)]
    public bool? Ok { get; init; }

    [JsonPropertyName(ReactorEventFields.Artifact)]
    public string? Artifact { get; init; }

    [JsonPropertyName(ReactorEventFields.Detail)]
    public string? Detail { get; init; }
}

/// <summary>The <c>reactor.acted</c> payload, as it is written.</summary>
internal sealed record ActedPayload
{
    [JsonPropertyName(ReactorEventFields.Rule)]
    public required string Rule { get; init; }

    [JsonPropertyName(ReactorEventFields.Subject)]
    public required string Subject { get; init; }

    [JsonPropertyName(ReactorEventFields.Action)]
    public required string Action { get; init; }

    [JsonPropertyName(ReactorEventFields.ActionInstance)]
    public string? ActionInstance { get; init; }

    [JsonPropertyName(ReactorEventFields.DecisionId)]
    public required string DecisionId { get; init; }

    [JsonPropertyName(ReactorEventFields.Ok)]
    public required bool Ok { get; init; }

    [JsonPropertyName(ReactorEventFields.Artifact)]
    public string? Artifact { get; init; }

    [JsonPropertyName(ReactorEventFields.Detail)]
    public string? Detail { get; init; }
}

/// <summary>
/// The serializer for what this leaf writes.
/// </summary>
/// <remarks>
/// Source-generated, because this binary is Native AOT and there is no reflection fallback to catch a
/// type nobody registered — it throws at runtime instead. Its own context rather than kgsm-lib's:
/// writing goes through <c>IEventJournalWriter.AppendAsync(string, JsonElement, …)</c>, which needs no
/// type from the library at all, so emission costs no package release and no consumer re-pin.
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(DecidedPayload))]
[JsonSerializable(typeof(ProposedPayload))]
[JsonSerializable(typeof(ResolvedPayload))]
[JsonSerializable(typeof(ActedPayload))]
internal partial class ReactorJsonContext : JsonSerializerContext;

/// <inheritdoc cref="IDecisionEmitter"/>
internal sealed class DecisionEmitter(
    IEventJournalWriter writer,
    ILogger<DecisionEmitter> logger) : IDecisionEmitter
{
    /// <summary>
    /// The surface that drove it (invariant 2). Not <c>system</c>: an autonomous decision by this leaf
    /// is a different fact from the engine acting with nobody behind it, and a reader has to be able to
    /// tell them apart.
    /// </summary>
    private const string Origin = "reactor";

    /// <summary>
    /// Invariant 2's <em>"actor the rule id"</em>, written in the ecosystem's <c>provider:name</c> actor
    /// shape so it parses like every other actor on the host rather than as a bare word.
    /// </summary>
    /// <remarks>
    /// This is what makes an audit row read <em>"by rule give_up_backup"</em> rather than naming a
    /// person or nothing at all. The payload repeats the rule id unprefixed, because the acceptance
    /// test is a consumer rendering from the payload alone.
    /// </remarks>
    private static string ActorFor(string ruleId) => $"rule:{ruleId}";

    public async ValueTask<bool> EmitAsync(Decision decision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        DecidedPayload payload = PayloadFor(decision);

        try
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                payload, ReactorJsonContext.Default.DecidedPayload);

            return await writer
                .AppendAsync(
                    ReactorEvents.DecidedName, data, ActorFor(decision.RuleId), Origin,
                    decision.Severity,
                    // A decision neither succeeded nor failed — it is a judgement, and the reactor's own
                    // richer outcome (fired, settled, suppressed, ceilinged, superseded, unreadable) is
                    // in the payload where it keeps its meaning. Flattening it here would lose four of
                    // the six.
                    EventOutcome.Neutral,
                    SummaryFor(decision),
                    token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The ledger already holds it. A journal that will not take the line costs downstream its
            // notice of the decision, which is worth a loud log and nothing more drastic: a reactor
            // that stopped judging because it could not announce would be a worse failure.
            logger.LogError(ex, "Could not write {Event} for {Rule} on {Subject}.",
                ReactorEvents.Decided, decision.RuleId, decision.Subject);
            return false;
        }
    }

    public async ValueTask<bool> EmitProposedAsync(Proposal proposal, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var payload = new ProposedPayload
        {
            Rule = proposal.RuleId,
            RuleAuthor = proposal.RuleAuthor,
            Subject = proposal.Subject,
            SubjectKind = Spell(proposal.SubjectKind),
            Severity = proposal.Severity.ToWire(),
            Reason = proposal.Reason,
            Action = proposal.ActionName,
            ActionInstance = proposal.ActionInstance,
            DecisionId = proposal.DecisionId,
            ProposalHandle = proposal.Handle,
            ExpiresAt = proposal.ExpiresAt,
            OpenedAt = proposal.StagedAt,
        };

        return await WriteAsync(
            ReactorEvents.ProposedName, proposal.RuleId,
            JsonSerializer.SerializeToElement(payload, ReactorJsonContext.Default.ProposedPayload),
            proposal.Severity,
            // Neutral: an offer is a question, and an outcome here would have a surface colour it as a
            // result. What became of it is the resolution's to report.
            EventOutcome.Neutral,
            // The fault first and the offer second, which is the order somebody decides in: what is
            // wrong, then what is being proposed about it.
            $"{proposal.Reason}. {Sentence($"offers to {proposal.Action}")} Waiting for somebody to confirm.",
            ReactorEvents.Proposed, proposal.Subject, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> EmitResolvedAsync(Proposal proposal, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        string resolution = ProposalStore.Wire(proposal.State);

        var payload = new ResolvedPayload
        {
            Rule = proposal.RuleId,
            Subject = proposal.Subject,
            Action = proposal.ActionName,
            ActionInstance = proposal.ActionInstance,
            DecisionId = proposal.DecisionId,
            ProposalHandle = proposal.Handle,
            Resolution = resolution,
            AnsweredBy = proposal.AnsweredBy,
            Ok = proposal.Ok,
            Detail = proposal.Detail,
            Artifact = proposal.Artifact,
        };

        return await WriteAsync(
            ReactorEvents.ResolvedName, proposal.RuleId,
            JsonSerializer.SerializeToElement(payload, ReactorJsonContext.Default.ResolvedPayload),
            proposal.Severity,
            // Neutral as a family: a dismissal is a person working as intended and a failed confirm is
            // not, and one outcome cannot be both. A consumer reads Resolution and Ok.
            EventOutcome.Neutral,
            Describe(proposal, resolution),
            ReactorEvents.Resolved, proposal.Subject, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> EmitActedAsync(
        Decision decision, ActionResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var payload = new ActedPayload
        {
            Rule = decision.RuleId,
            Subject = decision.Subject,
            Action = decision.ActionName,
            ActionInstance = decision.ActionInstance,
            DecisionId = decision.Id,
            Ok = result.Ok,
            Artifact = result.Artifact,
            Detail = result.Detail,
        };

        return await WriteAsync(
            ReactorEvents.ActedName, decision.RuleId,
            JsonSerializer.SerializeToElement(payload, ReactorJsonContext.Default.ActedPayload),
            decision.Severity,
            // The ACTION's outcome, unlike the other three: something was attempted and either worked
            // or did not, and that is exactly what this event reports.
            result.Ok ? EventOutcome.Success : EventOutcome.Failure,
            // Past tense from the performer, which is the only thing that knows what actually
            // happened — including the id of whatever it produced. The rule's description of the
            // action is written in the infinitive and belongs to an offer, not to a completed one.
            result.Ok
                ? $"{decision.Reason}. {Sentence(result.Detail ?? "done")}"
                : $"{decision.Reason}. {Sentence($"tried to {decision.Action} and could not: {result.Detail}")}",
            ReactorEvents.Acted, decision.Subject, token).ConfigureAwait(false);
    }

    /// <summary>
    /// One ended proposal in the words a person reads.
    /// </summary>
    /// <remarks>
    /// Each resolution gets its own sentence rather than a shared template with the word substituted:
    /// "nobody answered" and "the condition had gone" are different things to have happened, and a
    /// reader skimming a journal is who this is for.
    /// </remarks>
    private static string Describe(Proposal proposal, string resolution) => proposal.State switch
    {
        ProposalState.Confirmed when proposal.Ok is true =>
            $"{proposal.AnsweredBy} authorised the offer to {proposal.Action}. "
            + Sentence(proposal.Detail ?? "done"),

        ProposalState.Confirmed =>
            $"{proposal.AnsweredBy} authorised the offer to {proposal.Action} and it could not be "
            + $"done: {proposal.Detail}",

        ProposalState.Dismissed => $"{proposal.AnsweredBy} declined the offer to {proposal.Action}",

        // ⚠ It says the condition was never re-checked, because that is the part a reader gets wrong.
        // An offer expiring is this leaf giving up on being answered, and says nothing whatever about
        // whether the server is still broken.
        ProposalState.Lapsed =>
            $"nobody answered the offer to {proposal.Action}. It expired after "
            + $"{(proposal.ExpiresAt - proposal.StagedAt).TotalHours:F0}h, and whether the condition "
            + "still holds was never re-checked",

        ProposalState.NoLongerApplicable =>
            $"{proposal.AnsweredBy} authorised the offer to {proposal.Action}, and the condition had "
            + $"gone by then, so nothing was done: {proposal.Detail}",

        _ => $"{resolution}: {proposal.Action}",
    };

    /// <summary>
    /// Appends one line, and answers whether it landed.
    /// </summary>
    /// <remarks>
    /// Shared by all four events because the failure handling is the part that must not differ: a
    /// journal that will not take a line is logged loudly and nothing else, and an action already
    /// performed is certainly not undone because its announcement did not land.
    /// </remarks>
    private async ValueTask<bool> WriteAsync(
        EventName name, string ruleId, JsonElement data, EventSeverity severity, EventOutcome outcome,
        string summary, string type, string subject, CancellationToken token)
    {
        try
        {
            return await writer
                .AppendAsync(name, data, ActorFor(ruleId), Origin, severity, outcome, summary, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write {Event} for {Rule} on {Subject}.", type, ruleId, subject);
            return false;
        }
    }

    /// <summary>
    /// One decision in the words a person reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule's own sentence, and then what the reactor made of it.</b> A reason names its own
    /// subject and stands alone, so this adds only what the reason cannot know: whether anything
    /// followed from it. The reason itself is never rewritten here.
    /// </para>
    /// <para>
    /// The three outcomes that mean <em>the condition holds and the reactor is staying quiet</em> share
    /// one lead-in, because which gate stopped it is what the reason already goes on to say — and the
    /// wire outcome in the payload is what a program reads. Spelling them apart here would put the
    /// same distinction in front of a reader twice, in different words.
    /// </para>
    /// </remarks>
    internal static string SummaryFor(Decision decision) => decision.Outcome switch
    {
        // A report-only rule's reason IS its output. "Would do nothing" beside it says less than
        // nothing — it invites a reader to look for an action that was never the point.
        DecisionOutcome.Fired when decision.ActionName == ActionCatalog.None => decision.Reason,
        DecisionOutcome.Fired => $"{decision.Reason}. {Sentence($"would {decision.Action}")}",
        DecisionOutcome.Settled => $"cleared on its own: {decision.Reason}",
        DecisionOutcome.Unreadable => $"cannot tell: {decision.Reason}",
        _ => $"holds, and this stays quiet: {decision.Reason}",
    };

    /// <summary>
    /// One decision as the payload that is written.
    /// </summary>
    /// <remarks>
    /// Separated from the append so a test can assert the shape a consumer will read without a
    /// journal, a directory or a clock — and, more to the point, so what it asserts is this mapping
    /// rather than a second copy of it written beside the test.
    /// </remarks>
    internal static DecidedPayload PayloadFor(Decision decision) => new()
    {
        Rule = decision.RuleId,
        Subject = decision.Subject,
        SubjectKind = Spell(decision.SubjectKind),
        Severity = decision.Severity.ToWire(),
        Mode = Spell(decision.Mode),
        Outcome = Spell(decision.Outcome),
        Reason = decision.Reason,
        RuleAuthor = decision.RuleAuthor,
        Action = decision.ActionName,
        ActionInstance = decision.ActionInstance,
        DecisionId = decision.Id,
        OpenedAt = decision.OpenedAt,
        SourceProducer = decision.Source.Producer,
        SourceSegment = decision.Source.Segment,
        SourceOffset = decision.Source.Offset,
        SourceEventId = decision.Source.EventId,
    };

    /// <summary>
    /// A clause written to stand as its own sentence after a full stop: capitalised, and closed.
    /// </summary>
    /// <remarks>
    /// Every fragment joined here is written lower-case and open, because each is also read on its
    /// own — a rule's action reads <em>"would archive it"</em> in an editor and a performer's detail is
    /// the body of a log line. Punctuating them at the source would put a stray capital in the middle
    /// of every other sentence they appear in.
    /// </remarks>
    private static string Sentence(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        // Only the first letter, and only when it is one: an id, a version or a figure at the front of
        // a sentence is spelled the way the thing it names is spelled.
        string opened = char.IsLower(trimmed[0])
            ? char.ToUpperInvariant(trimmed[0]) + trimmed[1..]
            : trimmed;

        return opened.EndsWith('.') || opened.EndsWith('!') || opened.EndsWith('?')
            ? opened
            : opened + ".";
    }

    /// <summary>
    /// Lower-case, underscore-separated — the spelling every other event payload on this host uses for
    /// an enumerated value, and the one a consumer can compare against without knowing C# casing.
    /// </summary>
    private static string Spell<T>(T value) where T : struct, Enum =>
        value.ToString()!.ToLowerInvariant();
}
