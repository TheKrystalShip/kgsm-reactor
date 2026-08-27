using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// The rules a host starts with, composed from the same catalogs a person composes from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seeds, not a second implementation.</b> They are ordinary definitions with nothing privileged
/// about them: a host with no file gets these, and anything here can be edited or retired from the
/// panel like any other rule. What makes them worth keeping in code is that they are the acceptance
/// test — each restates a rule that was written by hand, and <c>ComposedRuleTwinTests</c> runs both
/// against the same fixtures and demands the same verdict and the same sentence. A model that could
/// not restate these would not carry the fifth rule either.
/// </para>
/// <para>
/// <b>Their ids and their measured windows are the same ones.</b> A composed rule that quietly lost
/// the 45-minute threshold window would be a new rule wearing an old one's name, and its decisions
/// would fold into the old one's episodes.
/// </para>
/// <para>
/// <b>Every threshold here is a comparand.</b> What used to be a declared parameter with a label and
/// a unit is now the number a clause compares against — the label, the unit and the description having
/// moved onto the signal, which is the thing they were always describing.
/// </para>
/// </remarks>
internal static class SeededRules
{
    // ---- the windows, each carrying what measured it ----

    /// <summary>Measured: a give-up that ends on its own takes at least 83s (p50 3.1m, p95 7.9m).</summary>
    /// <remarks>
    /// Above the minimum and below the median rather than at p95: this rule only ever creates, so its
    /// false positive costs disk where its false negative costs the rollback candidate.
    /// </remarks>
    private static readonly TimeSpan GiveUpSettle = TimeSpan.FromMinutes(2);

    /// <summary>Measured: a crash the supervisor rides out is ready again in 6.1s p50, 38s p95.</summary>
    private static readonly TimeSpan CrashSettle = TimeSpan.FromSeconds(60);

    /// <summary>Measured: every threshold episode here cleared on its own — 12 of 12, slowest 39.7m.</summary>
    private static readonly TimeSpan ThresholdSettle = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Short, because there is nothing here for a settle window to do — a fortnight's measurement does
    /// not resolve itself in an hour. It only keeps a sweep during startup from deciding before the
    /// monitor is reachable.
    /// </summary>
    private static readonly TimeSpan DriftSettle = TimeSpan.FromMinutes(2);

    /// <summary>Measured: a give-up repeats every 5.5m p50 / 10.3m p95.</summary>
    private static readonly TimeSpan GiveUpSuppression = TimeSpan.FromMinutes(15);

    /// <summary>Measured: a threshold breach repeats every 4.1h p50 / 2.0d p95.</summary>
    private static readonly TimeSpan ThresholdSuppression = TimeSpan.FromHours(4);

    /// <summary>A day, because a figure drawn from a fortnight cannot change faster than that.</summary>
    private static readonly TimeSpan DriftSuppression = TimeSpan.FromDays(1);

    /// <summary>
    /// ⚠ How soon after an update a failure is still that update's fault. <b>Not measured</b> — 30 days
    /// hold two update-then-fault pairs at 112 and 168 minutes and neither is plausibly causal, so a
    /// window fitted to them would be a causal claim built from coincidence.
    /// </summary>
    private const string RegressionWindowMinutes = "30";

    /// <summary>Below this a p95 is drawn from a handful of samples and is not a distribution.</summary>
    private const double MinimumEpisodeSamples = 5;

    /// <summary>How far back a rule may look. Bounded by the ledger's own retention regardless.</summary>
    private const string LookBackDays = "30";

    // ---- what the drift rule compares against ----

    /// <summary>Calendar days the observations must span before a figure is read off them.</summary>
    private const double MinSpanDays = 2;

    /// <summary>
    /// Cumulative hours of measurement required.
    /// </summary>
    /// <remarks>
    /// Above what the trend behind it needs: a direction is read from five-minute buckets and needs
    /// two dozen, so a verdict admitted on less would be one whose decrement guard could never answer.
    /// </remarks>
    private const double MinObservedHours = 5;

    /// <summary>Independent runs, which is what separates an instance's behaviour from one session's.</summary>
    private const double MinRuns = 2;

    /// <summary>Hours of unbroken running that stand in for a run count, when no start was observed.</summary>
    private const double ContinuousRunHours = 24;

    /// <summary>How far two true figures must sit apart before the gap is worth saying.</summary>
    private const double DriftMarginPct = 25;

    /// <summary>How much a working set may still be growing and count as having found its ceiling.</summary>
    private const double SettledGrowthPct = 10;

    private const string BreachOpens = "host.threshold.breached";
    private const string BreachCloses = "host.threshold.cleared";

    public static IReadOnlyList<RuleDefinition> All { get; } =
    [
        GiveUpBackup(),
        UpdateRegression(),
        ThresholdStuck(),
        MemoryDeclarationDrift(),
    ];

