using System.Text.Json.Serialization;

using TheKrystalShip.KGSM.Events;

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
/// <b>Every counter here is since this process started</b>, not since the beginning. A restart
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

    /// <summary>
    /// The most authority this build will let any rule have.
    /// </summary>
    /// <remarks>
    /// <b>Reported so a surface does not have to know which phases exist.</b> Propose and act are
    /// later phases, and a panel that hard-coded "this build only observes" would go on saying it
    /// after the build that acts is deployed — offering a control that does nothing, or refusing one
    /// that would work. The leaf is the only thing that knows, so the leaf says.
    /// </remarks>
    [JsonPropertyName("honours")]
    public required string Honours { get; init; }

    /// <summary>Every rule that is live, and the authority it runs under.</summary>
    [JsonPropertyName("rules")]
    public required IReadOnlyList<RuleStatus> Rules { get; init; }

    /// <summary>
    /// Rules that are kept but never evaluated, so an old decision still resolves to a name.
    /// </summary>
    /// <remarks>
    /// Reported apart from the live ones rather than mixed in with a flag, because the two answer
    /// different questions. A surface listing what is running must not show these; a surface
    /// explaining a decision from last month has to be able to find them.
    /// </remarks>
    [JsonPropertyName("retired")]
    public required IReadOnlyList<RuleStatus> Retired { get; init; }

    /// <summary>The directory this host's rules are read from, one file per rule.</summary>
    /// <remarks>
    /// Reported whether or not anything is in it, because "this host judges nothing" and "the rules
    /// are somewhere other than you think" look identical from the outside and only the path
    /// separates them.
    /// </remarks>
    [JsonPropertyName("rulesDirectory")]
    public required string RulesDirectory { get; init; }

    /// <summary>How many rules were read from it, retired ones included.</summary>
    /// <remarks>
    /// Counts what loaded, so it is smaller than the number of files when one of them was refused.
    /// The difference is exactly what <see cref="Problems"/> lists.
    /// </remarks>
    [JsonPropertyName("ruleFiles")]
    public required int RuleFiles { get; init; }

    /// <summary>
    /// What was written and could not be honoured. Empty on a host where everything was.
    /// </summary>
    /// <remarks>
    /// <b>The single most important field here for anyone who has just written a rule.</b> A
    /// misspelled signal, a step with no sentence, an action this build cannot perform, a duplicate id
    /// — each leaves the daemon running the rules it could honour, and without this every one of them
    /// presents as "I saved it and nothing happened", which is indistinguishable from a rule that
    /// simply had nothing to say.
    /// </remarks>
    [JsonPropertyName("problems")]
    public required IReadOnlyList<string> Problems { get; init; }

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

/// <summary>One comparison a rule makes.</summary>
/// <param name="Signal">The binding being read, by the name the rule calls it.</param>
/// <param name="Operator">How it is compared, in the wire spelling a file uses.</param>
/// <param name="Value">A figure it is compared against, when it is compared against one.</param>
/// <param name="Text">Text it is compared against, when it is compared against text.</param>
/// <param name="VsSignal">Another of the rule's bindings it is compared against, when it is.</param>
public sealed record ClauseStatus(
    [property: JsonPropertyName("signal")] string Signal,
    [property: JsonPropertyName("op")] string Operator,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("vsSignal")] string? VsSignal);

/// <summary>One step of a rule's decision, in the order it is read.</summary>
/// <remarks>
/// <b>Order is the semantics.</b> The first row whose clauses all hold decides, so a surface that
/// re-sorted these — alphabetically, by outcome, by anything — would show a rule that behaves
/// differently from the one running.
/// </remarks>
/// <param name="Clauses">All of these must hold. Empty always holds, which is what a default step is.</param>
/// <param name="Outcome">What it concludes: <c>holds</c>, <c>doesNotHold</c> or <c>unreadable</c>.</param>
/// <param name="Message">The sentence it records, with its placeholders unfilled.</param>
/// <param name="UnreadableMessage">What it records when a signal it needs cannot be read, if it says.</param>
public sealed record GuardRowStatus(
    [property: JsonPropertyName("when")] IReadOnlyList<ClauseStatus> Clauses,
    [property: JsonPropertyName("then")] string Outcome,
    [property: JsonPropertyName("say")] string Message,
    [property: JsonPropertyName("sayWhenUnreadable")] string? UnreadableMessage);

