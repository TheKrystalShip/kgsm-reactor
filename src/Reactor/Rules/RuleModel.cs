using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>What wakes a rule. It decides nothing else.</summary>
/// <remarks>
/// <b>The distinction collapses at evaluation, and that is the point.</b> Every rule re-derives from
/// the live world when it evaluates; the shape decides only what brings it to that point. So a missed
/// event costs a <em>wake</em> and never a judgment — and a state rule has no wake to miss, because
/// its wake is a timer. That is what makes reading the journal at the tail with no cursor safe for a
/// leaf that acts: express the rules that matter as state, and a gap in the event stream stops
/// mattering.
/// </remarks>
internal enum RuleShape
{
    /// <summary>Woken by an event arriving. Misses what happened while the process was down.</summary>
    Edge,

    /// <summary>Woken by the sweep. Rediscovers its own condition, so nothing can be missed.</summary>
    State,
}

/// <summary>How loudly a rule speaks. Used for composition, where the most severe wins.</summary>
/// <summary>What a rule is permitted to do when it fires.</summary>
/// <remarks>
/// A field on the rule: the same rule is <see cref="Observe"/> on a host that has not earned trust in
/// it and <see cref="Act"/> on one that has. <b>Every rule starts at <see cref="Observe"/></b> and only
/// moves when somebody decides it should. What it is permitted to do is clamped by what the build
/// honours, and both figures are reported so nobody has to guess which they are looking at.
/// </remarks>
internal enum RuleMode
{
    /// <summary>
    /// Do not evaluate it at all.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not the same as retiring one.</b> A rule that is off is live, listed and one field away
    /// from running again — somebody silenced it while they work out whether it is right. A retired
    /// rule is gone from the live list and kept only so the decisions it already made still resolve to
    /// something that can be named. Offering one control for both would make un-deleting and
    /// un-muting the same gesture.
    /// </remarks>
    Off,

    /// <summary>Evaluate and record. Dispatch nothing.</summary>
    Observe,

    /// <summary>Stage the action for a human to confirm.</summary>
    Propose,

    /// <summary>Perform it.</summary>
    Act,
}

/// <summary>Whether a rule's condition holds.</summary>
/// <remarks>
/// <b>Three-valued, and the third value is the point.</b> "Cannot tell" must not be able to
/// masquerade as "no" — which would be silence — or as "yes", which would be acting blind. It is
/// invariant 5 expressed as a type rather than as a convention somebody has to remember.
/// </remarks>
internal enum VerdictKind
{
    /// <summary>The condition is true right now.</summary>
    Holds,

    /// <summary>The condition is false right now. Usually because it resolved itself.</summary>
    DoesNotHold,

    /// <summary>
    /// No judgment could be formed. Either the world could not be read, or what was read is not
    /// enough to decide on — a rule that compares against a distribution cannot speak before it has
    /// one.
    /// </summary>
    Unreadable,
}

/// <summary>
/// A rule's answer, and why. The reason is recorded whichever way it went.
/// </summary>
/// <param name="Kind">Whether the condition holds.</param>
/// <param name="Reason">Why, in words. Present on every verdict, including the ones that decide nothing.</param>
/// <param name="Withheld">
/// Whether the rule declined to judge on the evidence it has, as opposed to something refusing to
/// answer.
/// <para>
/// <b>Both are <see cref="VerdictKind.Unreadable"/>, and they are not the same news.</b> A supervisor
/// that could not be reached is an operational fact somebody may need to act on. A rule saying
/// <em>"this instance has not been measured for long enough to judge"</em> is the rule describing its
/// own evidence — unactionable by construction, and the permanent steady state for anything recently
/// installed. Recorded either way; only the first is worth announcing.
/// </para>
/// </param>
internal readonly record struct Verdict(VerdictKind Kind, string Reason, bool Withheld = false)
{
    public static Verdict Holds(string reason) => new(VerdictKind.Holds, reason);

    public static Verdict DoesNotHold(string reason) => new(VerdictKind.DoesNotHold, reason);

    /// <summary>Something would not answer, so nothing could be established.</summary>
    public static Verdict Unreadable(string reason) => new(VerdictKind.Unreadable, reason);

    /// <summary>
    /// The rule read what it needed and decided that is not enough to judge on.
    /// </summary>
    /// <remarks>
    /// A coverage gate — a span too short, a sample too thin, a distribution with too few points. The
    /// reading succeeded; the rule is refusing to draw a conclusion from it, which is the refusal that
    /// keeps it from reporting noise as a finding.
    /// </remarks>
    public static Verdict Withhold(string reason) => new(VerdictKind.Unreadable, reason, Withheld: true);
}

