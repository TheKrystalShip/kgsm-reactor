using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The rule that reports a footprint drifting away from what a blueprint declares.
/// </summary>
/// <remarks>
/// The figures here are the ones measured on the host this was written against, so a reader can see
/// which real instance each case is: <c>romestead</c> declares 4096 and holds 2908 across 25 days;
/// <c>Ketchup</c> declares 8192 and has never been seen to restart; <c>minecraft</c> declares 1024 and
/// launches with <c>-Xmx4096M</c>.
/// </remarks>
public class MemoryDriftRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

    /// <summary>The nth five-minute bucket, which is the cadence the monitor rolls history up to.</summary>
    private static DateTimeOffset At(int bucket) => Now.AddMinutes(5 * bucket);

    private static Rule TheRule =>
        RuleCatalog.All.Single(r => r.Id == "memory_declaration_drift");

    /// <summary>
    /// The thresholds the rule ships with — what it runs on where no file overrides them.
    /// </summary>
    /// <remarks>
    /// Resolved through the same path the daemon uses rather than restated here. A test carrying its
    /// own copy of a default would keep passing after the shipped figure moved, which is precisely the
    /// case it exists to catch.
    /// </remarks>
    private static IReadOnlyDictionary<string, double> Shipped =>
        RuleTuning.Defaults(RuleCatalog.All).For(TheRule.Id);

    /// <summary>The shipped thresholds with some moved, as an operator's file would.</summary>
    private static IReadOnlyDictionary<string, double> Tuned(params (string Key, double Value)[] moved) =>
        RuleTuning
            .Resolve(
                RuleCatalog.All,
                new Dictionary<string, IReadOnlyDictionary<string, double>>
                {
                    [TheRule.Id] = moved.ToDictionary(m => m.Key, m => m.Value),
                })
            .For(TheRule.Id);

    /// <summary>A footprint that clears every coverage gate, which each test then spoils one of.</summary>
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

    private static async Task<Verdict> Evaluate(
        InstanceFootprint footprint,
        MemoryDeclaration declaration,
        Reading<MemoryTrend>? trend = null,
        string subject = "romestead",
        IReadOnlyDictionary<string, double>? thresholds = null)
    {
        var ctx = new RuleContext(
            subject, Now,
            new StubWorld(Reading<MemoryDeclaration>.Measured(declaration)),
            new StubHistory(),
            new StubFootprints([footprint], trend ?? Reading<MemoryTrend>.Measured(new MemoryTrend(500, 1.0))),
            thresholds ?? Shipped);

        return await TheRule.Holds(ctx, CancellationToken.None);
    }

    // ---- coverage: the two axes are separate questions ----

    [Fact]
    public async Task A_short_calendar_span_cannot_be_decided_on()
    {
        // stationeers: 0.4 hours in one 40-minute block.
        Verdict v = await Evaluate(
            WellEvidenced(hours: 0.4, span: 0.02), new MemoryDeclaration(6144, 12288, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("span", v.Reason);
    }

    [Fact]
    public async Task Plenty_of_calendar_days_with_little_measurement_cannot_be_decided_on()
    {
        // The inverse of the case below, and the reason both gates exist: an instance started once a
        // month for five minutes spans a month and has been measured for nothing.
        Verdict v = await Evaluate(
            WellEvidenced(hours: 3, span: 30), new MemoryDeclaration(4096, 8192, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("measured for", v.Reason);
    }

    [Fact]
    public async Task Hours_spread_across_weeks_are_enough_even_though_they_are_few()
    {
        // ⚠ The defect this rule was nearly shipped with. romestead has 57 hours of measurement across
        // 25 calendar days — most evenings for most of a month, which is the second-best evidence on
        // its host. A single gate reading "days" off the sample count calls that two days and refuses,
        // leaving one eligible instance and a rule that fires on nothing.
        Verdict v = await Evaluate(WellEvidenced(), new MemoryDeclaration(4096, 8192, null));

        Assert.Equal(VerdictKind.Holds, v.Kind);
    }

    [Fact]
    public async Task One_run_is_not_enough_to_generalise_from()
    {
        // Under the unbroken-run stand-in, deliberately: above it a single run IS its own evidence,
        // which is the case below.
        Verdict v = await Evaluate(
            WellEvidenced(runs: 1, hours: 9), new MemoryDeclaration(4096, 8192, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("seen to start", v.Reason);
    }

    [Fact]
    public async Task A_long_continuous_run_stands_in_for_a_second_one()
    {
        // Ketchup: 704 hours across 30 days and not one boundary observed, because it has been running
        // since before this host started measuring. A month of continuous operation is not weaker
        // evidence than two evenings.
        Verdict v = await Evaluate(
            WellEvidenced(instance: "Ketchup", peakMb: 5433, runs: 0, hours: 704, span: 30),
            new MemoryDeclaration(8192, 16384, null),
            subject: "Ketchup");

        Assert.NotEqual(VerdictKind.Unreadable, v.Kind);
    }

    // ---- what makes a measurement meaningless ----

    [Fact]
    public async Task An_instance_whose_heap_is_fixed_by_a_flag_is_not_a_measurement()
    {
        // minecraft: declares 1024, holds 4500, and the 4500 is -Xmx4096M -XX:+AlwaysPreTouch touching
        // every page at boot. Reporting +340% here would be reporting the distance between a vendor's
        // advice and an operator's flag, which is a true statement about nothing.
        Verdict v = await Evaluate(
            WellEvidenced(instance: "minecraft", peakMb: 4500),
            new MemoryDeclaration(1024, 2048, "-Xmx4096M"),
            subject: "minecraft");

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Contains("-Xmx4096M", v.Reason);
    }

    [Fact]
    public async Task A_blueprint_declaring_nothing_leaves_nothing_to_compare()
    {
        Verdict v = await Evaluate(WellEvidenced(), new MemoryDeclaration(null, null, null));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("declares no minimum", v.Reason);
    }

    // ---- the comparison ----

    [Fact]
    public async Task A_figure_close_to_the_declaration_is_not_worth_saying()
    {
        // Ketchup at +8%: measured and declared genuinely agree, which is the answer for most instances
        // and must not produce a decision.
        Verdict v = await Evaluate(
            WellEvidenced(instance: "Ketchup", peakMb: 8852, runs: 0, hours: 704, span: 30),
            new MemoryDeclaration(8192, 16384, null),
            subject: "Ketchup");

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Contains("within", v.Reason);
    }

    [Fact]
    public async Task Holding_far_more_than_declared_is_reported_without_needing_a_trend()
    {
        // An instance already over its declaration is not going to be talked out of it by which way it
        // is heading, so no trend is consulted — proven by handing it one that cannot be read.
        Verdict v = await Evaluate(
            WellEvidenced(instance: "pz", peakMb: 12269),
            new MemoryDeclaration(8192, 16384, null),
            trend: Reading<MemoryTrend>.Unavailable("no series"),
            subject: "pz");

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Contains("above it", v.Reason);
    }

    [Fact]
    public async Task An_oom_kill_is_named_in_the_report()
    {
        InstanceFootprint killed = WellEvidenced(instance: "pz", peakMb: 12269) with { OomKills = 3 };
        Verdict v = await Evaluate(
            killed, new MemoryDeclaration(8192, 16384, null), subject: "pz");

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Contains("killed 3", v.Reason);
    }

    [Fact]
    public async Task Holding_far_less_than_declared_is_reported_once_it_has_settled()
    {
        Verdict v = await Evaluate(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            trend: Reading<MemoryTrend>.Measured(new MemoryTrend(600, 0.4)));

        Assert.Equal(VerdictKind.Holds, v.Kind);
        Assert.Contains("below it", v.Reason);
    }

    [Fact]
    public async Task A_working_set_still_climbing_is_not_lowered()
    {
        // The way this rule would do harm rather than noise: a world three weeks into growing has not
        // found its ceiling, and a figure lowered against a number still in motion over-commits a node.
        Verdict v = await Evaluate(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            trend: Reading<MemoryTrend>.Measured(new MemoryTrend(600, 34.0)));

        Assert.Equal(VerdictKind.DoesNotHold, v.Kind);
        Assert.Contains("not found its ceiling", v.Reason);
    }

    [Fact]
    public async Task An_unreadable_trend_blocks_a_decrement_rather_than_guessing_at_it()
    {
        Verdict v = await Evaluate(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            trend: Reading<MemoryTrend>.Unavailable("only 4 working-set points"));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("cannot be told", v.Reason);
    }

    // ---- the thresholds an operator moved ----

    /// <summary>
    /// ⚠ The gates are what decide whether this rule can speak at all, so a moved one has to reach it.
    /// </summary>
    [Fact]
    public async Task A_narrowed_span_gate_admits_a_footprint_the_shipped_one_refuses()
    {
        // Two days of evenings on a young world: refused on the shipped span, judged on a narrowed one.
        InstanceFootprint young = WellEvidenced(hours: 9, span: 1.9);

        Verdict shipped = await Evaluate(young, new MemoryDeclaration(4096, 8192, null));
        Assert.Equal(VerdictKind.Unreadable, shipped.Kind);
        Assert.Contains("span", shipped.Reason);

        Verdict tuned = await Evaluate(
            young, new MemoryDeclaration(4096, 8192, null),
            thresholds: Tuned(("min_span_days", 1)));

        Assert.Equal(VerdictKind.Holds, tuned.Kind);
        // The evidence travels with the verdict, which is what lets a reader see how thin it is.
        Assert.Contains("spanning 2 days", tuned.Reason);
    }

    [Fact]
    public async Task A_widened_margin_stops_a_gap_being_worth_saying()
    {
        // 2908 against 4096 declared is 29% below it — over the shipped margin, under a wider one.
        Verdict shipped = await Evaluate(WellEvidenced(), new MemoryDeclaration(4096, 8192, null));
        Assert.Equal(VerdictKind.Holds, shipped.Kind);

        Verdict tuned = await Evaluate(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            thresholds: Tuned(("drift_margin_pct", 40)));

        Assert.Equal(VerdictKind.DoesNotHold, tuned.Kind);
        Assert.Contains("within 40%", tuned.Reason);
    }

    /// <summary>Zero turns a gate off rather than making it impossible to pass.</summary>
    [Fact]
    public async Task Every_gate_off_judges_whatever_has_been_measured()
    {
        Verdict v = await Evaluate(
            WellEvidenced(hours: 0.4, span: 0.02, runs: 0),
            new MemoryDeclaration(4096, 8192, null),
            thresholds: Tuned(
                ("min_span_days", 0), ("min_observed_hours", 0),
                ("min_runs", 0), ("continuous_run_hours", 0)));

        Assert.Equal(VerdictKind.Holds, v.Kind);
    }

    // ---- the monitor being absent ----

    [Fact]
    public async Task No_monitor_means_no_subjects_rather_than_an_evaluation_per_instance()
    {
        var ctx = new SubjectContext(
            Now, new StubWorld(Reading<MemoryDeclaration>.Unavailable("no engine")),
            new StubHistory(), new UnreachableMonitor());

        IReadOnlyList<string> subjects = await TheRule.Subjects!(ctx, CancellationToken.None);

        Assert.Empty(subjects);
    }

    [Fact]
    public async Task No_footprint_for_the_subject_is_cannot_tell_not_no()
    {
        var ctx = new RuleContext(
            "never-measured", Now,
            new StubWorld(Reading<MemoryDeclaration>.Measured(new MemoryDeclaration(1024, 2048, null))),
            new StubHistory(),
            new StubFootprints([], Reading<MemoryTrend>.Unavailable("none")),
            Shipped);

        Verdict v = await TheRule.Holds(ctx, CancellationToken.None);

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
    }

    // ---- the monitor's wire shape ----

    /// <summary>
    /// ⚠ The history body parses as the monitor actually serves it, timestamps included.
    /// </summary>
    /// <remarks>
    /// <b>The failure this guards is silent and total.</b> A field typed against a shape the monitor
    /// does not send throws on the whole response, which reaches the rule as an unreadable trend — and
    /// an unreadable trend blocks only the decrement, so the rule goes on answering "cannot tell" for
    /// exactly the verdict the trend exists to permit. The literal below is a real response: <c>ts</c>
    /// is an ISO-8601 instant, and the rolled-up tier carries <c>min</c>/<c>max</c>/<c>n</c> beside the
    /// value.
    /// </remarks>
    [Fact]
    public void The_history_body_parses_as_the_monitor_serves_it()
    {
        const string body = """
            {
              "entityId": "Ketchup", "kind": "server", "range": "30d", "step": 300, "tier": "rollup",
              "series": {
                "memAnonBytes": [
                  { "ts": "2026-07-28T14:55:00+00:00", "value": 41.85, "min": 40.9, "max": 43.7, "n": 4 },
                  { "ts": "2026-07-28T15:00:00+00:00", "value": 42.10, "min": 41.2, "max": 44.0, "n": 4 }
                ]
              }
            }
            """;

        MetricsHistoryDto? parsed =
            JsonSerializer.Deserialize(body, MonitorJsonContext.Default.MetricsHistoryDto);

        List<HistoryPointDto> points = Assert.Single(parsed!.Series).Value;
        Assert.Equal(2, points.Count);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 28, 14, 55, 0, TimeSpan.Zero), points[0].Ts);
        Assert.Equal(41.85, points[0].Value);
    }

    // ---- the trend arithmetic ----

    [Fact]
    public void A_flat_series_reports_no_growth()
    {
        MemoryTrend t = MonitorFootprintSource.Compute(
            [.. Enumerable.Range(0, 40).Select(i => new HistoryPointDto(At(i), 2_900_000_000))]);

        Assert.Equal(0, t.GrowthPct);
        Assert.Equal(40, t.Points);
    }

    [Fact]
    public void A_climbing_series_reports_the_climb()
    {
        // 1000 for the first half, 1500 for the second: +50%.
        var points = new List<HistoryPointDto>();
        points.AddRange(Enumerable.Range(0, 20).Select(i => new HistoryPointDto(At(i), 1000)));
        points.AddRange(Enumerable.Range(20, 20).Select(i => new HistoryPointDto(At(i), 1500)));

        Assert.Equal(50, MonitorFootprintSource.Compute(points).GrowthPct);
    }

    [Fact]
    public void Points_out_of_order_are_sorted_before_the_halves_are_taken()
    {
        // The series is irregular — it exists only while the instance runs — so nothing guarantees the
        // order it arrives in, and halves taken off an unsorted list compare two random samples.
        var points = new List<HistoryPointDto>();
        points.AddRange(Enumerable.Range(20, 20).Select(i => new HistoryPointDto(At(i), 1500)));
        points.AddRange(Enumerable.Range(0, 20).Select(i => new HistoryPointDto(At(i), 1000)));

        Assert.Equal(50, MonitorFootprintSource.Compute(points).GrowthPct);
    }

    // ---- the heap-flag scan ----

    [Theory]
    [InlineData("-Xmx4096M -Xms4096M -XX:+UseG1GC", "-Xmx4096M")]
    [InlineData("-server -Xmx8g -jar server.jar", "-Xmx8g")]
    [InlineData("-XX:MaxRAMPercentage=75.0", "-XX:MaxRAMPercentage=75.0")]
    public void A_heap_argument_is_found_and_reported_verbatim(string args, string expected)
    {
        Assert.Equal(expected, WatchdogWorldView.FindHeapFlag(args));
    }

    [Theory]
    [InlineData("-useperfthreads -NoAsyncLoadingThread -publicport=8211")]
    [InlineData("")]
    [InlineData(null)]
    public void A_launch_line_without_one_reports_none_found(string? args)
    {
        // ⚠ Which is not the same as there being none. Project Zomboid sets -Xmx8g inside its own
        // install/ProjectZomboid64.json, where nothing on this path can see it.
        Assert.Null(WatchdogWorldView.FindHeapFlag(args));
    }

    // ---- stubs ----

    private sealed class StubWorld(Reading<MemoryDeclaration> declaration) : IWorldView
    {
        public ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<InstanceRunState>.Measured(new InstanceRunState("running", true, 0)));

        public ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(
            string instance, CancellationToken token) => ValueTask.FromResult(declaration);
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

    private sealed class StubHistory : IRuleHistory
    {
        public HistoryEvent? LastOccurrence(string eventType, string subject, DateTimeOffset notBefore) => null;

        public IReadOnlyList<OpenEpisode> OpenEpisodes(
            string opensWith, string closesWith, DateTimeOffset notBefore) => [];

        public (TimeSpan P95, int Samples) EpisodeDuration(
            string opensWith, string closesWith, string subject, DateTimeOffset notBefore) => (TimeSpan.Zero, 0);
    }
}
