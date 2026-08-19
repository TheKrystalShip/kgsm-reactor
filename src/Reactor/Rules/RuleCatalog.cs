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
/// ⚠ <b>Every window here is a placeholder</b> until the population report has a week behind it.
/// They are labelled as such individually. A number chosen before the measurement is a guess wearing
/// a default's clothing, and the one figure already measured — a single <c>kgsm install</c> producing
/// 22 events in one minute — says intuition is not to be trusted on this host.
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
    /// Measured: a crash that the supervisor rides out reaches <c>instance_ready</c> again in 6.1
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

    public static IReadOnlyList<Rule> All { get; } =
    [
        GiveUpBackup(),
        UpdateRegression(),
        ThresholdStuck(),
    ];

    public static Rule? ById(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

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
        Wakes: ["instance_failed"],
        Severity: Severity.Danger,
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
        Wakes: ["instance_failed", "instance_crashed"],
        Severity: Severity.Danger,
        // No Suppression: a crash repeats every 25s at p50, which the host-wide window already covers.
        Settle: CrashSettle,
        Holds: async (ctx, token) =>
        {
            HistoryEvent? update = ctx.History.LastOccurrence(
                "instance_update_finished", ctx.Subject, ctx.Now - RegressionWindow);

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
        Wakes: ["host_threshold_breached"],
        Severity: Severity.Warning,
        Settle: ThresholdSettle,
        Suppression: ThresholdSuppression,
        Subjects: (history, _) =>
        {
            IReadOnlyList<OpenEpisode> open = history.OpenEpisodes(
                "host_threshold_breached", "host_threshold_cleared", DateTimeOffset.UtcNow - LookBack);
            return ValueTask.FromResult<IReadOnlyList<string>>([.. open.Select(e => e.Subject)]);
        },
        Holds: (ctx, _) =>
        {
            IReadOnlyList<OpenEpisode> open = ctx.History.OpenEpisodes(
                "host_threshold_breached", "host_threshold_cleared", ctx.Now - LookBack);

            OpenEpisode episode = open.FirstOrDefault(e => e.Subject == ctx.Subject);
            if (episode.Subject is null)
                return ValueTask.FromResult(Verdict.DoesNotHold("no episode is open"));

            (TimeSpan p95, int samples) = ctx.History.EpisodeDuration(
                "host_threshold_breached", "host_threshold_cleared", ctx.Subject, ctx.Now - LookBack);

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
