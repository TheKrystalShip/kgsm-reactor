using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.Kgsm.Reactor.Status;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// What a rule would decide about this host right now, without becoming one of its rules.
/// </summary>
/// <remarks>
/// ⚠ <b>The failure a preview exists to catch is a rule that reads plausibly and fires on nothing.</b>
/// A gate set where no instance clears it, a step ordered after one that always matches first — neither
/// is visible in an editor, and both are visible in the sentence a preview returns.
/// </remarks>
public class RulePreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

    private static Task<RulePreview> Preview(
        RuleDefinition rule, string? subject = null, IFootprintSource? footprint = null) =>
        RulePreview.RunAsync(
            rule, subject,
            new StubWorld(), new StubHistory(), footprint ?? new StubFootprints([]),
            Now, CancellationToken.None);

    private static RuleDefinition Drift =>
        ShippedRules.All.Single(r => r.Id =="memory_declaration_drift");

    private static RuleDefinition GiveUp =>
        ShippedRules.All.Single(r => r.Id =="give_up_backup");

    private static InstanceFootprint Measured(string instance, double peakMb) => new(
        Instance: instance,
        WorkingSetPeakBytes: peakMb * 1024 * 1024,
        WorkingSetAvgBytes: peakMb * 0.75 * 1024 * 1024,
        PeakBytes: peakMb * 1.1 * 1024 * 1024,
        OomKills: 0, MaxEvents: 0, StallSeconds: 0,
        Runs: 12, ObservedHours: 57, SpanDays: 25, Samples: 4000);

    /// <summary>
    /// ⚠ The sentence is the answer, not the verdict.
    /// </summary>
    /// <remarks>
    /// "Yes" tells somebody their rule fires; the figures tell them whether it fires for the reason they
    /// meant. A preview that returned only an outcome would be a rehearsal of the wrong thing.
    /// </remarks>
    [Fact]
    public async Task A_preview_returns_the_sentence_the_rule_would_record()
    {
        RulePreview preview = await Preview(
            Drift, footprint: new StubFootprints([Measured("romestead", 2908)]));

        PreviewVerdict verdict = Assert.Single(preview.Verdicts);

        Assert.Equal("romestead", verdict.Subject);
        Assert.Equal("holds", verdict.Outcome);
        Assert.Contains("peaks at 2908MB where its blueprint declares 4096MB", verdict.Reason);
    }

    /// <summary>
    /// ⚠ A verdict is spelled the way the catalog spells it.
    /// </summary>
    /// <remarks>
    /// A panel classifies an outcome against what <c>/catalog</c> offered it. An enum name lowercased is
    /// <c>doesnothold</c>, which matches none of them — a preview whose verdicts no surface can read.
    /// </remarks>
    [Fact]
    public async Task Every_outcome_is_spelled_the_way_the_catalog_spells_it()
    {
        HashSet<string> offered = [.. ReactorCatalog.Read().Outcomes.Select(o => o.Id)];

        RulePreview held = await Preview(
            Drift, footprint: new StubFootprints([Measured("romestead", 2908)]));
        RulePreview refused = await Preview(
            Drift, footprint: new StubFootprints([Measured("romestead", 4300)]));

        Assert.Contains(Assert.Single(held.Verdicts).Outcome, offered);
        Assert.Contains(Assert.Single(refused.Verdicts).Outcome, offered);
        Assert.Equal("doesNotHold", refused.Verdicts[0].Outcome);
    }

    [Fact]
    public async Task A_state_rule_finds_its_own_subjects()
    {
        RulePreview preview = await Preview(
            Drift,
            footprint: new StubFootprints([Measured("romestead", 2908), Measured("pz", 12269)]));

        Assert.Equal("enumerated", preview.SubjectsFrom);
        Assert.Equal("state", preview.Shape);
        Assert.Equal(["romestead", "pz"], preview.Verdicts.Select(v => v.Subject));
    }

    /// <summary>
    /// ⚠ An edge rule has no event here, so a preview of one is a preview against a chosen subject.
    /// </summary>
    /// <remarks>
    /// The answer says which it was, rather than letting a reader assume the rule found the subject on
    /// its own — which is the difference between "it would fire on Ketchup" and "it will find Ketchup".
    /// </remarks>
    [Fact]
    public async Task An_edge_rule_previews_against_a_subject_somebody_named()
    {
        RulePreview preview = await Preview(GiveUp, subject: "Ketchup");

        Assert.Equal("edge", preview.Shape);
        Assert.Equal("named", preview.SubjectsFrom);
        Assert.Equal("Ketchup", Assert.Single(preview.Verdicts).Subject);
    }

    [Fact]
    public async Task An_edge_rule_with_nobody_naming_a_subject_decides_nothing()
    {
        RulePreview preview = await Preview(GiveUp);

        Assert.Empty(preview.Verdicts);
        Assert.Equal("enumerated", preview.SubjectsFrom);
        Assert.Empty(preview.Problems);
    }

    /// <summary>
    /// ⚠ A rule that cannot run is reported as such, and nothing is evaluated.
    /// </summary>
    /// <remarks>
    /// The same validator the daemon runs at load, so a rule that previews clean is a rule that will
    /// load. A preview that quietly skipped validation would be a rehearsal that told somebody nothing
    /// about the thing they are about to save.
    /// </remarks>
    [Fact]
    public async Task A_rule_that_could_not_be_honoured_is_not_evaluated()
    {
        RuleDefinition broken = Drift with
        {
            Rows = [new([new Clause("footprint.spanDaze", ClauseOperator.LessThan,
                Comparand.Literal.Number(2))], VerdictKind.Unreadable, "too little")],
        };

        RulePreview preview = await Preview(
            broken, footprint: new StubFootprints([Measured("romestead", 2908)]));

        Assert.Contains(preview.Problems, p => p.Contains("footprint.spanDaze"));
        Assert.Empty(preview.Verdicts);
    }

    /// <summary>
    /// ⚠ A truncated preview says it was truncated.
    /// </summary>
    /// <remarks>
    /// One that looked complete would tell somebody their rule is quiet on a fleet it was never asked
    /// about — a false negative produced by the surface rather than by the rule.
    /// </remarks>
    [Fact]
    public async Task A_fleet_larger_than_one_answer_says_what_it_left_out()
    {
        IReadOnlyList<InstanceFootprint> fleet =
            [.. Enumerable.Range(0, RulePreview.MaxSubjects + 4).Select(i => Measured($"srv{i}", 2908))];

        RulePreview preview = await Preview(Drift, footprint: new StubFootprints(fleet));

        Assert.Equal(RulePreview.MaxSubjects, preview.Verdicts.Count);
        Assert.Equal(4, preview.NotEvaluated);
    }

    /// <summary>A rule whose sources cannot be read previews as "cannot tell", never as a refusal.</summary>
    [Fact]
    public async Task An_unreachable_source_previews_as_cannot_tell()
    {
        RulePreview preview = await Preview(Drift, subject: "romestead");

        Assert.Equal("unreadable", Assert.Single(preview.Verdicts).Outcome);
        Assert.Contains("no footprint is recorded", preview.Verdicts[0].Reason);
    }

    // ---- stubs ----

    private sealed class StubWorld : IWorldView
    {
        public ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<InstanceRunState>.Measured(new InstanceRunState("failed", true, 5)));

        public ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryDeclaration>.Measured(new MemoryDeclaration(4096, 8192, null)));

        public ValueTask<Reading<InstanceSupervision>> SupervisionAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<InstanceSupervision>.Measured(new InstanceSupervision(3)));
    }

    private sealed class StubFootprints(IReadOnlyList<InstanceFootprint> footprints) : IFootprintSource
    {
        public ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token) =>
            ValueTask.FromResult(Reading<IReadOnlyList<InstanceFootprint>>.Measured(footprints));

        public ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryTrend>.Measured(new MemoryTrend(600, 0.4)));
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