/// <summary>
/// The complete set of things this leaf can do. Nothing outside this file can add to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed union, deliberately, rather than a command string.</b> The never-list — never
/// uninstall, never delete a backup, never rewrite instance config, never moderate a player — is then
/// enforced by the type system instead of by the discretion of whoever writes the next rule. Adding a
/// capability is a visible edit to this type, which is exactly the review it deserves.
/// </para>
/// <para>
/// The private constructor is what closes it: a record with no accessible base constructor cannot be
/// derived from outside this declaration.
/// </para>
/// </remarks>
internal abstract record ReactorAction
{
    private ReactorAction() { }

    /// <summary>
    /// Whether performing this changes the server, as opposed to only adding something beside it.
    /// </summary>
    /// <remarks>
    /// The composition gate excludes a second action on one episode — but purely additive actions are
    /// exempt, because a regression wants the broken state preserved <em>and</em> the rollback
    /// offered, and making those compete would lose one of them.
    /// </remarks>
    public abstract bool ChangesServerState { get; }

    /// <summary>What this would do, in a few words, for the decision record.</summary>
    public abstract string Describe();

    /// <summary>
    /// What performing it costs, and whether it can be taken back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The half of an offer that is not about the fault.</b> A person deciding whether to authorise
    /// something needs to know what the host will look like afterwards, and every part of that lives
    /// here rather than in the rule: the rule found a condition, and what an action does to a server is
    /// a property of the action on every host that has it.
    /// </para>
    /// <para>
    /// ⚠ <b>It says what changes, never how likely it is to help.</b> "This will fix it" is a claim
    /// about a fault nothing here has diagnosed, and an offer that made one would be selling the action
    /// rather than describing it.
    /// </para>
    /// </remarks>
    public abstract string Consequence { get; }

    /// <summary>
    /// The stable name of the action, for a reader that switches on it rather than reads it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Describe"/> because that one is prose and free to be reworded; this
    /// one is compared against by other programs, and rewording it would silently stop matching.
    /// </remarks>
    public abstract string Name { get; }

    /// <summary>
    /// The server this would operate on, or <see langword="null"/> when it operates on none.
    /// </summary>
    /// <remarks>
    /// Not the same question as what the rule judged: a rule that decides about a host sensor can
    /// still propose something about a server.
    /// </remarks>
    public abstract string? TargetInstance { get; }

    /// <summary>A rule that reports and proposes nothing.</summary>
    public sealed record Nothing : ReactorAction
    {
        public override bool ChangesServerState => false;

        public override string Describe() => "nothing";

        /// <summary>Nothing is offered for this, so nobody is ever shown it.</summary>
        public override string Consequence => "Nothing on this host changes.";

        public override string Name => "none";

        public override string? TargetInstance => null;
    }

    /// <summary>Capture an instance's state as a pinned backup.</summary>
    /// <param name="Instance">The instance to capture.</param>
    public sealed record CreateBackup(string Instance) : ReactorAction
    {
        /// <summary>Additive: it takes nothing away and competes with nothing.</summary>
        public override bool ChangesServerState => false;

        public override string Describe() => $"archive {Instance} as it stands, pinned";

        public override string Consequence =>
            "Adds an archive and changes nothing about the server — it is not started, stopped or "
            + "written to. The archive is pinned, so rotation never deletes it and it takes no slot "
            + "from the ordinary backups. It costs disk until somebody removes it.";

        public override string Name => "create_backup";

        public override string? TargetInstance => Instance;
    }

    /// <summary>
    /// Offer to roll an instance back to the archive taken before the update that preceded its
    /// failure.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The target is described, not named.</b> It is resolved at dispatch by the manifest's
    /// <c>reason</c>, never by recency — once <c>give_up_backup</c> is acting, the newest archive at
    /// this moment is the broken post-update state that rule has just captured. An archive written
    /// before the manifest carried a reason reads back unknown, and an unknown one is not a candidate.
    /// </remarks>
    /// <param name="Instance">The instance to roll back.</param>
    public sealed record ProposeRestore(string Instance) : ReactorAction
    {
        public override bool ChangesServerState => true;

        public override string Describe() => $"roll {Instance} back to the archive taken before its update";

        public override string Consequence =>
            "Overwrites the server's files with the archive's. Everything it has done since that "
            + "archive was taken — world state, saves, config edits — is gone, and nothing keeps a "
            + "copy of it unless somebody archives it first. There is no undo.";

        public override string Name => "propose_restore";

