using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Reporting;
using TheKrystalShip.Kgsm.Reactor.Rules;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The review as data — what the status socket serves and the terminal renders.
/// </summary>
/// <remarks>
/// <see cref="DecisionReportTests"/> asserts on the rendered text and therefore covers the readings
/// as a person reads them. What it cannot reach is the part only a caller with a limit exercises:
/// <b>capping the log must never cap the arithmetic</b>. A busiest hour measured over a truncated
/// sample under-reports exactly the peak a ceiling has to clear, and it would do so silently — the
/// payload would look complete and be wrong about the one number it exists to establish.
/// </remarks>
public class DecisionReviewTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-reviewdata-{Guid.NewGuid():N}.db");

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

    private static DecisionReview Read(
        ObservationLedger ledger, int days = 7, int limit = int.MaxValue, params string[] liveRules) =>
        DecisionReview.Read(
            ledger, days, Now, limit,
            liveRules.Length > 0 ? liveRules : [.. HandWrittenRules.All.Select(r => r.Id)]);

    [Fact]
    public void The_limit_caps_the_log_and_never_the_readings()
    {
        using ObservationLedger ledger = OpenLedger();

        // Five fires inside one hour. A limit of two must still report a busiest hour of five: the
        // ceiling is measured over the window, and the log is only what is shown of it.
        for (var i = 0; i < 5; i++)
            Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddMinutes(-i * 5));

        DecisionReview review = Read(ledger, limit: 2);

        Assert.Equal(2, review.Decisions.Count);
        Assert.Equal(5, review.Total);
        Assert.NotNull(review.Ceiling);
        Assert.Equal(5, review.Ceiling.Fired);
        Assert.Equal(5, review.Ceiling.PeakInHour);
        Assert.Equal(5, review.Rules.Single().Total);
    }

    [Fact]
    public void The_log_is_newest_first_so_a_capped_one_shows_the_most_recent()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "old", DecisionOutcome.Fired, Now.AddHours(-5));
        Record(ledger, "give_up_backup", "new", DecisionOutcome.Fired, Now.AddMinutes(-1));

        Assert.Equal("new", Read(ledger, limit: 1).Decisions.Single().Subject);
    }

    [Fact]
    public void Nothing_fired_leaves_the_ceiling_null_rather_than_a_peak_of_zero()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Settled, Now.AddMinutes(-1));

        // A zero peak would read as a measurement that came back empty. There is no pressure to
        // measure here and no basis for a ceiling either way, which is a different statement.
        DecisionReview review = Read(ledger);
        Assert.Null(review.Ceiling);
        Assert.Equal(1, review.Total);
    }

    [Fact]
    public void An_empty_window_names_every_rule_as_silent()
    {
        using ObservationLedger ledger = OpenLedger();

        DecisionReview review = Read(ledger);

        Assert.Equal(0, review.Total);
        Assert.Empty(review.Decisions);
        Assert.Equal(HandWrittenRules.All.Count, review.Silent.Count);
    }

    [Fact]
    public void A_rule_that_spoke_is_not_named_as_silent()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Settled, Now.AddMinutes(-1));

        Assert.DoesNotContain("give_up_backup", Read(ledger).Silent);
    }

    [Fact]
    public void The_outcome_mix_carries_counts_rather_than_shares()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "a", DecisionOutcome.Fired, Now.AddMinutes(-1));
        Record(ledger, "give_up_backup", "b", DecisionOutcome.Suppressed, Now.AddMinutes(-2));
        Record(ledger, "give_up_backup", "c", DecisionOutcome.Suppressed, Now.AddMinutes(-3));

        RuleOutcomes rule = Read(ledger).Rules.Single();

        Assert.Equal(3, rule.Total);
        // Commonest first, so a reader sees what the rule mostly does before what it rarely does.
        Assert.Equal("suppressed", rule.Outcomes[0].Outcome);
        Assert.Equal(2, rule.Outcomes[0].Count);
        Assert.Equal(1, rule.Outcomes[1].Count);
    }

    [Fact]
    public void A_single_fire_contributes_no_repeat_spacing()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddMinutes(-1));

        // A spacing derived from one fire is derived from nothing, and the suppression window is read
        // off exactly this list.
        Assert.Empty(Read(ledger).Repeats);
    }

    [Fact]
    public void Two_fires_on_one_subject_carry_the_gap_between_them()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-3));
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddHours(-1));

        RepeatSpacing spacing = Read(ledger).Repeats.Single();

        Assert.Equal("starbound", spacing.Subject);
        Assert.Equal(2, spacing.Fires);
        Assert.Equal((long)TimeSpan.FromHours(2).TotalMilliseconds, spacing.ShortestMs);
        Assert.Equal(spacing.ShortestMs, spacing.LongestMs);
    }

    [Fact]
    public void A_decision_outside_the_window_is_not_read()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddDays(-9));

        Assert.Equal(0, Read(ledger).Total);
    }

    [Fact]
    public void Every_row_carries_the_journal_line_it_was_derived_from()
    {
        using ObservationLedger ledger = OpenLedger();
        Record(ledger, "give_up_backup", "starbound", DecisionOutcome.Fired, Now.AddMinutes(-1));

        // Invariant 1 as a field: a reviewer disagreeing with a verdict has to be able to go and read
        // what it was made from.
        DecisionRow row = Read(ledger).Decisions.Single();
        Assert.Equal("kgsm-watchdog:s.ndjson:1", row.Source);
        Assert.Equal("observe", row.Mode, ignoreCase: true);
        Assert.Equal("create_backup", row.ActionName);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        File.Delete(_path);
    }
}
