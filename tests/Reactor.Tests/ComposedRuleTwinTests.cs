using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// Every rule this build ships, judged twice: once by the rule written by hand, once by the same rule
/// composed from the catalogs.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the acceptance test for composed rules, and it is the whole design method.</b> The model
/// is correct when the four compiled rules can be expressed in it and produce the same verdict
/// <em>and the same sentence</em> on the same fixtures. Not similar ones — a model that cannot restate
/// <c>memory_declaration_drift</c> would not carry the fifth rule either, and this is the cheapest
/// possible place to find that out.
/// </para>
/// <para>
/// ⚠ <b>The sentence is asserted, not just the verdict.</b> A decision reads <em>"Ketchup holds 5433MB
/// against 8192MB declared, -34% below it"</em> because the rule that measured it wrote that sentence.
/// A composition that reached the same conclusion while losing the figures would pass a verdict-only
/// test and destroy the thing the record exists for.
/// </para>
/// </remarks>
public class ComposedRuleTwinTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

    /// <summary>Judge one subject by both rules, and demand they agree exactly.</summary>
    private static async Task<Verdict> Twin(
        string ruleId,
        string subject,
        IWorldView world,
        IRuleHistory history,
        IFootprintSource footprint)
    {
        Rule compiled = HandWrittenRules.All.Single(r => r.Id == ruleId);
        RuleDefinition definition = ShippedRules.All.Single(r => r.Id == ruleId);
        Rule composed = RuleEvaluator.ToRule(definition);

        var context = new RuleContext(subject, Now, world, history, footprint);

        Verdict left = await compiled.Holds(context, CancellationToken.None);
        Verdict right = await composed.Holds(context, CancellationToken.None);

        Assert.Equal(left.Kind, right.Kind);
        Assert.Equal(left.Reason, right.Reason);

        return left;
    }

    private static Task<Verdict> Drift(
        InstanceFootprint footprint,
        MemoryDeclaration declaration,
        Reading<MemoryTrend>? trend = null,
        string subject = "romestead") =>
        Twin("memory_declaration_drift", subject,
            new StubWorld(Declaration: Reading<MemoryDeclaration>.Measured(declaration)),
            new StubHistory(),
            new StubFootprints([footprint], trend ?? Reading<MemoryTrend>.Measured(new MemoryTrend(500, 1.0))));

    /// <summary>A footprint that clears every coverage gate, which each case then spoils one of.</summary>
    private static InstanceFootprint WellEvidenced(
        string instance = "romestead", double peakMb = 2908, long runs = 12,
        double hours = 57, double span = 25) => new(
        Instance: instance,
        WorkingSetPeakBytes: peakMb * 1024 * 1024,
        WorkingSetAvgBytes: peakMb * 0.75 * 1024 * 1024,
        PeakBytes: peakMb * 1.1 * 1024 * 1024,
        OomKills: 0,
        MaxEvents: 0,
        StallSeconds: 0,
        Runs: runs,
        ObservedHours: hours,
        SpanDays: span,
        Samples: 4000);

    // ---- memory_declaration_drift: the rule that decides the model ----

    [Fact]
    public async Task A_short_calendar_span_reads_the_same_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(hours: 0.4, span: 0.02), new MemoryDeclaration(6144, 12288, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("observed across", v.Reason);
    }

    [Fact]
    public async Task Calendar_without_measurement_reads_the_same_either_way()
    {
        Verdict v = await Drift(WellEvidenced(hours: 3, span: 30), new MemoryDeclaration(4096, 8192, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("measured for", v.Reason);
    }

    [Fact]
    public async Task Hours_spread_across_weeks_reach_a_verdict_either_way()
    {
        Verdict v = await Drift(WellEvidenced(), new MemoryDeclaration(4096, 8192, null));

        Assert.Equal(VerdictKind.Holds, v.Kind);
    }

    /// <summary>
    /// ⚠ The two gates in one row, which is the case a flat list of conditions could not express.
    /// </summary>
    /// <remarks>
    /// The compiled rule refuses when runs are few <em>and</em> the unbroken stretch is short, and its
    /// sentence names the second figure rather than the first. The composed row therefore has to hold
    /// two clauses over the same signal at two different comparands — the hours gate above compares
    /// against five, and this one against twenty-four — and name the right one in its prose.
    /// </remarks>
    [Fact]
    public async Task Too_few_runs_names_the_unbroken_stretch_it_wanted_instead()
    {
        Verdict v = await Drift(WellEvidenced(runs: 1, hours: 9), new MemoryDeclaration(4096, 8192, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("seen to start 1 time(s)", v.Reason);
        Assert.Contains("has not run the 24 hours", v.Reason);
    }

    [Fact]
    public async Task A_long_continuous_run_stands_in_for_a_second_one_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(instance: "Ketchup", peakMb: 5433, runs: 0, hours: 704, span: 30),
            new MemoryDeclaration(8192, 16384, null),
            subject: "Ketchup");

        Assert.NotEqual(VerdictKind.Unreadable, v.Kind);
    }

    [Fact]
    public async Task A_fixed_heap_reads_the_same_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(instance: "minecraft", peakMb: 4500),
            new MemoryDeclaration(1024, 2048, "-Xmx4096M"),
            subject: "minecraft");

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Contains("-Xmx4096M", v.Reason);
    }

    [Fact]
    public async Task A_blueprint_declaring_nothing_reads_the_same_either_way()
    {
        Verdict v = await Drift(WellEvidenced(), new MemoryDeclaration(null, null, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("declares no minimum", v.Reason);
    }

    [Fact]
    public async Task A_figure_close_to_the_declaration_reads_the_same_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(instance: "Ketchup", peakMb: 8852, runs: 0, hours: 704, span: 30),
            new MemoryDeclaration(8192, 16384, null),
            subject: "Ketchup");

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Contains("within 25%", v.Reason);
    }

    /// <summary>
    /// ⚠ The lazy read, which is a property of the evaluator rather than of the rule.
    /// </summary>
    /// <remarks>
    /// An instance already over its declaration is reported without the trend ever being asked for —
    /// proven by handing both rules a trend that cannot be read. A composition that read every signal
    /// a rule mentions before deciding would answer "cannot tell" here, which is the wrong answer to
    /// the easiest question this rule is asked.
    /// </remarks>
    [Fact]
    public async Task Holding_more_than_declared_consults_no_trend_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(instance: "pz", peakMb: 12269),
            new MemoryDeclaration(8192, 16384, null),
            trend: Reading<MemoryTrend>.Unavailable("no series"),
            subject: "pz");

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Contains("above it", v.Reason);
        Assert.Contains("without ever stalling on memory", v.Reason);
    }

    [Fact]
    public async Task An_oom_kill_is_named_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(instance: "pz", peakMb: 12269) with { OomKills = 3 },
            new MemoryDeclaration(8192, 16384, null),
            subject: "pz");

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Contains("killed 3", v.Reason);
    }

    [Fact]
    public async Task A_stall_is_named_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(instance: "pz", peakMb: 12269) with { StallSeconds = 42 },
            new MemoryDeclaration(8192, 16384, null),
            subject: "pz");

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Contains("stalled 42s", v.Reason);
    }

    [Fact]
    public async Task Holding_less_than_declared_reads_the_same_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            trend: Reading<MemoryTrend>.Measured(new MemoryTrend(600, 0.4)));

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Contains("below it", v.Reason);
        Assert.Contains("settled — its working set moved +0% over 600 measurements", v.Reason);
    }

    [Fact]
    public async Task A_working_set_still_climbing_reads_the_same_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            trend: Reading<MemoryTrend>.Measured(new MemoryTrend(600, 34.0)));

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Contains("not found its ceiling", v.Reason);
    }

    /// <summary>
    /// ⚠ The row that writes its own sentence for a signal it could not read.
    /// </summary>
    /// <remarks>
    /// The trend reader's own words are "only 4 working-set points", which is true and useless beside
    /// the figures a decrement would have moved. The row says what was at stake and carries the
    /// reader's reason inside its own sentence — the case that justifies a row owning two messages.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_trend_says_what_was_at_stake_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            trend: Reading<MemoryTrend>.Unavailable("only 4 working-set points"));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("peaks at 2908MB where its blueprint declares 4096MB", v.Reason);
        Assert.Contains("cannot be told: only 4 working-set points", v.Reason);
    }

    [Fact]
    public async Task No_footprint_for_the_subject_reads_the_same_either_way()
    {
        Verdict v = await Twin("memory_declaration_drift", "never-measured",
            new StubWorld(Reading<MemoryDeclaration>.Measured(new MemoryDeclaration(1024, 2048, null))),
            new StubHistory(),
            new StubFootprints([], Reading<MemoryTrend>.Unavailable("none")));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("no footprint is recorded", v.Reason);
    }

    [Fact]
    public async Task An_unreachable_monitor_reads_the_same_either_way()
    {
        Verdict v = await Twin("memory_declaration_drift", "romestead",
            new StubWorld(Reading<MemoryDeclaration>.Measured(new MemoryDeclaration(1024, 2048, null))),
            new StubHistory(),
            new UnreachableMonitor());

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("the footprint could not be read", v.Reason);
    }

    [Fact]
    public async Task A_measured_instance_with_no_working_set_reads_the_same_either_way()
    {
        Verdict v = await Drift(
            WellEvidenced() with { WorkingSetPeakBytes = null },
            new MemoryDeclaration(4096, 8192, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("no working set has been measured", v.Reason);
    }

    [Fact]
    public async Task An_unreadable_declaration_reads_the_same_either_way()
    {
        Verdict v = await Twin("memory_declaration_drift", "romestead",
            new StubWorld(Reading<MemoryDeclaration>.Unavailable("the engine could not be run")),
            new StubHistory(),
            new StubFootprints([WellEvidenced()], Reading<MemoryTrend>.Measured(new MemoryTrend(500, 1.0))));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("declares could not be read", v.Reason);
    }

    // ---- give_up_backup ----

    [Fact]
    public async Task A_standing_give_up_reads_the_same_either_way()
    {
        Verdict v = await Twin("give_up_backup", "pz",
            new StubWorld(Instance: Reading<InstanceRunState>.Measured(new InstanceRunState("failed", true, 5))),
            new StubHistory(), new UnreachableMonitor());

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Equal(
            "pz crashed 5 times in a row and the supervisor has stopped trying to restart it — it "
            + "stays down until somebody starts it",
            v.Reason);
    }

    [Fact]
    public async Task A_give_up_that_cleared_reads_the_same_either_way()
    {
        Verdict v = await Twin("give_up_backup", "pz",
            new StubWorld(Instance: Reading<InstanceRunState>.Measured(new InstanceRunState("running", true, 0))),
            new StubHistory(), new UnreachableMonitor());

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Equal(
            "the supervisor is looking after pz again — it reports the instance running", v.Reason);
    }

    [Fact]
    public async Task An_unreadable_supervisor_reads_the_same_either_way()
    {
        Verdict v = await Twin("give_up_backup", "pz",
            new StubWorld(Instance: Reading<InstanceRunState>.Unavailable("the watchdog socket is not there")),
            new StubHistory(), new UnreachableMonitor());

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Equal(
            "the supervisor could not be read: the watchdog socket is not there", v.Reason);
    }

    // ---- update_regression ----

    [Fact]
    public async Task A_failure_with_no_update_behind_it_reads_the_same_either_way()
    {
        Verdict v = await Twin("update_regression", "pz",
            new StubWorld(Instance: Reading<InstanceRunState>.Measured(new InstanceRunState("failed", true, 5))),
            new StubHistory(), new UnreachableMonitor());

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Equal(
            "nothing updated pz in the last 30 minutes, so whatever is wrong with it did not "
            + "arrive with an update",
            v.Reason);
    }

    [Fact]
    public async Task A_failure_that_recovered_reads_the_same_either_way()
    {
        Verdict v = await Twin("update_regression", "pz",
            new StubWorld(Instance: Reading<InstanceRunState>.Measured(new InstanceRunState("running", true, 0))),
            new StubHistory(LastUpdate: Now.AddMinutes(-12)), new UnreachableMonitor());

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Equal("pz is running again", v.Reason);
    }

    [Fact]
    public async Task A_failure_after_an_update_reads_the_same_either_way()
    {
        Verdict v = await Twin("update_regression", "pz",
            new StubWorld(Instance: Reading<InstanceRunState>.Measured(new InstanceRunState("failed", true, 5))),
            new StubHistory(LastUpdate: Now.AddMinutes(-12)), new UnreachableMonitor());

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Equal(
            "pz failed 12m after an update finished and is not running now — near enough in time "
            + "that the update is worth ruling out before anything else",
            v.Reason);
    }

    // ---- threshold_stuck ----

    private static Task<Verdict> Threshold(StubHistory history) =>
        Twin("threshold_stuck", "cpu",
            new StubWorld(), history, new UnreachableMonitor());

    [Fact]
    public async Task A_closed_episode_reads_the_same_either_way()
    {
        Verdict v = await Threshold(new StubHistory());

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Equal("no threshold on cpu is currently breached", v.Reason);
    }

    [Fact]
    public async Task Too_little_history_to_compare_reads_the_same_either_way()
    {
        Verdict v = await Threshold(new StubHistory(
            Open: [Episode("cpu", Now.AddHours(-3))],
            Episodes: (TimeSpan.FromMinutes(20), 3)));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Equal(
            "cpu has only 3 breach(es) on record that cleared — too few to say what unusually "
            + "long means on this host",
            v.Reason);
    }

    /// <summary>
    /// ⚠ One measurement compared against another, which no threshold could express.
    /// </summary>
    /// <remarks>
    /// How long this episode has been open against how long episodes of its kind usually last here.
    /// A clause model offering only fixed figures could not state it, which is why a comparand may be
    /// another of the rule's own signals.
    /// </remarks>
    [Fact]
    public async Task An_episode_past_its_p95_reads_the_same_either_way()
    {
        Verdict v = await Threshold(new StubHistory(
            Open: [Episode("cpu", Now.AddHours(-3))],
            Episodes: (TimeSpan.FromMinutes(40), 12)));

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Equal(
            "cpu has been over its threshold for 3.0h — 95% of the 12 breaches on record here "
            + "cleared within 40m, so this one is not clearing the way they do",
            v.Reason);
    }

    [Fact]
    public async Task An_episode_inside_its_p95_reads_the_same_either_way()
    {
        Verdict v = await Threshold(new StubHistory(
            Open: [Episode("cpu", Now.AddMinutes(-20))],
            Episodes: (TimeSpan.FromMinutes(40), 12)));

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Equal(
            "cpu has been over its threshold for 20m, inside the 40m that 95% of its breaches "
            + "clear within",
            v.Reason);
    }

    // ---- what each rule evaluates over ----

    [Fact]
    public async Task Both_rules_enumerate_the_same_subjects()
    {
        var context = new SubjectContext(
            Now, new StubWorld(), new StubHistory(Open: [Episode("cpu", Now.AddHours(-1))]),
            new StubFootprints(
                [WellEvidenced(instance: "romestead"), WellEvidenced(instance: "pz")],
                Reading<MemoryTrend>.Unavailable("none")));

        foreach (string id in new[] { "memory_declaration_drift", "threshold_stuck" })
        {
            Rule compiled = HandWrittenRules.All.Single(r => r.Id == id);
            Rule composed = RuleEvaluator.ToRule(ShippedRules.All.Single(r => r.Id == id));

            Assert.Equal(
                await compiled.Subjects!(context, CancellationToken.None),
                await composed.Subjects!(context, CancellationToken.None));
        }
    }

    [Fact]
    public async Task No_monitor_means_no_subjects_either_way()
    {
        var context = new SubjectContext(Now, new StubWorld(), new StubHistory(), new UnreachableMonitor());

        Rule composed = RuleEvaluator.ToRule(
            ShippedRules.All.Single(r => r.Id == "memory_declaration_drift"));

        Assert.Empty(await composed.Subjects!(context, CancellationToken.None));
    }

    // ---- the seeds restate the compiled catalog, field for field ----

    /// <summary>
    /// ⚠ The seeds keep the ids and the measured windows of the rules they restate.
    /// </summary>
    /// <remarks>
    /// A composed rule that quietly lost the 45-minute threshold window would be a new rule wearing an
    /// old rule's name, and its decisions would fold into the old one's episodes.
    /// </remarks>
    [Fact]
    public void Every_compiled_rule_has_a_seed_with_its_id_and_its_windows()
    {
        Assert.Equal(HandWrittenRules.All.Count, ShippedRules.All.Count);

        foreach (Rule compiled in HandWrittenRules.All)
        {
            RuleDefinition seed = Assert.Single(ShippedRules.All, s => s.Id == compiled.Id);

            Assert.Equal(compiled.Shape, seed.Shape);
            Assert.Equal(compiled.Settle, seed.Settle);
            Assert.Equal(compiled.Suppression, seed.Suppression);
            Assert.Equal(compiled.Severity, seed.Severity);
            Assert.Equal(compiled.Wakes, seed.Wakes);
            Assert.Equal(compiled.Action("x").Name, seed.ActionId);
        }
    }

    // ---- stubs ----

    private static OpenEpisode Episode(string subject, DateTimeOffset openedAt) =>
        new(subject, SubjectKind.Host, openedAt, new EventSource("kgsm-monitor", "seg", 0, null));

    private sealed class StubWorld(
        Reading<MemoryDeclaration>? Declaration = null,
        Reading<InstanceRunState>? Instance = null) : IWorldView
    {
        public ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Instance
                ?? Reading<InstanceRunState>.Measured(new InstanceRunState("running", true, 0)));

        public ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Declaration
                ?? Reading<MemoryDeclaration>.Unavailable("no declaration in this fixture"));

        public ValueTask<Reading<InstanceSupervision>> SupervisionAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<InstanceSupervision>.Measured(new InstanceSupervision(null)));
    }

    private sealed class StubFootprints(
        IReadOnlyList<InstanceFootprint> footprints, Reading<MemoryTrend> trend) : IFootprintSource
    {
        public ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token) =>
            ValueTask.FromResult(Reading<IReadOnlyList<InstanceFootprint>>.Measured(footprints));

        public ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(trend);
    }

    private sealed class UnreachableMonitor : IFootprintSource
    {
        public ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token) =>
            ValueTask.FromResult(Reading<IReadOnlyList<InstanceFootprint>>.Unavailable("no monitor here"));

        public ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryTrend>.Unavailable("no monitor here"));
    }

    private sealed class StubHistory(
        DateTimeOffset? LastUpdate = null,
        IReadOnlyList<OpenEpisode>? Open = null,
        (TimeSpan P95, int Samples)? Episodes = null) : IRuleHistory
    {
        public HistoryEvent? LastOccurrence(string eventType, string subject, DateTimeOffset notBefore) =>
            LastUpdate is { } at && at >= notBefore
                ? new HistoryEvent(eventType, subject, at)
                : null;

        public IReadOnlyList<OpenEpisode> OpenEpisodes(
            string opensWith, string closesWith, DateTimeOffset notBefore) => Open ?? [];

        public (TimeSpan P95, int Samples) EpisodeDuration(
            string opensWith, string closesWith, string subject, DateTimeOffset notBefore) =>
            Episodes ?? (TimeSpan.Zero, 0);
    }
}
