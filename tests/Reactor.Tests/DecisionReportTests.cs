using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Reporting;
using TheKrystalShip.Kgsm.Reactor.Rules;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The review the action modes are gated behind.
/// </summary>
/// <remarks>
/// Every failure guarded here is a report that reads as a clean bill of health. A silent rule left
/// unnamed, a busiest hour computed over the wrong decisions, or an absent measurement rendered as a
/// zero all produce a page somebody signs off — which is worse than no page, because the gate was
/// supposed to be the thing that caught it.
/// </remarks>
public class DecisionReportTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-review-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private ObservationLedger OpenLedger()
    {
        var ledger = new ObservationLedger(_path);
        new DecisionStore(ledger).Initialize();
        return ledger;
    }

    private static void Record(
        ObservationLedger ledger,
        string rule,
        string subject,
        DecisionOutcome outcome,
        DateTimeOffset at,
        string episode = "kgsm-watchdog:s.ndjson:1") =>
        new DecisionStore(ledger).Record(new Decision(
            Id: Decision.IdFor(rule, subject, episode + at.ToUnixTimeMilliseconds()),
            RuleId: rule,
            Subject: subject,
            SubjectKind: SubjectKind.Instance,
            EpisodeKey: episode + at.ToUnixTimeMilliseconds(),
            Severity: EventSeverity.Danger,
            Mode: RuleMode.Observe,
            Outcome: outcome,
            Reason: "because",
            RuleAuthor: null,
            Action: "take a pinned backup",
            ActionName: "create_backup",
            ActionInstance: subject,
            ActionState: ActionState.None,
            OpenedAt: at.AddMinutes(-1),
            DecidedAt: at,
            Source: new EventSource("kgsm-watchdog", "s.ndjson", 1, null)));

    private string Render(ObservationLedger ledger, int days = 7, params string[] liveRules) =>
        DecisionReport.Render(
            ledger, days, Now,
            liveRules.Length > 0 ? liveRules : [.. HandWrittenRules.All.Select(r => r.Id)]);

    [Fact]
    public void An_empty_window_reads_as_a_reading_rather_than_an_error()
    {
        using ObservationLedger ledger = OpenLedger();

        string report = Render(ledger);

        Assert.Contains("No decisions in the window", report, StringComparison.Ordinal);
        // And it must not let the reader conclude the host is quiet: a rule whose waking event never
        // arrives also decides nothing, and those are different facts.
        Assert.Contains("never arrived", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_that_decided_nothing_is_named()
    {
        // The failure that looks most like success. threshold_stuck is enabled, appears in the
        // descriptor and on the status socket, and would go on deciding nothing forever unnoticed.
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-2));

        string report = Render(ledger);

        Assert.Contains("Rules that decided nothing", report, StringComparison.Ordinal);
        Assert.Contains("threshold_stuck", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_that_spoke_is_not_listed_as_silent()
    {
        using ObservationLedger ledger = OpenLedger();
        foreach (Rule rule in HandWrittenRules.All)
            Record(ledger, rule.Id, "starbound", DecisionOutcome.Settled, Now.AddHours(-1));

        Assert.DoesNotContain("Rules that decided nothing", Render(ledger), StringComparison.Ordinal);
    }

    [Fact]
    public void The_busiest_hour_counts_only_what_fired()
    {
        // A ceiling bounds what the reactor DOES. An evaluation that settled cost the host nothing, so
        // counting it would inflate the figure a ceiling is set above and make the ceiling useless.
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "a", DecisionOutcome.Fired, Now.AddMinutes(-50));
        Record(ledger, "give_up_backup", "b", DecisionOutcome.Fired, Now.AddMinutes(-40));
        for (var i = 0; i < 20; i++)
            Record(ledger, "update_regression", $"s{i}", DecisionOutcome.Settled, Now.AddMinutes(-45));

        string report = Render(ledger);

        Assert.Contains("2  fired in total", report, StringComparison.Ordinal);
        Assert.Contains("2  in the busiest hour", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Fires_further_apart_than_an_hour_are_not_one_busy_hour()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "a", DecisionOutcome.Fired, Now.AddHours(-5));
        Record(ledger, "give_up_backup", "a", DecisionOutcome.Fired, Now.AddHours(-3));
        Record(ledger, "give_up_backup", "a", DecisionOutcome.Fired, Now.AddHours(-1));

        Assert.Contains("1  in the busiest hour", Render(ledger), StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_fire_yields_no_repeat_spacing_and_the_report_says_so()
    {
        // The measurement the suppression window is derived from. Rendering "0s" from one fire would
        // hand somebody a figure that looks measured and describes nothing.
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-2));

        string report = Render(ledger);

        Assert.Contains("no rule fired twice about the same subject", report, StringComparison.Ordinal);
        Assert.Contains("derived from nothing", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_fires_on_one_subject_report_the_gap_between_them()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-3));
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-1));

        string report = Render(ledger);

        Assert.Contains("give_up_backup on starbound  (2 fires)", report, StringComparison.Ordinal);
        Assert.Contains("2.0h", report, StringComparison.Ordinal);
    }

    [Fact]
    public void One_subjects_repeat_is_not_confused_with_anothers()
    {
        // Two servers each failing once is not one server failing twice, and a window derived from
        // the second reading would be derived from an event that never happened.
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-3));
        Record(ledger, "give_up_backup", "necesse", DecisionOutcome.Fired, Now.AddHours(-1));

        Assert.Contains(
            "no rule fired twice about the same subject", Render(ledger), StringComparison.Ordinal);
    }

    [Fact]
    public void The_outcome_mix_shows_what_did_not_fire()
    {
        // The reading the gate exists for: a rule suppressed four times in five is telling you its
        // window is wrong, and it can only say so if the non-firing outcomes are on the page.
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "a", DecisionOutcome.Fired, Now.AddHours(-5));
        for (var i = 0; i < 4; i++)
            Record(ledger, "give_up_backup", $"s{i}", DecisionOutcome.Suppressed, Now.AddHours(-4));

        string report = Render(ledger);

        Assert.Contains("80.0%  suppressed", report, StringComparison.Ordinal);
        Assert.Contains("20.0%  fired", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_decision_carries_the_line_it_was_derived_from()
    {
        // Invariant 1 as something a reviewer can act on: disagreeing with a verdict has to be a
        // lookup, not an archaeology.
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-1));

        Assert.Contains("from:  kgsm-watchdog:s.ndjson:1", Render(ledger), StringComparison.Ordinal);
    }

    [Fact]
    public void A_decision_outside_the_window_is_not_reviewed()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddDays(-9));

        Assert.Contains("No decisions in the window", Render(ledger), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (string file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { /* a temp file the OS still holds */ }
        }
    }
}
