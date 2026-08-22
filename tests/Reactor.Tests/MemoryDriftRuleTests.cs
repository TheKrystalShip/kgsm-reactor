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

    private static Rule TheRule =>
        RuleCatalog.All.Single(r => r.Id == "memory_declaration_drift");

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
        string subject = "romestead")
    {
        var ctx = new RuleContext(
            subject, Now,
            new StubWorld(Reading<MemoryDeclaration>.Measured(declaration)),
            new StubHistory(),
            new StubFootprints([footprint], trend ?? Reading<MemoryTrend>.Measured(new MemoryTrend(500, 1.0))));

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
        Verdict v = await Evaluate(
            WellEvidenced(runs: 1, hours: 30), new MemoryDeclaration(4096, 8192, null));

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
            new StubFootprints([], Reading<MemoryTrend>.Unavailable("none")));

        Verdict v = await TheRule.Holds(ctx, CancellationToken.None);

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
    }

    // ---- the trend arithmetic ----

    [Fact]
    public void A_flat_series_reports_no_growth()
    {
        MemoryTrend t = MonitorFootprintSource.Compute(
            [.. Enumerable.Range(0, 40).Select(i => new HistoryPointDto(i, 2_900_000_000))]);

        Assert.Equal(0, t.GrowthPct);
        Assert.Equal(40, t.Points);
    }

    [Fact]
    public void A_climbing_series_reports_the_climb()
    {
        // 1000 for the first half, 1500 for the second: +50%.
        var points = new List<HistoryPointDto>();
        points.AddRange(Enumerable.Range(0, 20).Select(i => new HistoryPointDto(i, 1000)));
        points.AddRange(Enumerable.Range(20, 20).Select(i => new HistoryPointDto(i, 1500)));

        Assert.Equal(50, MonitorFootprintSource.Compute(points).GrowthPct);
    }

    [Fact]
    public void Points_out_of_order_are_sorted_before_the_halves_are_taken()
    {
        // The series is irregular — it exists only while the instance runs — so nothing guarantees the
        // order it arrives in, and halves taken off an unsorted list compare two random samples.
        var points = new List<HistoryPointDto>();
        points.AddRange(Enumerable.Range(20, 20).Select(i => new HistoryPointDto(i, 1500)));
        points.AddRange(Enumerable.Range(0, 20).Select(i => new HistoryPointDto(i, 1000)));

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
