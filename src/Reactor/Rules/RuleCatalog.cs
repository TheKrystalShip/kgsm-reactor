using TheKrystalShip.KGSM.Core.Models;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// Every rule this build ships, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Three, and each survived the seven questions in <c>kgsm-reactor-plan.md</c> §P2: which events
/// exactly and what their fields hold here, what the false-positive shape is, what a human does
/// today, who owns the action, whether it is reversible, what happens if it fires for every instance
/// at once, and how long the condition must persist to be real.
/// </para>
/// <para>
/// <b>The rejections are the more useful half of that exercise</b> and are recorded in the plan, each
/// against the question it failed — a rule that restated a fact the give-up already carried, two that
/// reached into what the watchdog and the firewall own, and one whose false positive is visible to
/// players.
/// </para>
/// <para>
/// <b>A window carries the measurement that chose it, or says it has none.</b> Each is documented
/// against what was read off 30 days of this host — and where nothing could be read, the field says
/// so rather than presenting a guess in a default's clothing. The one figure measured before any of
/// them, a single <c>kgsm install</c> producing 22 events in a minute, is why: intuition is not to be
/// trusted on this host.
/// </para>
/// <para>
/// <b>A settle or suppression window is compiled; a rule's thresholds are not.</b> The two windows
/// are properties of how a condition behaves over time, measured here and the same on any host
/// running this software. A threshold is a judgement about one fleet — how much evidence is enough,
/// how wide a gap is worth mentioning — so each is declared as a <see cref="RuleParameter"/> an
/// operator moves without a rebuild.
/// </para>
/// </remarks>
internal static class RuleCatalog
{
    /// <summary>
    /// How long a failure is left alone before it is judged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured: a give-up that ends on its own takes at least 83 seconds to do it (p50 3.1m, p95
    /// 7.9m over 30 days of this host). Anything below that fires on every give-up that was about to
    /// fix itself, which is the one failure a settle window exists to prevent.
    /// </para>
    /// <para>
    /// Above the minimum and below the median rather than at p95, deliberately. A backup's value
    /// decays as the failed state gets overwritten, and this rule's action only ever creates — its
    /// false positive costs disk where its false negative costs the rollback candidate. So it is
    /// tuned toward capturing the failure promptly, at roughly six archives a month on this host.
    /// </para>
    /// <para>
    /// It still does the work the window is for: an operator who sees the alert and restarts inside
    /// it makes the rule settle rather than fire, which is correct — they have decided, and a backup
    /// taken over an instance that is coming back up is a hot archive mislabelled as the cold one it
    /// would have been.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan GiveUpSettle = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a crash is given to turn back into a running server before it is judged.
    /// </summary>
    /// <remarks>
    /// Measured: a crash that the supervisor rides out reaches <c>server.ready</c> again in 6.1
    /// seconds at p50 and 38 seconds at p95. A minute sits above the p95, so ordinary crash-restart
    /// never reaches an evaluation — which is the whole point, since crash-restart is the watchdog's
    /// job and this rule is only about what survives it.
    /// </remarks>
    private static readonly TimeSpan CrashSettle = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a threshold breach is given to clear before it counts as stuck.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured: <b>every</b> threshold episode on this host cleared on its own — twelve of twelve
    /// over 30 days, p50 6.2m, the slowest 39.7m. This sits above that slowest one.
    /// </para>
    /// <para>
    /// ⚠ The consequence is that this rule decides nothing here, and that is the measurement rather
    /// than a fault. A window shorter than the slowest observed self-clear would announce a breach
    /// that was going to end anyway, and the reading says every one of them was.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ThresholdSettle = TimeSpan.FromMinutes(45);

    /// <summary>
    /// How long one rule stays quiet about one subject after firing, where the rule's own repeat
    /// spacing differs enough from the host-wide setting to matter.
    /// </summary>
    /// <remarks>
    /// A window shorter than the p50 spacing between repeats suppresses nothing; one longer than the
    /// p95 hides the second occurrence of almost everything. Measured per waking event over 30 days:
    /// a give-up repeats every 5.5m (p50) / 10.3m (p95), and a threshold breach every 4.1h / 2.0d.
    /// A crash repeats every 25 seconds, which the host-wide 30 minutes already covers — so
    /// <c>update_regression</c> names no window and follows the host.
    /// </remarks>
    private static readonly TimeSpan GiveUpSuppression = TimeSpan.FromMinutes(15);

