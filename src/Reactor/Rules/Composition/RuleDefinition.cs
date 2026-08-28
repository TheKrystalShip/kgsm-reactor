using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// A signal bound to its arguments, under a name the rest of the rule refers to it by.
/// </summary>
/// <remarks>
/// <b>Arguments live here rather than on each clause, and that is what lets prose reference a
/// measurement.</b> <c>update_regression</c> asks two things about the same lookback — whether an
/// update finished inside it, and how long ago — and its sentence names the second. Repeating the
/// arguments at every mention would let two mentions of "the last update" quietly refer to different
/// windows, which is a rule that reads correctly and means something else.
/// </remarks>
/// <param name="Alias">
/// What clauses and messages call it. Defaults to the signal's own id for a signal that takes no
/// arguments, so the common case needs no binding written at all.
/// </param>
/// <param name="SignalId">The catalog entry it reads.</param>
/// <param name="Arguments">What that entry needs supplied.</param>
internal sealed record SignalBinding(
    string Alias,
    string SignalId,
    IReadOnlyDictionary<string, string> Arguments)
{
    public static SignalBinding Bare(string signalId) =>
        new(signalId, signalId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static SignalBinding Of(string alias, string signalId, params (string Key, string Value)[] arguments) =>
        new(alias, signalId,
            arguments.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));
}

/// <summary>How a clause compares.</summary>
internal enum ClauseOperator
{
    LessThan,
    AtMost,
    GreaterThan,
    AtLeast,
    EqualTo,
    NotEqualTo,
    IsTrue,
    IsFalse,

    /// <summary>There is a value. Says nothing about what it is.</summary>
    Present,

    /// <summary>There is none — measured, not unreadable.</summary>
    Absent,

    /// <summary>Text containment, case-insensitive.</summary>
    Contains,
}

/// <summary>What a clause compares its signal against.</summary>
/// <remarks>
/// <b>A literal or another signal, and both are needed.</b> A coverage gate compares a measurement
/// against a figure somebody chose; <c>threshold_stuck</c> compares one measurement against another —
/// how long this episode has been open against how long episodes of its kind usually last. A model
/// offering only literals could not express the second, which is the rule that most justifies a
/// ledger.
/// </remarks>
internal abstract record Comparand
{
    private Comparand() { }

    /// <summary>A figure written into the rule. This is what a threshold now is.</summary>
    public sealed record Literal(SignalValue Value) : Comparand
    {
        public static Literal Number(double value) => new(SignalValue.OfNumber(value));

        public static Literal Text(string value) => new(SignalValue.OfText(value));
    }

    /// <summary>Another of this rule's bindings, read at the same instant.</summary>
    public sealed record OfSignal(string Alias) : Comparand;
}

/// <summary>One comparison: a signal, an operator, and what it is measured against.</summary>
/// <param name="Alias">The binding being read.</param>
/// <param name="Operator">How it is compared.</param>
/// <param name="Against">
/// What it is compared with, or null for the operators that take no comparand
/// (<see cref="ClauseOperator.IsTrue"/>, <see cref="ClauseOperator.Present"/> and their negations).
/// </param>
internal sealed record Clause(string Alias, ClauseOperator Operator, Comparand? Against = null)
{
    public static Clause Below(string alias, double value) =>
        new(alias, ClauseOperator.LessThan, Comparand.Literal.Number(value));

    public static Clause Above(string alias, double value) =>
        new(alias, ClauseOperator.GreaterThan, Comparand.Literal.Number(value));

    public static Clause AboveSignal(string alias, string other) =>
        new(alias, ClauseOperator.GreaterThan, new Comparand.OfSignal(other));

    public static Clause True(string alias) => new(alias, ClauseOperator.IsTrue);

    public static Clause False(string alias) => new(alias, ClauseOperator.IsFalse);

    public static Clause Present(string alias) => new(alias, ClauseOperator.Present);

    public static Clause Absent(string alias) => new(alias, ClauseOperator.Absent);

    public static Clause Is(string alias, string value) =>
        new(alias, ClauseOperator.EqualTo, Comparand.Literal.Text(value));

    public static Clause IsNot(string alias, string value) =>
        new(alias, ClauseOperator.NotEqualTo, Comparand.Literal.Text(value));
}

/// <summary>
/// One step of a rule's decision: everything that must hold, what it concludes, and what it says.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>A flat list of conditions could not express any of the four rules this build ships.</b> None
/// of them is a conjunction — each is an ordered decision with a different outcome <em>and a
/// different sentence</em> at each step. Rows are read top to bottom and the first whose clauses all
/// hold decides; a conjunction is the special case of one row and a default.
/// </para>
/// <para>
/// <b>Each row owns its prose, which is the reason a decision is worth reading.</b> A generic printer
/// rendering <c>peak(5433) &lt; declared(8192) → true</c> would satisfy the record-the-reasoning
/// invariant to the letter and destroy the thing it exists for.
/// </para>
/// </remarks>
/// <param name="Clauses">All must hold. An empty list always holds, which is how a default row works.</param>
/// <param name="Outcome">What this row concludes.</param>
/// <param name="Message">
/// The sentence recorded, with <c>{alias}</c> placeholders filled from the same reads the clauses
/// used. See <see cref="MessageTemplate"/> for what may appear in one.
/// </param>
/// <param name="UnreadableMessage">
/// What to say when a signal this row needs cannot be read, or null to report the reader's own words.
/// <para>
/// ⚠ <b>Worth writing wherever the row already knows something.</b> "Whether that has settled cannot
/// be told" beside the figures a decrement would have moved is a materially better record than the
/// trend reader's own "no working-set series" — same verdict, and only one of them lets somebody see
/// what was at stake. <c>{reason}</c> carries the reader's words inside it.
/// </para>
/// </param>
internal sealed record GuardRow(
    IReadOnlyList<Clause> Clauses,
    VerdictKind Outcome,
    string Message,
    string? UnreadableMessage = null);

