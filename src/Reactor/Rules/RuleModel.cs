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
internal enum Severity
{
    Info,
    Warning,
    Danger,
}

/// <summary>What a rule is permitted to do when it fires.</summary>
/// <remarks>
/// Configuration, not a property of the rule: the same rule is <see cref="Observe"/> on a host that
/// has not earned trust in it and <see cref="Act"/> on one that has. <b>Every rule starts at
/// <see cref="Observe"/></b> and only moves when somebody decides it should.
/// </remarks>
internal enum RuleMode
{
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

/// <summary>A rule's answer, and why. The reason is recorded whichever way it went.</summary>
internal readonly record struct Verdict(VerdictKind Kind, string Reason)
{
    public static Verdict Holds(string reason) => new(VerdictKind.Holds, reason);

    public static Verdict DoesNotHold(string reason) => new(VerdictKind.DoesNotHold, reason);

    public static Verdict Unreadable(string reason) => new(VerdictKind.Unreadable, reason);
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

        public override string Name => "none";

        public override string? TargetInstance => null;
    }

    /// <summary>Capture an instance's state as a pinned backup.</summary>
    /// <param name="Instance">The instance to capture.</param>
    public sealed record CreateBackup(string Instance) : ReactorAction
    {
        /// <summary>Additive: it takes nothing away and competes with nothing.</summary>
        public override bool ChangesServerState => false;

        public override string Describe() => $"take a pinned backup of {Instance}";

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

        public override string Describe() => $"restore {Instance} to its pre-update backup";

        public override string Name => "propose_restore";

        public override string? TargetInstance => Instance;
    }
}

/// <summary>What a rule's predicate is given to decide with.</summary>
/// <param name="Subject">What the rule is deciding about — an instance, a host reference, a leaf.</param>
/// <param name="Now">The evaluation instant. Passed rather than read, so a test owns the clock.</param>
/// <param name="World">The live world. Every rule re-derives from it rather than trusting the event.</param>
/// <param name="History">What has been observed, for the questions a single event cannot answer.</param>
internal sealed record RuleContext(
    string Subject,
    DateTimeOffset Now,
    IWorldView World,
    IRuleHistory History);

/// <summary>One rule: what wakes it, how it decides, and what it would do about it.</summary>
/// <remarks>
/// <b>Rules ship in code.</b> Configuration enables one, sets its mode and tunes its windows; it
/// cannot invent one. A file that could add a rule is a file that could make the host act, which is
/// the same argument that keeps tool tiers out of a JSON catalog elsewhere in this ecosystem.
/// </remarks>
/// <param name="Id">Stable, and the actor string an audit row will carry.</param>
/// <param name="Shape">What wakes it.</param>
/// <param name="Wakes">Event types that bring it to an evaluation.</param>
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
internal sealed record Rule(
    string Id,
    RuleShape Shape,
    IReadOnlyList<string> Wakes,
    Severity Severity,
    TimeSpan Settle,
    Func<RuleContext, CancellationToken, ValueTask<Verdict>> Holds,
    Func<string, ReactorAction> Action,
    Func<IRuleHistory, CancellationToken, ValueTask<IReadOnlyList<string>>>? Subjects = null);