    /// <inheritdoc cref="GiveUpSuppression"/>
    private static readonly TimeSpan ThresholdSuppression = TimeSpan.FromHours(4);

    /// <summary>
    /// How soon after an update a failure is still that update's fault.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not measured, because this host has nothing to measure it from.</b> Thirty days hold two
    /// updates followed by a fault on the same server at all, at 112 and 168 minutes, and neither is
    /// plausibly the update's doing — one is a launcher that reported success while dying, the other
    /// a live server hours later. A window fitted to those two would be a causal claim built from
    /// coincidence, which is the opposite of what this field asserts.
    /// </remarks>
    private static readonly TimeSpan RegressionWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The fewest closed episodes that count as a distribution.
    /// </summary>
    /// <remarks>
    /// Below this, <c>threshold_stuck</c> answers <see cref="VerdictKind.Unreadable"/> rather than
    /// comparing against a percentile drawn from a handful of samples. "I do not have enough history
    /// to say" is a true statement; a p95 over three episodes is not.
    /// </remarks>
    private const int MinimumEpisodeSamples = 5;

    /// <summary>How far back a rule may look. Bounded by the ledger's own retention regardless.</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a drift is left alone before it is judged.
    /// </summary>
    /// <remarks>
    /// <b>Short, because there is nothing here for a settle window to do.</b> The window exists for a
    /// condition that was going to resolve itself, and a footprint drawn from a fortnight of
    /// measurement does not resolve itself in an hour. What keeps this rule from re-deciding is
    /// suppression, not settling. Two minutes only keeps a sweep during startup from deciding before
    /// the monitor is reachable — which would be an "unreadable" recorded against a daemon that was
    /// merely still coming up.
    /// </remarks>
    private static readonly TimeSpan DriftSettle = TimeSpan.FromMinutes(2);

    /// <summary>How long the drift rule stays quiet about one instance after speaking.</summary>
    /// <remarks>
    /// A day, because the answer cannot meaningfully change faster: the figure it compares is drawn
    /// from a fortnight of measurement, and a second opinion on it the same afternoon is the same
    /// opinion.
    /// </remarks>
    private static readonly TimeSpan DriftSuppression = TimeSpan.FromDays(1);

    /// <summary>The stable wire ids of the thresholds <c>memory_declaration_drift</c> compares against.</summary>
    /// <remarks>⚠ Immutable once shipped: a stored override is keyed by these.</remarks>
    private const string MinSpanDaysKey = "min_span_days";
    private const string MinObservedHoursKey = "min_observed_hours";
    private const string MinRunsKey = "min_runs";
    private const string ContinuousRunHoursKey = "continuous_run_hours";
    private const string DriftMarginPctKey = "drift_margin_pct";
    private const string SettledGrowthPctKey = "settled_growth_pct";