/// <summary>Who shaped a rule, in the ecosystem's actor shape.</summary>
/// <remarks>
/// ⚠ <b>Provenance about the rule, never the actor on a decision.</b> Nobody decided anything at three
/// in the morning; the rule did, and writing a person into the actor would claim they performed an act
/// they did not. An audit row reads <em>"stopped by rule <c>disk_pressure_stop</c>, written by
/// <c>discord:tanya</c>"</em> — the act and its origin named without being confused.
/// </remarks>
/// <param name="Actor">
/// <c>provider:name</c>, and the stable <b>username</b> rather than a display name — a person can
/// change the second, which would then rewrite what an old decision appears to say.
/// </param>
/// <param name="At">When they did it.</param>
internal sealed record RuleAuthorship(string Actor, DateTimeOffset At);

/// <summary>
/// A rule, assembled from what this build offers.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a person composes and what the store holds.</b> The catalogs it draws on are compiled — a
/// signal is a reader, an action is a case of a closed union — so a rule may combine what this build
/// can do and can never reach past it.
/// </para>
/// <para>
/// <b>The shape is derived rather than declared.</b> A rule whose subjects come from the event that
/// woke it is edge-shaped by definition; one that enumerates its own is state-shaped and cannot miss
/// a wake. Asking somebody to state both is asking them to contradict themselves.
/// </para>
/// </remarks>
/// <param name="Id">Stable, immutable, and the actor string every decision it makes carries.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="Wakes">
/// Event types that bring it to an evaluation. Empty for a rule the sweep alone reaches, whose
/// condition no producer announces.
/// </param>
/// <param name="SubjectSource">Which catalog entry works out what it decides about.</param>
/// <param name="SubjectArguments">What that entry needs supplied.</param>
/// <param name="Signals">Its bindings — every signal it reads, with the arguments it reads them under.</param>
/// <param name="Rows">Its decision, in order.</param>
/// <param name="Default">
/// What it concludes when no row holds. Required: a rule that could fall off the end deciding nothing
/// would be silence indistinguishable from a condition that does not apply.
/// </param>
/// <param name="ActionId">Which catalog action it would take.</param>
/// <param name="Severity">How loudly it speaks, for composition.</param>
/// <param name="Settle">How long after a wake before it is evaluated.</param>
/// <param name="Suppression">Its own quiet window, or null to follow the host-wide one.</param>
/// <param name="ProposalLifetime">
/// How long an unanswered offer from this rule stays redeemable, or null to follow the host-wide one.
/// <para>
/// ⚠ <b>Not what makes a proposal safe.</b> The condition is re-derived at redemption, so a stale
/// offer answers <em>no longer applicable</em> rather than executing — which is why this can be
/// measured in hours at all. What it is for is the difference between offers: capturing a broken
/// state is worth answering all day, where rolling a server back stops being the right move once
/// somebody has started working on it by hand.
/// </para>
/// </param>
/// <param name="Mode">The authority it asks for. Clamped by what the build honours.</param>
/// <param name="Retired">
/// Stopped evaluating and out of the live list, definition kept. ⚠ Never erased: <c>rule:{Id}</c> is
/// the actor on every line it produced, and a decision that cannot resolve to a rule that can be named
/// is a record with a hole in it.
/// </param>
/// <param name="Shipped">
/// Whether this build seeded it. A seeded rule is attributed to the build rather than to a person, and
/// neither is guessed at: a definition hand-written into the file over SSH carries no identity and is
/// left unattributed.
/// </param>
/// <param name="CreatedBy">Who created it, or null when nobody is known to have.</param>
/// <param name="UpdatedBy">Who last changed it, which is the attribution a decision is stamped with.</param>
internal sealed record RuleDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Wakes,
    string SubjectSource,
    IReadOnlyDictionary<string, string> SubjectArguments,
    IReadOnlyList<SignalBinding> Signals,
    IReadOnlyList<GuardRow> Rows,
    GuardRow Default,
    string ActionId,
    EventSeverity Severity,
    TimeSpan Settle,
    TimeSpan? Suppression = null,
    TimeSpan? ProposalLifetime = null,
    RuleMode Mode = RuleMode.Observe,
    bool Retired = false,
    bool Shipped = false,
    RuleAuthorship? CreatedBy = null,
    RuleAuthorship? UpdatedBy = null)
{
    /// <summary>What wakes it, worked out from where its subjects come from.</summary>
    public RuleShape Shape =>
        SubjectSourceCatalog.ById(SubjectSource) is { FromEvent: true } ? RuleShape.Edge : RuleShape.State;

    /// <summary>
    /// The attribution a decision this rule makes carries, or null when nobody is known to have shaped it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The last hand on it, and copied onto the decision rather than joined at read time.</b> A
    /// rogue rule is as often an edit as a creation. Resolving this through the store when a decision
    /// is read would mean editing a rule silently rewrites the attribution of everything it ever
    /// decided, and retiring one — or closing an account — erases the trace entirely.
    /// </remarks>
    public string? Author => (UpdatedBy ?? CreatedBy)?.Actor;

    /// <summary>The binding an alias names, or null when nothing declares it.</summary>
    public SignalBinding? Binding(string alias) =>
        Signals.FirstOrDefault(b => string.Equals(b.Alias, alias, StringComparison.Ordinal));
}