    public static RuleDefinition? ById(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The supervisor gave up on an instance — capture what it died as, before anybody debugs it.
    /// </summary>
    /// <remarks>
    /// The give-up is re-asserted rather than taken from the event, and the settle window is exactly
    /// the gap that closes: an operator start clears the latch at any moment, and a backup taken over
    /// an instance coming back up is a hot archive where a cold one was intended.
    /// </remarks>
    private static RuleDefinition GiveUpBackup() => new(
        Id: "give_up_backup",
        Name: "Back up an instance the supervisor gave up on",
        Wakes: ["server.crash.exhausted"],
        SubjectSource: SubjectSourceCatalog.FromEvent,
        SubjectArguments: NoArguments,
        Signals: [],
        Rows:
        [
            new([Clause.True("world.gaveUp")],
                VerdictKind.Holds,
                "still given up on after {settleSeconds}s ({world.restarts} consecutive failures)"),
        ],
        Default: new([],
            VerdictKind.DoesNotHold,
            "no longer given up on — the supervisor reports {world.phase}"),
        ActionId: ActionCatalog.CreateBackup,
        Severity: EventSeverity.Danger,
        Settle: GiveUpSettle,
        Suppression: GiveUpSuppression,
        Shipped: true);

    /// <summary>
    /// An instance that failed shortly after an update — offer the archive taken before it.
    /// </summary>
    /// <remarks>
    /// The engine records the update and the supervisor records the failure; nobody joins them, and the
    /// join is the whole answer at three in the morning. ⚠ Proposes and never acts: a restore
    /// overwrites live state.
    /// </remarks>
    private static RuleDefinition UpdateRegression() => new(
        Id: "update_regression",
        Name: "Offer a rollback after an update-shaped failure",
        Wakes: ["server.crash.exhausted", "server.crashed"],
        SubjectSource: SubjectSourceCatalog.FromEvent,
        SubjectArguments: NoArguments,
        Signals:
        [
            SignalBinding.Of("lastUpdate", "history.lastOccurrence",
                ("eventType", "server.update.finished"), ("withinMinutes", RegressionWindowMinutes)),
            SignalBinding.Of("sinceUpdate", "history.minutesSince",
                ("eventType", "server.update.finished"), ("withinMinutes", RegressionWindowMinutes)),
        ],
        Rows:
        [
            new([Clause.Absent("lastUpdate")],
                VerdictKind.DoesNotHold,
                "no update finished on {subject} in the last {lastUpdate@withinMinutes} minutes"),

            new([Clause.True("world.running")],
                VerdictKind.DoesNotHold,
                "it is running again"),
        ],
        Default: new([],
            VerdictKind.Holds,
            "failed {sinceUpdate}m after an update finished, and is not running"),
        ActionId: ActionCatalog.ProposeRestore,
        Severity: EventSeverity.Danger,
        // No suppression: a crash repeats every 25s at p50, which the host-wide window already covers.
        Settle: CrashSettle,
        Shipped: true);

    /// <summary>
    /// A threshold episode open far longer than episodes of its kind usually last on this host.
    /// </summary>
    /// <remarks>
    /// The rule that could not exist without the ledger. The monitor reports breached and cleared and
    /// kgsm-api alerts on breached; neither knows what normal is <em>here</em>. State-shaped, so it
    /// rediscovers open episodes rather than depending on having seen the breach go by.
    /// </remarks>
    private static RuleDefinition ThresholdStuck() => new(
        Id: "threshold_stuck",
        Name: "Report a threshold breach that is not clearing",
        Wakes: [BreachOpens],
        SubjectSource: SubjectSourceCatalog.OpenEpisodes,
        SubjectArguments: Arguments(
            ("opensWith", BreachOpens), ("closesWith", BreachCloses), ("withinDays", LookBackDays)),
        Signals:
        [
            Episode("episodeOpen", "episode.isOpen"),
            Episode("openFor", "episode.openAge"),
            Episode("p95", "episode.durationP95"),
            Episode("closed", "episode.closedSamples"),
        ],
        Rows:
        [
            new([Clause.False("episodeOpen")],
                VerdictKind.DoesNotHold,
                "no episode is open"),

            new([Clause.Below("closed", MinimumEpisodeSamples)],
                VerdictKind.Unreadable,
                "only {closed} closed episode(s) on record for {subject} — too few to say what unusual is"),

            new([Clause.AboveSignal("openFor", "p95")],
                VerdictKind.Holds,
                "open for {openFor}, past the p95 of {p95} over {closed} closed episodes"),
        ],
        Default: new([],
            VerdictKind.DoesNotHold,
            "open for {openFor}, within the p95 of {p95}"),
        ActionId: ActionCatalog.None,
        Severity: EventSeverity.Warn,
        Settle: ThresholdSettle,
        Suppression: ThresholdSuppression,
        Shipped: true);

    /// <summary>
    /// What an instance is declared to need and what it has been measured to hold have drifted apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neither figure is wrong, which is why this reports rather than corrects.</b> A blueprint's
    /// minimum is curated from vendor documentation and describes a <em>game</em>; a footprint
    /// describes one world, with its own map, mods and players.
    /// </para>
    /// <para>
    /// <b>The three coverage rows are three questions.</b> Span asks whether the observation reaches
    /// across enough of the world's life; hours ask how much measurement is inside that reach; runs ask
    /// whether more than one session sits behind the peak. A server played two hours an evening for a
    /// month has sixty hours across thirty days, and a single gate reading days off the sample count
    /// would call that two days and refuse it.
    /// </para>
    /// <para>
    /// <b>Upward drift needs no trend and downward drift does.</b> An instance already holding more
    /// than its declaration is not going to be talked out of it by which way it is heading; lowering a
    /// figure against a working set still climbing is the one direction this rule could do harm in.
    /// </para>
    /// </remarks>
    private static RuleDefinition MemoryDeclarationDrift() => new(
        Id: "memory_declaration_drift",
        Name: "Report a footprint that has drifted from what the blueprint declares",
        // Nothing announces a drift, so there is no event that would bring this to an evaluation.
        Wakes: [],
        SubjectSource: SubjectSourceCatalog.InstancesWithFootprint,
        SubjectArguments: NoArguments,
        Signals: [],
        Rows:
        [
            new([Clause.Below("footprint.spanDays", MinSpanDays)],
                VerdictKind.Unreadable,
                "observations of {subject} span {footprint.spanDays:F1} days, short of the "
                + "{footprint.spanDays#:0.###} a world's growth shows up over"),

            new([Clause.Below("footprint.observedHours", MinObservedHours)],
                VerdictKind.Unreadable,
                "{subject} has been measured for {footprint.observedHours:F1} hours, short of the "
                + "{footprint.observedHours#:0.###} a peak means anything over"),

            new([Clause.Below("footprint.runs", MinRuns),
                 Clause.Below("footprint.observedHours", ContinuousRunHours)],
                VerdictKind.Unreadable,
                "{subject} has been seen to start {footprint.runs} time(s) and has not run the "
                + "{footprint.observedHours#:0.###} hours that would stand in for a second one"),

            new([Clause.Absent("footprint.workingSetPeakMb")],
                VerdictKind.Unreadable,
                "no working set has been measured for {subject}"),

            new([Clause.Present("declaration.heapFlag")],
                VerdictKind.DoesNotHold,
                "{subject} launches with {declaration.heapFlag}, so its {footprint.workingSetPeakMb}MB "
                + "working set is that setting rather than a measurement of what the world needs"),

            new([Clause.Absent("declaration.minRamMb")],
                VerdictKind.Unreadable,
                "{subject}'s blueprint declares no minimum to compare against"),

            new([Clause.Below("drift.absPctVsDeclared", DriftMarginPct)],
                VerdictKind.DoesNotHold,
                Holds + "within {drift.absPctVsDeclared#:0.###}% (" + Evidence + ")"),

            new([Clause.Above("drift.pctVsDeclared", 0), Clause.Above("footprint.oomKills", 0)],
                VerdictKind.Holds,
                Above + ", and the kernel has killed {footprint.oomKills} process(es) in it for want of memory"),

            new([Clause.Above("drift.pctVsDeclared", 0), Clause.Above("footprint.stallSeconds", 0)],
                VerdictKind.Holds,
                Above + ", having stalled {footprint.stallSeconds:F0}s waiting on memory"),

            new([Clause.Above("drift.pctVsDeclared", 0)],
                VerdictKind.Holds,
                Above + ", without ever stalling on memory"),

            new([Clause.Above("trend.growthPct", SettledGrowthPct)],
                VerdictKind.DoesNotHold,
                Holds + "but its working set has grown {trend.growthPct:F0}% across the window and has "
                + "not found its ceiling",
                // ⚠ Worth writing: the trend reader's own "no working-set series" is true and useless
                // beside the figures a decrement would have moved.
                UnreadableMessage: Holds + "but whether that has settled cannot be told: {reason}"),
        ],
        Default: new([],
            VerdictKind.Holds,
            Holds + "{drift.pctVsDeclared:+0;-0}% below it (" + Evidence + "), settled at "
            + "{trend.growthPct:+0;-0}% growth over {trend.points} points"),
        ActionId: ActionCatalog.None,
        Severity: EventSeverity.Info,
        Settle: DriftSettle,
        Suppression: DriftSuppression,
        Shipped: true);

    /// <summary>The opening every sentence about a drift shares: what is held against what is declared.</summary>
    private const string Holds =
        "{subject} holds {footprint.workingSetPeakMb}MB against {declaration.minRamMb}MB declared, ";

    /// <summary>How much measurement the figure rests on, which is what lets a reader see how thin it is.</summary>
    private const string Evidence =
        "measured over {footprint.observedHours:F0}h spanning {footprint.spanDays:F0} days";

    private const string Above = Holds + "{drift.pctVsDeclared:+0;-0}% above it (" + Evidence + ")";

    private static SignalBinding Episode(string alias, string signalId) =>
        SignalBinding.Of(alias, signalId,
            ("opensWith", BreachOpens), ("closesWith", BreachCloses), ("withinDays", LookBackDays));

    /// <summary>A source that needs nothing supplied.</summary>
    /// <remarks>
    /// A property rather than a field: static field initialisers run in the order they are written, and
    /// the rules above are built during <c>All</c>'s own initialisation.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> NoArguments =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> Arguments(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
}