/// <summary>A signal a rule reads, under the name the rule calls it.</summary>
/// <param name="Alias">What the rule's clauses and sentences call it.</param>
/// <param name="Signal">The catalog entry it reads.</param>
/// <param name="Arguments">What that entry was given.</param>
public sealed record SignalBindingStatus(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("signal")] string Signal,
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, string> Arguments);

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

/// <summary>One rule, as it is actually running.</summary>
/// <param name="Id">Its stable id, and the actor on every decision it makes.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="Shape">
/// What wakes it — <c>edge</c> or <c>state</c>. Derived from where its subjects come from rather than
/// declared, so it cannot disagree with them.
/// </param>
/// <param name="Severity">How loudly it speaks, for composition.</param>
/// <param name="Mode">
/// The authority it actually runs under — what this build will let it do, not what the file asked for.
/// </param>
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
/// <param name="ConfiguredMode">
/// What the configuration asked for, when this build cannot honour it — otherwise <see langword="null"/>.
/// </param>
/// <remarks>
/// <b>The pair is the honest answer, and neither half alone is.</b> Reporting only the configured
/// mode shows an authority the rule does not have; reporting only the effective one hides that
/// somebody asked for more and did not get it. A surface renders <see cref="Mode"/> as what is in
/// force and mentions <see cref="ConfiguredMode"/> as what was intended.
/// </remarks>
/// <param name="Wakes">The event types that bring it to an evaluation.</param>
/// <param name="SubjectSource">Which catalog entry works out what it decides about.</param>
/// <param name="SubjectArguments">What that entry was given.</param>
/// <param name="ActionName">
/// The stable name of what it would do, or <c>none</c> for a rule that reports and proposes nothing.
/// </param>
/// <param name="Signals">Every signal it reads, under the name it calls each one.</param>
/// <param name="Rows">Its decision, in the order it is read. The first whose clauses all hold wins.</param>
/// <param name="Default">What it concludes when no row holds.</param>
/// <param name="Author">
/// Who last shaped it, as <c>provider:name</c>, or null when nobody is known to have.
/// </param>
/// <remarks>
/// <b>Null is a real answer and must be rendered as one.</b> A rule this build seeded, or one
/// hand-written into the file, is unattributed — and there is no fallback to the OS user. A surface
/// substituting the host or the person reading would invent a hand that was never on it.
/// </remarks>
/// <param name="Enabled">
/// Whether it runs. Off, <c>mode</c> reads <c>off</c> and <c>configuredMode</c> carries the authority
/// it resumes with — which is the pair a switch has to be rendered from, since the honoured value
/// alone cannot say the difference between a rule that watches and one that was told to stop.
/// </param>
/// <param name="Retired">
/// Kept so its decisions still resolve to a name, and never evaluated.
/// </param>
public sealed record RuleStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("settleSeconds")] int SettleSeconds,
    [property: JsonPropertyName("suppressionMinutes")] int SuppressionMinutes,
    [property: JsonPropertyName("configuredMode")] string? ConfiguredMode,
    [property: JsonPropertyName("wakes")] IReadOnlyList<string> Wakes,
    [property: JsonPropertyName("subjectSource")] string SubjectSource,
    [property: JsonPropertyName("subjectArgs")] IReadOnlyDictionary<string, string> SubjectArguments,
    [property: JsonPropertyName("actionName")] string ActionName,
    [property: JsonPropertyName("signals")] IReadOnlyList<SignalBindingStatus> Signals,
    [property: JsonPropertyName("rows")] IReadOnlyList<GuardRowStatus> Rows,
    [property: JsonPropertyName("default")] GuardRowStatus Default,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("retired")] bool Retired);

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
