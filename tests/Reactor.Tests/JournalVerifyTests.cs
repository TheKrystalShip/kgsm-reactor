using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ingest;
using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// Detecting a journal that has been rewritten under the ledger.
/// </summary>
/// <remarks>
/// A stored position is <c>(producer, segment, offset)</c>, which is right for as long as segments are
/// only appended to and deleted whole. Deleting one line shifts every byte after it, and the check
/// that matters is the one for the failure that <em>does not announce itself</em>: an offset that
/// still resolves to a real, parseable event which is simply not the one the row was written for.
/// </remarks>
public class JournalVerifyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kgsm-reactor-verify-").FullName;

    private string StateRoot => Path.Combine(_root, "state");

    private string JournalPath => Path.Combine(StateRoot, "kgsm-watchdog", "events", "2026-08-18.ndjson");

    private ObservationLedger OpenLedger() => new(Path.Combine(_root, "reactor.db"));

    private static string Envelope(string type, string instance) =>
        $$"""{"V":1,"EventType":"{{type}}","Data":{"InstanceName":"{{instance}}"},"Timestamp":"2026-08-18T10:00:00.000Z"}""";

    private void WriteJournal(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        // No explicit Encoding.UTF8 — that overload emits a BOM, and three bytes in front of the first
        // line make it unparseable. The real writers emit none.
        File.WriteAllText(JournalPath, string.Join("\n", lines) + "\n");
    }

    private void Fill()
    {
        using ObservationLedger ledger = OpenLedger();
        JournalBackfill.Run(
            ledger,
            JournalBackfill.Discover(StateRoot, engineJournalDir: string.Empty),
            DateTimeOffset.MinValue,
            retentionDays: 3650,
            now: DateTimeOffset.UtcNow);
    }

    private JournalVerify.VerifyResult Verify()
    {
        using ObservationLedger ledger = OpenLedger();
        return JournalVerify.Run(ledger, StateRoot, engineJournalDir: string.Empty);
    }

    [Fact]
    public void An_untouched_journal_verifies_clean()
    {
        WriteJournal(
            Envelope("instance_started", "prod"),
            Envelope("instance_ready", "prod"));
        Fill();

        JournalVerify.VerifyResult result = Verify();

        Assert.Equal(2, result.Checked);
        Assert.Equal(2, result.Intact);
        Assert.False(result.FoundDrift);
    }

    [Fact]
    public void Appending_to_a_journal_does_not_disturb_what_is_already_stored()
    {
        // The invariant the whole scheme rests on. If an append could drift a position, the ledger
        // would be wrong on every ordinary host rather than only on an edited one.
        WriteJournal(Envelope("instance_started", "prod"));
        Fill();

        File.AppendAllText(JournalPath, Envelope("instance_ready", "prod") + "\n");

        Assert.False(Verify().FoundDrift);
    }

    [Fact]
    public void Deleting_a_line_is_caught_including_the_silent_case()
    {
        // The failure this file exists for. The first surviving position resolves to a real event of
        // the wrong kind — no exception, no malformed line, nothing to notice.
        WriteJournal(
            Envelope("instance_started", "prod"),
            Envelope("instance_crashed", "TEST"),
            Envelope("instance_ready", "prod"),
            Envelope("instance_stopped", "prod"));
        Fill();

        string[] kept = [.. File.ReadAllLines(JournalPath).Where(l => !l.Contains("TEST", StringComparison.Ordinal))];
        File.WriteAllText(JournalPath, string.Join("\n", kept) + "\n");

        JournalVerify.VerifyResult result = Verify();

        Assert.True(result.FoundDrift);
        Assert.Equal(1, result.Intact);

        JournalVerify.Drift silent = Assert.Single(
            result.Drifted, d => d.State == JournalVerify.PositionState.WrongEvent);
        Assert.Equal("instance_crashed", silent.Expected);
        Assert.Equal("instance_ready", silent.Found);
    }

    [Fact]
    public void A_shortened_journal_reports_positions_past_its_end()
    {
        WriteJournal(
            Envelope("instance_started", "prod"),
            Envelope("instance_ready", "prod"),
            Envelope("instance_stopped", "prod"));
        Fill();

        File.WriteAllText(JournalPath, Envelope("instance_started", "prod") + "\n");

        JournalVerify.VerifyResult result = Verify();

        Assert.Equal(1, result.Intact);
        Assert.Equal(2, result.Drifted.Count(d => d.State == JournalVerify.PositionState.PastEnd));
    }

    [Fact]
    public void An_offset_that_no_longer_starts_a_line_is_caught()
    {
        // Editing a line's CONTENT rather than removing it — a redaction, say. Nothing is missing, so
        // a count would still agree; only the byte boundaries moved.
        WriteJournal(
            Envelope("instance_started", "prod"),
            Envelope("instance_ready", "prod"));
        Fill();

        WriteJournal(
            Envelope("instance_started", "a-much-longer-instance-name-than-before"),
            Envelope("instance_ready", "prod"));

        Assert.Contains(
            Verify().Drifted,
            d => d.State is JournalVerify.PositionState.MidLine or JournalVerify.PositionState.WrongEvent);
    }

    [Fact]
    public void A_pruned_segment_is_retention_rather_than_drift()
    {
        // Crying corruption on every host older than its retention window would make the check useless
        // exactly where it is most needed.
        WriteJournal(Envelope("instance_started", "prod"));
        Fill();

        File.Delete(JournalPath);

        JournalVerify.VerifyResult result = Verify();

        Assert.False(result.FoundDrift);
        Assert.Equal(1, result.SegmentsMissing);
    }

    [Fact]
    public void A_ledger_with_no_decisions_table_verifies_rather_than_throwing()
    {
        // The state a --backfill leaves on a host where the daemon has never run, which is exactly the
        // host worth checking. Verified because it threw the first time it was tried.
        WriteJournal(Envelope("instance_started", "prod"));
        Fill();

        using (ObservationLedger check = OpenLedger())
        {
            Assert.Empty(check.Query(
                "SELECT name FROM sqlite_master WHERE type='table' AND name='decisions';",
                _ => { },
                reader => reader.GetString(0)));
        }

        Assert.Equal(1, Verify().Intact);
    }

    [Fact]
    public void A_decisions_source_pointer_is_checked_too()
    {
        // The pointer published outside this leaf, and therefore the drift that reaches other people.
        WriteJournal(
            Envelope("instance_started", "prod"),
            Envelope("instance_failed", "prod"));
        Fill();

        using (ObservationLedger ledger = OpenLedger())
        {
            var store = new DecisionStore(ledger);
            store.Initialize();
            store.Record(new Decision(
                Id: "d1", RuleId: "give_up_backup", Subject: "prod",
                SubjectKind: SubjectKind.Instance, EpisodeKey: "e",
                Severity: Rules.Severity.Danger, Mode: Rules.RuleMode.Observe,
                Outcome: DecisionOutcome.Fired, Reason: "because",
                Action: "take a backup", ActionName: "create_backup", ActionInstance: "prod",
                ActionState: ActionState.None,
                OpenedAt: DateTimeOffset.UtcNow, DecidedAt: DateTimeOffset.UtcNow,
                // Deliberately past the end of a two-line segment.
                Source: new EventSource("kgsm-watchdog", "2026-08-18.ndjson", 99_999)));
        }

        Assert.Contains(
            Verify().Drifted,
            d => d.Expected == "decision source" && d.State == JournalVerify.PositionState.PastEnd);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* the OS still holds it */ }
    }
}
