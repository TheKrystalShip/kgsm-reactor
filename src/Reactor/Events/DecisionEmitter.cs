using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Kgsm.Reactor.Events;

/// <summary>Puts a decision on the journal, where anything on this host can read it.</summary>
internal interface IDecisionEmitter
{
    /// <summary>
    /// Write <paramref name="decision"/> as a <c>reactor_decided</c> line.
    /// </summary>
    /// <returns>
    /// True when the line was appended. <b>False is not an error to throw on</b> — a journal that
    /// cannot be written is a reason to keep judging and say so, not a reason to stop.
    /// </returns>
    ValueTask<bool> EmitAsync(Decision decision, CancellationToken token = default);
}

/// <summary>
/// The <c>reactor_decided</c> payload, as it is written.
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
                .AppendAsync(ReactorEvents.Decided, data, ActorFor(decision.RuleId), Origin, token)
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
        Severity = Spell(decision.Severity),
        Mode = Spell(decision.Mode),
        Outcome = Spell(decision.Outcome),
        Reason = decision.Reason,
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
    /// Lower-case, underscore-separated — the spelling every other event payload on this host uses for
    /// an enumerated value, and the one a consumer can compare against without knowing C# casing.
    /// </summary>
    private static string Spell<T>(T value) where T : struct, Enum =>
        value.ToString()!.ToLowerInvariant();
}
