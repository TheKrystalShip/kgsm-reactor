using System.Text.Json.Serialization;

namespace TheKrystalShip.Kgsm.Reactor.Status;

/// <summary>
/// What the reactor is doing right now.
/// </summary>
/// <remarks>
/// <para>
/// The question neither of the other two surfaces answers. The journal says what was concluded and
/// arrives only when something changes; <c>--report</c> reads the ledger and describes a population
/// over days. Neither can say <em>"there are two evaluations waiting out their settle window and the
/// last sweep was eleven seconds ago"</em>, which is what somebody asks when they are standing in
/// front of a host wondering whether this thing is alive.
/// </para>
/// <para>
/// ⚠ <b>Every counter here is since this process started</b>, not since the beginning. A restart
/// resets them, and they are named so that is unmistakable — a total that quietly meant something
/// else after a deploy would be worse than no total.
/// </para>
/// </remarks>
public sealed record ReactorStatus
{
    /// <summary>The producer id this leaf writes its journal under.</summary>
    [JsonPropertyName("leaf")]
    public required string Leaf { get; init; }

    /// <summary>The running build, version and commit.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>When this process started.</summary>
    [JsonPropertyName("startedAt")]
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>How long it has been up, in seconds.</summary>
    [JsonPropertyName("uptimeSeconds")]
    public required long UptimeSeconds { get; init; }

    /// <summary>
    /// Whether it is observing at all.
    /// </summary>
    /// <remarks>
    /// False is a configured silence, not a fault: the daemon runs, reports ready, and records
    /// nothing. Without this a deliberately quiet reactor and a broken one look identical.
    /// </remarks>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    /// <summary>The ledger it is writing to.</summary>
    [JsonPropertyName("ledgerPath")]
    public required string LedgerPath { get; init; }

    /// <summary>The gate's tuning, as it is actually running.</summary>
    [JsonPropertyName("gate")]
    public required GateStatus Gate { get; init; }

    /// <summary>What has been ingested since start.</summary>
    [JsonPropertyName("observations")]
    public required IngestStatus Observations { get; init; }

    /// <summary>What has been judged since start.</summary>
    [JsonPropertyName("decisions")]
    public required DecisionStatus Decisions { get; init; }

    /// <summary>Every rule that is live, and the authority it runs under.</summary>
    [JsonPropertyName("rules")]
    public required IReadOnlyList<RuleStatus> Rules { get; init; }

    /// <summary>
    /// Evaluations woken and waiting out their settle window.
    /// </summary>
    /// <remarks>
    /// The most useful thing here during an incident: it is the difference between a reactor that has
    /// not noticed and one that has noticed and is deliberately waiting to see whether the condition
    /// resolves itself.
    /// </remarks>
    [JsonPropertyName("pending")]
    public required IReadOnlyList<PendingStatus> Pending { get; init; }

    /// <summary>
    /// When rules were last evaluated, or null if no sweep has completed yet.
    /// </summary>
    /// <remarks>
    /// Null on a reactor whose first sweep has not landed. Reported as null rather than as the start
    /// time, which would read as a sweep that happened.
    /// </remarks>
    [JsonPropertyName("lastSweepAt")]
    public DateTimeOffset? LastSweepAt { get; init; }
}

/// <summary>The gate's tuning, read off the running configuration.</summary>
/// <param name="SweepIntervalSeconds">How often rules are evaluated.</param>
/// <param name="SuppressionWindowMinutes">How long one rule stays quiet about one subject.</param>
/// <param name="MaxActionsPerHour">The host-wide ceiling. Zero means none.</param>
public sealed record GateStatus(
    [property: JsonPropertyName("sweepIntervalSeconds")] int SweepIntervalSeconds,
    [property: JsonPropertyName("suppressionWindowMinutes")] int SuppressionWindowMinutes,
    [property: JsonPropertyName("maxActionsPerHour")] int MaxActionsPerHour);

/// <summary>What ingestion has done since the process started.</summary>
/// <param name="RecordedSinceStart">Observations committed to the ledger.</param>
/// <param name="DroppedSinceStart">
/// Observations discarded because the buffer was full. <b>Not hidden.</b> A non-zero value here means
/// the ledger is missing events that really happened, and every rate derived from it under-reports —
/// which is the one failure that would otherwise look like a quiet host.
/// </param>
public sealed record IngestStatus(
    [property: JsonPropertyName("recordedSinceStart")] long RecordedSinceStart,
    [property: JsonPropertyName("droppedSinceStart")] long DroppedSinceStart);

/// <summary>What judging has done since the process started.</summary>
/// <param name="RecordedSinceStart">Evaluations written to the ledger, including the ones that acted on nothing.</param>
/// <param name="AnnouncedSinceStart">
/// Those written to the journal. Lower by design: only a transition is announced, so the gap between
/// the two is every sweep that re-read a condition and found the same answer.
/// </param>
public sealed record DecisionStatus(
    [property: JsonPropertyName("recordedSinceStart")] long RecordedSinceStart,
    [property: JsonPropertyName("announcedSinceStart")] long AnnouncedSinceStart);

/// <summary>One live rule.</summary>
/// <param name="Id">Its stable id.</param>
/// <param name="Shape">What wakes it — <c>edge</c> or <c>state</c>.</param>
/// <param name="Severity">How loudly it speaks, for composition.</param>
/// <param name="Mode">The authority it runs under.</param>
/// <param name="SettleSeconds">How long after a wake before it is evaluated.</param>
/// <param name="SuppressionMinutes">
/// How long it stays quiet about one subject after firing, <b>as resolved</b> — its own window when it
/// carries one, the host-wide setting when it does not.
/// </param>
/// <remarks>
/// Resolved rather than configured, for the same reason <see cref="Mode"/> is. The gate block reports
/// the host-wide window, and a reader seeing only that would take it for the window in force on every
/// rule — which for two of the three here it is not.
/// </remarks>
public sealed record RuleStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("settleSeconds")] int SettleSeconds,
    [property: JsonPropertyName("suppressionMinutes")] int SuppressionMinutes);

/// <summary>One evaluation waiting out its settle window.</summary>
/// <param name="Rule">Which rule was woken.</param>
/// <param name="Subject">What about.</param>
/// <param name="DueAt">When it will be evaluated.</param>
public sealed record PendingStatus(
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("dueAt")] DateTimeOffset DueAt);

/// <summary>
/// The serializer for the status endpoint.
/// </summary>
/// <remarks>
/// Source-generated, because this binary is Native AOT: there is no reflection fallback, and a type
/// nobody registered throws at runtime rather than degrading.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ReactorStatus))]
public partial class ReactorStatusJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