    /// <summary>
    /// How much evidence has to sit behind a footprint before a figure is read off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three questions, three gates, and collapsing them into one is a real defect.</b> Span asks
    /// whether the observation reaches across enough of the world's life; hours ask how much
    /// measurement is inside that reach; runs ask whether more than one session is behind the peak. A
    /// server played two hours an evening for a month has sixty hours of measurement spread over
    /// thirty days, and a single gate reading days off the sample count would call that two days and
    /// refuse it.
    /// </para>
    /// <para>
    /// <b>They are set where a figure becomes worth reading, not where it becomes beyond dispute.</b>
    /// This rule reports and acts on nothing — the decision record is its whole output — so an early
    /// verdict costs a ledger row that names the evidence it rests on, and every reader can see how
    /// thin that evidence is because the reason string carries it. The direction that could do harm is
    /// guarded separately and much harder: a decrement additionally has to clear
    /// <see cref="SettledGrowthPctKey"/>, which is what stops a figure being lowered against a working
    /// set still climbing.
    /// </para>
    /// <para>
    /// ⚠ <b>Hours sit above what the trend behind them needs.</b> A direction is read from
    /// five-minute buckets and needs two dozen of them, so a verdict admitted on less measurement than
    /// that would be one whose decrement guard could never answer — an instance permanently
    /// "cannot tell" rather than one being judged carefully.
    /// </para>
    /// <para>
    /// <b>The run gate takes a long unbroken stretch instead.</b> A boundary is only counted when this
    /// host was watching, so an instance running since before the monitor was installed reports none —
    /// and a day of continuous operation is not weaker evidence than two short evenings.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyList<RuleParameter> DriftGates =
    [
        new(MinSpanDaysKey, "Observations must span", Default: 2, Unit: "days",
            Description: "Calendar days between the first and last observation. Separate from the "
                + "hours below: a server played an hour an evening for a fortnight has little "
                + "measurement spread across a lot of calendar, and those are different evidence. "
                + "Zero asks for a verdict from however narrow a window has been seen."),
        new(MinObservedHoursKey, "Measured for at least", Default: 5, Unit: "h",
            Description: "Cumulative hours the server was observed running, summed across sessions — "
                + "five evenings of an hour count the same as one five-hour run. Below about two "
                + "hours the working-set trend has too few points to read a direction from, and a "
                + "decrement can never be cleared."),
        new(MinRunsKey, "Independent runs", Default: 2,
            Description: "Times the server was seen to start, which is what separates its behaviour "
                + "from one session's. A server running since before the monitor was installed shows "
                + "none of these, and the setting below is what lets it be judged anyway."),
        new(ContinuousRunHoursKey, "Or one unbroken run of", Default: 24, Unit: "h",
            Description: "Hours of continuous running that stand in for the run count when no start "
                + "has been observed."),
        new(DriftMarginPctKey, "Report a gap wider than", Default: 25, Unit: "%",
            Description: "How far what a server holds has to sit from what its blueprint declares "
                + "before the gap is worth saying. Neither figure is wrong — the blueprint describes "
                + "a game and the measurement describes one world — so this is the width at which "
                + "two true statements have drifted far enough apart to be worth a look."),
        new(SettledGrowthPctKey, "Treat as settled below", Default: 10, Unit: "%",
            Description: "How much a working set may grow across the trend window and still count as "
                + "having found its ceiling. This only ever blocks reporting a server as needing "
                + "LESS than it declares, which is the direction that would over-commit a host if it "
                + "were wrong. Raising it lets a still-climbing server be called settled."),
    ];

    public static IReadOnlyList<Rule> All { get; } =
    [
        GiveUpBackup(),
        UpdateRegression(),
        ThresholdStuck(),
        MemoryDeclarationDrift(),
    ];

