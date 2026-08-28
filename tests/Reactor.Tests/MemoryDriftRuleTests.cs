using System.Text.Json;

using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// What the drift rule reads, and what happens when somebody moves one of the figures it compares
/// against.
/// </summary>
/// <remarks>
/// <para>
/// The rule's own verdicts are pinned in <see cref="ComposedRuleTwinTests"/>, which judges every
/// fixture twice and demands the same sentence from both. What is left here is the half that is not a
/// verdict: whether a moved comparand actually reaches the rule, and whether the readings underneath
/// it — the monitor's wire shape, the trend arithmetic, the heap-flag scan — are what the rule thinks
/// they are.
/// </para>
/// <para>
/// The figures are the ones measured on the host this was written against, so a reader can see which
/// real instance each case is: <c>romestead</c> declares 4096 and holds 2908 across 25 days;
/// <c>Ketchup</c> declares 8192 and has never been seen to restart; <c>minecraft</c> declares 1024 and
/// launches with <c>-Xmx4096M</c>.
/// </para>
/// </remarks>
public class MemoryDriftRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

    /// <summary>The nth five-minute bucket, which is the cadence the monitor rolls history up to.</summary>
    private static DateTimeOffset At(int bucket) => Now.AddMinutes(5 * bucket);

    private static RuleDefinition TheRule =>
        ShippedRules.All.Single(r => r.Id == "memory_declaration_drift");

    /// <summary>
    /// The same rule with one comparand moved, as an operator's file would have it.
    /// </summary>
    /// <remarks>
    /// <b>Located by the figure it currently holds, not by position.</b> Two steps compare the same
    /// signal — the hours gate against five and the unbroken-run stand-in against twenty-four — so a
    /// helper that took the first match would silently move the wrong one, and a test built on it would
    /// pass while proving nothing. Naming the figure being replaced also fails loudly if the shipped
    /// one moves, which is the second thing worth knowing.
    /// </remarks>
    private static RuleDefinition Moved(RuleDefinition rule, string alias, double from, double to)
    {
        List<GuardRow> rows = [];
        bool found = false;

        foreach (GuardRow row in rule.Rows)
        {
            List<Clause> clauses = [];

            foreach (Clause clause in row.Clauses)
            {
                if (!found
                    && string.Equals(clause.Alias, alias, StringComparison.Ordinal)
                    && clause.Against is Comparand.Literal { Value.Number: var held }
                    && held.Equals(from))
                {
                    clauses.Add(clause with { Against = Comparand.Literal.Number(to) });
                    found = true;
                    continue;
                }

                clauses.Add(clause);
            }

            rows.Add(row with { Clauses = clauses });
        }

        Assert.True(found, $"no step compares {alias} against {from} — the shipped figure has moved");
        return rule with { Rows = rows };
    }

    private static async Task<Verdict> Evaluate(
        InstanceFootprint footprint,
        MemoryDeclaration declaration,
        Reading<MemoryTrend>? trend = null,
        string subject = "romestead",
        RuleDefinition? rule = null)
    {
        var context = new RuleContext(
            subject, Now,
            new StubWorld(Reading<MemoryDeclaration>.Measured(declaration)),
            new StubHistory(),
            new StubFootprints([footprint], trend ?? Reading<MemoryTrend>.Measured(new MemoryTrend(500, 1.0))));

        return await RuleEvaluator.ToRule(rule ?? TheRule).Holds(context, CancellationToken.None);
    }

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

    // ---- the figures an operator moved ----

    /// <summary>
    /// The gates are what decide whether this rule can speak at all, so a moved one has to reach it.
    /// </summary>
    [Fact]
    public async Task A_narrowed_span_gate_admits_a_footprint_the_shipped_one_refuses()
    {
        // Two days of evenings on a young world: refused on the shipped span, judged on a narrowed one.
        InstanceFootprint young = WellEvidenced(hours: 9, span: 1.9);

        Verdict shipped = await Evaluate(young, new MemoryDeclaration(4096, 8192, null));
        Assert.Equal(VerdictKind.Unreadable, shipped.Kind);
        Assert.Contains("observed across", shipped.Reason);

        Verdict tuned = await Evaluate(
            young, new MemoryDeclaration(4096, 8192, null),
            rule: Moved(TheRule, "footprint.spanDays", 2, 1));

        Assert.Equal(VerdictKind.Holds, tuned.Kind);
        // The evidence travels with the verdict, which is what lets a reader see how thin it is.
        Assert.Contains("spanning 2 days", tuned.Reason);
    }

    /// <summary>
    /// A moved figure has to reach the rule's <em>prose</em> as well as its arithmetic.
    /// </summary>
    /// <remarks>
    /// The sentence names the width the gap was held to, and a step that compared against the new
    /// figure while printing the old one would produce a record contradicting the decision it explains.
    /// </remarks>
    [Fact]
    public async Task A_widened_margin_stops_a_gap_being_worth_saying_and_says_the_new_width()
    {
        // 2908 against 4096 declared is 29% below it — over the shipped margin, under a wider one.
        Verdict shipped = await Evaluate(WellEvidenced(), new MemoryDeclaration(4096, 8192, null));
        Assert.Equal(VerdictKind.Holds, shipped.Kind);

        Verdict tuned = await Evaluate(
            WellEvidenced(), new MemoryDeclaration(4096, 8192, null),
            rule: Moved(TheRule, "drift.absPctVsDeclared", 25, 40));

        Assert.Equal(VerdictKind.DoesNotHold, tuned.Kind);
        Assert.Contains("within 40%", tuned.Reason);
    }

    /// <summary>Zero turns a gate off rather than making it impossible to pass.</summary>
    [Fact]
    public async Task Every_gate_off_judges_whatever_has_been_measured()
    {
        RuleDefinition ungated = TheRule;
        ungated = Moved(ungated, "footprint.spanDays", 2, 0);
        ungated = Moved(ungated, "footprint.observedHours", 5, 0);
        ungated = Moved(ungated, "footprint.runs", 2, 0);
        ungated = Moved(ungated, "footprint.observedHours", 24, 0);

        Verdict v = await Evaluate(
            WellEvidenced(hours: 0.4, span: 0.02, runs: 0),
            new MemoryDeclaration(4096, 8192, null),
            rule: ungated);

        Assert.Equal(VerdictKind.Holds, v.Kind);
    }

    /// <summary>
    /// The unbroken-run stand-in is a second comparison of a signal another step already compares.
    /// </summary>
    /// <remarks>
    /// Moving the hours gate must not move it, and moving it must not move the hours gate. They are
    /// different questions that happen to read the same measurement, and a model keyed on the signal
    /// rather than on the step would collapse them into one.
    /// </remarks>
    [Fact]
    public async Task Moving_the_hours_gate_leaves_the_unbroken_run_stand_in_alone()
    {
        // Nine hours across one run: over a lowered hours gate, still under the 24-hour stand-in, and
        // with too few runs to pass on its own.
        Verdict v = await Evaluate(
            WellEvidenced(runs: 1, hours: 9),
            new MemoryDeclaration(4096, 8192, null),
            rule: Moved(TheRule, "footprint.observedHours", 5, 1));

        Assert.Equal(VerdictKind.Unreadable, v.Kind);
        Assert.Contains("has not run the 24 hours", v.Reason);
    }

    // ---- the monitor's wire shape ----

    /// <summary>
    /// The history body parses as the monitor actually serves it, timestamps included.
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
        // Which is not the same as there being none. Project Zomboid sets -Xmx8g inside its own
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

    private sealed class StubHistory : IRuleHistory
    {
        public HistoryEvent? LastOccurrence(string eventType, string subject, DateTimeOffset notBefore) => null;

        public IReadOnlyList<OpenEpisode> OpenEpisodes(
            string opensWith, string closesWith, DateTimeOffset notBefore) => [];

        public (TimeSpan P95, int Samples) EpisodeDuration(
            string opensWith, string closesWith, string subject, DateTimeOffset notBefore) => (TimeSpan.Zero, 0);
    }
}