        public override string? TargetInstance => Instance;
    }
}

/// <summary>What a rule's predicate is given to decide with.</summary>
/// <param name="Subject">What the rule is deciding about — an instance, a host reference, a leaf.</param>
/// <param name="Now">The evaluation instant. Passed rather than read, so a test owns the clock.</param>
/// <param name="World">The live world. Every rule re-derives from it rather than trusting the event.</param>
/// <param name="History">What has been observed, for the questions a single event cannot answer.</param>
/// <param name="Footprint">
/// What kgsm-monitor has measured, for the questions no event answers at all. Reads through it are
/// three-valued like the rest: the monitor is a leaf and may not be installed.
/// </param>
/// <param name="OpenedAt">
/// When the condition being judged began, or null when this evaluation has no opening to name.
/// <para>
/// ⚠ <b>Not the evaluation instant, and never filled in with it.</b> A sentence dating a crash loop
/// from the moment somebody looked at it would be a fabricated measurement in the one place an
/// operator most needs a real one.
/// </para>
/// </param>
internal sealed record RuleContext(
    string Subject,
    DateTimeOffset Now,
    IWorldView World,
    IRuleHistory History,
    IFootprintSource Footprint,
    DateTimeOffset? OpenedAt = null);

/// <summary>
/// What a state rule is given to work out which subjects it should evaluate.
/// </summary>
/// <remarks>
/// The same sources as <see cref="RuleContext"/> without the subject, which is the thing being chosen.
/// A rule whose subjects come from a measurement rather than from an open episode needs to read the
/// world to find them, and a delegate given only the history could not.
/// </remarks>
internal sealed record SubjectContext(
    DateTimeOffset Now,
    IWorldView World,
    IRuleHistory History,
    IFootprintSource Footprint);

/// <summary>One rule, in the shape the engine evaluates.</summary>
/// <remarks>
/// <b>Assembled from a <see cref="Composition.RuleDefinition"/>, which is what a person composes and
/// what the store holds.</b> This is the runtime face of one: a predicate to call, a wake set to match
/// against, and the windows the gate reads. Keeping the two apart is what lets the engine be given a
/// rule without knowing whether the catalogs, a file, or a test assembled it.
/// </remarks>
/// <param name="Id">Stable, and the actor string an audit row will carry.</param>
/// <param name="Shape">What wakes it.</param>
/// <param name="Wakes">
/// Event types that bring it to an evaluation.
/// <para>
/// Empty is legal only for a <see cref="RuleShape.State"/> rule, and means the condition is one no
/// producer announces — a standing fact about accumulated measurement rather than something that
/// happens. Such a rule is reached by the sweep alone and identifies its own episodes. ⚠ Its decisions
/// then cite a measurement rather than a journal line, so its reason string has to carry the figures:
/// nothing else lets a reader reconstruct what it decided on.
/// </para>
/// </param>
/// <param name="Severity">How loudly it speaks, for composition.</param>
/// <param name="Settle">
/// How long after the wake before it is evaluated. The window in which a condition that was going to
/// resolve itself does so — the single largest source of noise a rule engine can avoid.
/// </param>
/// <param name="Holds">The predicate. Compiled in; never data.</param>
/// <param name="Action">What it would do. Declarative: nothing here dispatches.</param>
/// <param name="Subjects">
/// For a <see cref="RuleShape.State"/> rule, what to evaluate on each sweep. Null for an edge rule,
/// whose subject arrives with the event.
/// </param>
/// <param name="Suppression">
/// How long this rule stays quiet about one subject after firing, or null to take the host-wide
/// setting.
/// </param>
/// <remarks>
/// <para>
/// Per rule because the measurement is per rule, by three orders of magnitude. The window comes from
/// the observed spacing between repeat events for one subject, and on this host that is 25 seconds
/// for a crash and four hours for a threshold breach — one number serving both either collapses a
/// day of threshold episodes into one decision or lets a crash-loop speak nine times.
/// </para>
/// <para>
/// Null rather than a default, so a rule that has never been measured takes the host-wide value and
/// says so, instead of carrying a figure someone will later read as a measurement.
/// </para>
/// </remarks>
internal sealed record Rule(
    string Id,
    RuleShape Shape,
    IReadOnlyList<string> Wakes,
    EventSeverity Severity,
    TimeSpan Settle,
    Func<RuleContext, CancellationToken, ValueTask<Verdict>> Holds,
    Func<string, ReactorAction> Action,
    Func<SubjectContext, CancellationToken, ValueTask<IReadOnlyList<string>>>? Subjects = null,
    TimeSpan? Suppression = null);