    public static Rule? ById(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));


    /// <summary>
    /// What an instance is declared to need and what it has been measured to hold have drifted apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neither figure is wrong, which is why this reports rather than corrects.</b> A blueprint's
    /// <c>min_ram_mb</c> is curated from vendor documentation and describes a <em>game</em>; a
    /// footprint describes one world, with its own map, mods and players. The rule says two true
    /// statements about different things no longer agree, and a person decides what to do about it.
    /// </para>
    /// <para>
    /// <b>It proposes nothing and acts on nothing.</b> Rewriting instance config is on this leaf's
    /// never-list, so the decision record <em>is</em> the output: what was declared, what was measured,
    /// how much measurement is behind it, and which way it has been moving. A surface renders that
    /// beside the live footprint rather than storing a copy of a measurement that would go stale.
    /// </para>
    /// <para>
    /// <b>State-shaped.</b> Nothing emits an event when a footprint drifts — it is a slow fact about
    /// accumulated measurement, not a thing that happens — so the sweep rediscovers it and there is no
    /// wake to miss.
    /// </para>
    /// <para>
    /// ⚠ <b>Its known blind spot, measured rather than assumed.</b> The heap flag is read from the
    /// arguments KGSM launches with, and a game whose own start script sets it is invisible: Project
    /// Zomboid carries <c>-Xmx8g</c> inside <c>install/ProjectZomboid64.json</c> and will therefore be
    /// judged as a measured instance when its figure is a setting. It is left to say so in the ledger
    /// rather than covered by a heuristic guessed at now — what a behavioural test should look for is
    /// exactly what observe mode is here to establish.
    /// </para>
    /// </remarks>
    private static Rule MemoryDeclarationDrift() => new(
        Id: "memory_declaration_drift",
        Shape: RuleShape.State,
        // Nothing announces a drift, so there is no event type that would bring this to an evaluation.
        Wakes: [],
        Severity: EventSeverity.Info,
        Settle: DriftSettle,
        Suppression: DriftSuppression,
        Subjects: async (ctx, token) =>
        {
            Reading<IReadOnlyList<InstanceFootprint>> footprints =
                await ctx.Footprint.AllAsync(token).ConfigureAwait(false);

            // No monitor, no subjects. Returning every instance instead would produce an evaluation
            // per instance that could only answer "cannot tell", which is a ledger full of this leaf
            // reporting that another one is missing.
            return footprints is { State: ReadingState.Measured, Value: { } measured }
                ? [.. measured.Select(f => f.Instance)]
                : [];
        },
        Holds: async (ctx, token) =>
        {
            Reading<IReadOnlyList<InstanceFootprint>> all =
                await ctx.Footprint.AllAsync(token).ConfigureAwait(false);

            if (all is not { State: ReadingState.Measured, Value: { } measured })
                return Verdict.Unreadable($"the footprint could not be read: {all.Reason ?? "no reason given"}");

            InstanceFootprint? match = measured
                .Cast<InstanceFootprint?>()
                .FirstOrDefault(f => string.Equals(f!.Value.Instance, ctx.Subject, StringComparison.Ordinal));

            if (match is not { } footprint)
                return Verdict.Unreadable($"no footprint is recorded for {ctx.Subject}");

            // Three gates, because they are three questions, and each names the figure in force so a
            // reader can tell a rule that is waiting for evidence from one that has been tuned to.
            double minSpanDays = ctx.Threshold(MinSpanDaysKey);
            double minObservedHours = ctx.Threshold(MinObservedHoursKey);
            double minRuns = ctx.Threshold(MinRunsKey);
            double continuousRunHours = ctx.Threshold(ContinuousRunHoursKey);

            if (footprint.SpanDays < minSpanDays)
            {
                return Verdict.Unreadable(
                    $"observations of {ctx.Subject} span {footprint.SpanDays:F1} days, short of the "
                    + $"{minSpanDays:0.###} a world's growth shows up over");
            }

            if (footprint.ObservedHours < minObservedHours)
            {
                return Verdict.Unreadable(
                    $"{ctx.Subject} has been measured for {footprint.ObservedHours:F1} hours, short of "
                    + $"the {minObservedHours:0.###} a peak means anything over");
            }

            if (footprint.Runs < minRuns && footprint.ObservedHours < continuousRunHours)
            {
                return Verdict.Unreadable(
                    $"{ctx.Subject} has been seen to start {footprint.Runs} time(s) and has not run "
                    + $"the {continuousRunHours:0.###} hours that would stand in for a second one");
            }

            if (footprint.WorkingSetPeakMb is not { } observedMb)
                return Verdict.Unreadable($"no working set has been measured for {ctx.Subject}");

            Reading<MemoryDeclaration> declared =
                await ctx.World.MemoryDeclarationAsync(ctx.Subject, token).ConfigureAwait(false);

            if (declared.State != ReadingState.Measured)
                return Verdict.Unreadable($"what {ctx.Subject} declares could not be read: {declared.Reason}");

            // A launch line that fixes the heap fixes the measurement with it. Reporting a divergence
            // here would be reporting the distance between a vendor's advice and an operator's flag,
            // which is a true statement about nothing.
            if (declared.Value.HeapFlag is { } heap)
            {
                return Verdict.DoesNotHold(
                    $"{ctx.Subject} launches with {heap}, so its {observedMb}MB working set is that "
                    + "setting rather than a measurement of what the world needs");
            }

            if (declared.Value.MinRamMb is not { } declaredMb)
                return Verdict.Unreadable($"{ctx.Subject}'s blueprint declares no minimum to compare against");

            double driftPct = (observedMb - declaredMb) / (double)declaredMb * 100.0;
            string evidence =
                $"measured over {footprint.ObservedHours:F0}h spanning {footprint.SpanDays:F0} days";

            double marginPct = ctx.Threshold(DriftMarginPctKey);

            if (Math.Abs(driftPct) < marginPct)
            {
                return Verdict.DoesNotHold(
                    $"{ctx.Subject} holds {observedMb}MB against {declaredMb}MB declared, within "
                    + $"{marginPct:0.###}% ({evidence})");
            }

            // Upward drift needs no trend: an instance already holding more than its declaration is
            // not going to be talked out of it by which way it is heading.
            if (driftPct > 0)
            {
                string pressure = footprint.OomKills > 0
                    ? $", and the kernel has killed {footprint.OomKills} process(es) in it for want of memory"
                    : footprint.StallSeconds > 0
                        ? $", having stalled {footprint.StallSeconds:F0}s waiting on memory"
                        : ", without ever stalling on memory";

                return Verdict.Holds(
                    $"{ctx.Subject} holds {observedMb}MB against {declaredMb}MB declared, "
                    + $"{driftPct:+0;-0}% above it ({evidence}){pressure}");
            }

            // Downward is the direction that can do harm, so it carries the extra burden: a working
            // set still climbing has not found its ceiling, and a figure lowered against one still in
            // motion is wrong in the direction that over-commits a node.
            Reading<MemoryTrend> trend = await ctx.Footprint.TrendAsync(ctx.Subject, token).ConfigureAwait(false);

            if (trend.State != ReadingState.Measured)
            {
                return Verdict.Unreadable(
                    $"{ctx.Subject} holds {observedMb}MB against {declaredMb}MB declared, but whether "
                    + $"that has settled cannot be told: {trend.Reason}");
            }

            if (trend.Value.GrowthPct > ctx.Threshold(SettledGrowthPctKey))
            {
                return Verdict.DoesNotHold(
                    $"{ctx.Subject} holds {observedMb}MB against {declaredMb}MB declared, but its "
                    + $"working set has grown {trend.Value.GrowthPct:F0}% across the window and has "
                    + "not found its ceiling");
            }

            return Verdict.Holds(
                $"{ctx.Subject} holds {observedMb}MB against {declaredMb}MB declared, "
                + $"{driftPct:+0;-0}% below it ({evidence}), settled at {trend.Value.GrowthPct:+0;-0}% "
                + $"growth over {trend.Value.Points} points");
        },
        // Rewriting instance config is on the never-list, and the figure a surface would render is the
        // monitor's to serve. So the decision record is the whole output and there is nothing to stage.
        Action: _ => new ReactorAction.Nothing(),
        Parameters: DriftGates);

    /// <summary>
    /// The supervisor gave up on an instance — capture what it died as, before anybody debugs it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is the first rule allowed to act.</b> It only ever <em>creates</em>: a false
    /// positive costs disk, never a running server, which is the property that should decide what
    /// acts first rather than which rule is most valuable.
    /// </para>
    /// <para>
    /// <b>Q4, ownership.</b> The scheduler owns <em>scheduled</em> backups; an event-triggered one is
    /// nobody's today. The watchdog is finished with the instance by the time this fires — a give-up
    /// is a persisted latch and nothing on a timer leaves it.
    /// </para>
    /// <para>
    /// ⚠ <b>Q6, blast radius, and the number is real.</b> The largest backup on this host is 4.5 GB.
    /// A host OOM takes every instance down at once, so pinned-forever archives across a fleet fill
    /// the disk — and a full disk takes the fleet down, which is worse than the problem being solved.
    /// The cap and the free-space precondition belong with the dispatch at P5; in observe mode there
    /// is nothing yet to bound.
    /// </para>
    /// </remarks>
    private static Rule GiveUpBackup() => new(
        Id: "give_up_backup",
        Shape: RuleShape.Edge,
        Wakes: ["server.crash.exhausted"],
        Severity: EventSeverity.Danger,
        Settle: GiveUpSettle,
        Suppression: GiveUpSuppression,
        Holds: async (ctx, token) =>
        {
            var reading = await ctx.World.InstanceAsync(ctx.Subject, token).ConfigureAwait(false);
            if (reading.State != KGSM.Core.Models.ReadingState.Measured)
                return Verdict.Unreadable($"the supervisor could not be read: {reading.Reason ?? "no reason given"}");

            InstanceRunState state = reading.Value;

            // Re-asserted here rather than trusted from the event, and the settle window is exactly
            // the gap this closes: an operator start clears the give-up latch at any moment, and a
            // backup taken over an instance that is coming back up is a hot archive where a cold one
            // was intended — no error, just a quieter and worse result.
            return state.GaveUp
                ? Verdict.Holds($"still given up on after {(int)GiveUpSettle.TotalSeconds}s ({state.Restarts} consecutive failures)")
                : Verdict.DoesNotHold($"no longer given up on — the supervisor reports {state.Phase}");
        },
        Action: instance => new ReactorAction.CreateBackup(instance));

    /// <summary>
    /// An instance that failed shortly after an update — offer the archive taken before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A judgment nothing else on this host makes. The engine records the update and the supervisor
    /// records the failure; nobody joins them, and the join is the whole answer at three in the
    /// morning.
    /// </para>
    /// <para>
    /// ⚠ <b>Proposes, never acts.</b> A restore overwrites live state, which puts it permanently on
    /// the wrong side of "reversible".
    /// </para>
    /// </remarks>
    private static Rule UpdateRegression() => new(
        Id: "update_regression",
        Shape: RuleShape.Edge,
        Wakes: ["server.crash.exhausted", "server.crashed"],
        Severity: EventSeverity.Danger,
        // No Suppression: a crash repeats every 25s at p50, which the host-wide window already covers.
        Settle: CrashSettle,
        Holds: async (ctx, token) =>
        {
            HistoryEvent? update = ctx.History.LastOccurrence(
                "server.update.finished", ctx.Subject, ctx.Now - RegressionWindow);

            if (update is null)
                return Verdict.DoesNotHold(
                    $"no update finished on {ctx.Subject} in the last {(int)RegressionWindow.TotalMinutes} minutes");

            var reading = await ctx.World.InstanceAsync(ctx.Subject, token).ConfigureAwait(false);
            if (reading.State != KGSM.Core.Models.ReadingState.Measured)
                return Verdict.Unreadable($"the supervisor could not be read: {reading.Reason ?? "no reason given"}");

            if (reading.Value.Running)
                return Verdict.DoesNotHold("it is running again");

            var since = ctx.Now - update.Value.OccurredAt;
            return Verdict.Holds(
                $"failed {(int)since.TotalMinutes}m after an update finished, and is not running");
        },
        Action: instance => new ReactorAction.ProposeRestore(instance));

    /// <summary>
    /// A threshold episode open far longer than episodes of its kind usually last on this host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule that could not exist without the ledger.</b> The monitor reports breached and
    /// cleared; kgsm-api alerts on breached. Neither knows what normal is <em>here</em> — this reads
    /// the distribution of episodes that already closed and speaks only when one is well outside it.
    /// </para>
    /// <para>
    /// <b>State-shaped, so it cannot be missed.</b> It rediscovers open episodes from the ledger on
    /// every sweep instead of depending on having seen the breach go by.
    /// </para>
    /// <para>
    /// ⚠ <b>The reading that would retire it:</b> if episode durations here turn out to vary wildly,
    /// there is no "unusually long" to detect and this is noise. It stays in observe until the
    /// population report says otherwise.
    /// </para>
    /// </remarks>
    private static Rule ThresholdStuck() => new(
        Id: "threshold_stuck",
        Shape: RuleShape.State,
        Wakes: ["host.threshold.breached"],
        Severity: EventSeverity.Warn,
        Settle: ThresholdSettle,
        Suppression: ThresholdSuppression,
        Subjects: (ctx, _) =>
        {
            IReadOnlyList<OpenEpisode> open = ctx.History.OpenEpisodes(
                "host.threshold.breached", "host.threshold.cleared", DateTimeOffset.UtcNow - LookBack);
            return ValueTask.FromResult<IReadOnlyList<string>>([.. open.Select(e => e.Subject)]);
        },
        Holds: (ctx, _) =>
        {
            IReadOnlyList<OpenEpisode> open = ctx.History.OpenEpisodes(
                "host.threshold.breached", "host.threshold.cleared", ctx.Now - LookBack);

            OpenEpisode episode = open.FirstOrDefault(e => e.Subject == ctx.Subject);
            if (episode.Subject is null)
                return ValueTask.FromResult(Verdict.DoesNotHold("no episode is open"));

            (TimeSpan p95, int samples) = ctx.History.EpisodeDuration(
                "host.threshold.breached", "host.threshold.cleared", ctx.Subject, ctx.Now - LookBack);

            if (samples < MinimumEpisodeSamples)
                return ValueTask.FromResult(Verdict.Unreadable(
                    $"only {samples} closed episode(s) on record for {ctx.Subject} — too few to say what unusual is"));

            TimeSpan openFor = ctx.Now - episode.OpenedAt;
            return ValueTask.FromResult(openFor > p95
                ? Verdict.Holds(
                    $"open for {Format(openFor)}, past the p95 of {Format(p95)} over {samples} closed episodes")
                : Verdict.DoesNotHold(
                    $"open for {Format(openFor)}, within the p95 of {Format(p95)}"));
        },
        Action: _ => new ReactorAction.Nothing());

    private static string Format(TimeSpan span) =>
        span.TotalMinutes < 90 ? $"{span.TotalMinutes:F0}m" : $"{span.TotalHours:F1}h";
}
