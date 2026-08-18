using System.Text;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ingest;
using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// Reading journal history the reactor was not running for.
/// </summary>
/// <remarks>
/// The failure that matters most here is silent duplication. A row's identity is its position, so a
/// backfill that computed offsets differently from the live reader would insert a second copy of
/// every event the daemon had already seen — and every rate, burst and interval derived afterwards
/// would be overstated by exactly the overlap, with nothing to show it had happened.
/// </remarks>
public class JournalBackfillTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kgsm-reactor-backfill-").FullName;

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private string LedgerPath => Path.Combine(_root, "reactor.db");

    private ObservationLedger OpenLedger() => new(LedgerPath);

    /// <summary>Writes a journal segment and returns the byte offset of each line written.</summary>
    private List<long> WriteSegment(string producer, string segment, params string[] lines)
    {
        string dir = Path.Combine(_root, "state", producer, "events");
        Directory.CreateDirectory(dir);

        var offsets = new List<long>();
        using FileStream stream = File.Create(Path.Combine(dir, segment));

        foreach (string line in lines)
        {
            offsets.Add(stream.Position);
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        return offsets;
    }

    private static string Envelope(
        string type, string instance, string timestamp = "2026-08-18T10:00:00.000Z",
        string actor = "system:watchdog") =>
        $$"""{"V":1,"EventType":"{{type}}","Data":{"InstanceName":"{{instance}}"},"Timestamp":"{{timestamp}}","Actor":"{{actor}}","Origin":"system"}""";

    private static string Named(
        string type, string instance, string id, string timestamp = "2026-08-18T10:00:00.000Z") =>
        $$"""{"V":1,"Id":"{{id}}","EventType":"{{type}}","Data":{"InstanceName":"{{instance}}"},"Timestamp":"{{timestamp}}","Actor":"system:watchdog","Origin":"system"}""";

    private JournalBackfill.BackfillResult Run(int days = 3650, int retentionDays = 30)
    {
        using ObservationLedger ledger = OpenLedger();
        IReadOnlyList<string> dirs = JournalBackfill.Discover(
            Path.Combine(_root, "state"), engineJournalDir: string.Empty);

        return JournalBackfill.Run(ledger, dirs, Now.AddDays(-days), retentionDays, Now);
    }

    private List<(string Producer, string Segment, long Offset, string Type)> Rows()
    {
        using ObservationLedger ledger = OpenLedger();
        return ledger.Query(
            "SELECT producer, segment, offset, event_type FROM observations ORDER BY producer, offset;",
            _ => { },
            reader => (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
    }

    [Fact]
    public void An_offset_is_the_byte_the_line_starts_at()
    {
        // The whole contract with the live reader. Asserted against the offsets the writer actually
        // used, so this fails if either side of the convention moves.
        List<long> written = WriteSegment("kgsm-watchdog", "2026-08-18.ndjson",
            Envelope("instance_crashed", "starbound"),
            Envelope("instance_started", "starbound"),
            Envelope("instance_ready", "starbound"));

        Run();

        Assert.Equal(written, [.. Rows().Select(r => r.Offset)]);
    }

    [Fact]
    public void A_multibyte_payload_does_not_drift_the_offsets_after_it()
    {
        // Counted in bytes, not characters. A server named in anything but ASCII would otherwise shift
        // every line after it, and the duplicates would appear only on hosts that had one.
        List<long> written = WriteSegment("kgsm-watchdog", "2026-08-18.ndjson",
            Envelope("instance_crashed", "Ketchup—naïve"),
            Envelope("instance_started", "starbound"));

        Run();

        Assert.Equal(written, [.. Rows().Select(r => r.Offset)]);
    }

    [Fact]
    public void Reading_the_same_history_twice_adds_nothing()
    {
        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson",
            Envelope("instance_crashed", "starbound"),
            Envelope("instance_started", "starbound"));

        JournalBackfill.BackfillResult first = Run();
        JournalBackfill.BackfillResult second = Run();

        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, first.Skipped);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, Rows().Count);
    }

    [Fact]
    public void The_producer_comes_from_the_directory_not_the_payload()
    {
        // The conformance contract's rule: a producer named inside a line is a claim the reader cannot
        // check. The directory is the one answer that cannot disagree with where the line was found.
        WriteSegment("kgsm-monitor", "2026-08-17.ndjson", Envelope("instance_crashed", "starbound"));

        Run();

        Assert.Equal("kgsm-monitor", Assert.Single(Rows()).Producer);
    }

    [Fact]
    public void An_event_older_than_the_window_is_not_read()
    {
        WriteSegment("kgsm-watchdog", "2026-07-01.ndjson",
            Envelope("instance_crashed", "starbound", timestamp: "2026-07-01T10:00:00.000Z"));
        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson",
            Envelope("instance_started", "starbound", timestamp: "2026-08-18T10:00:00.000Z"));

        JournalBackfill.BackfillResult result = Run(days: 7);

        Assert.Equal(1, result.Inserted);
        Assert.Equal("instance_started", Assert.Single(Rows()).Type);
    }

    [Fact]
    public void Rows_the_next_prune_would_remove_are_counted_and_reported()
    {
        // A backfill whose result quietly disappears overnight is worse than one that refused, because
        // the report read in the morning describes a window that has already closed.
        WriteSegment("kgsm-watchdog", "2026-06-01.ndjson",
            Envelope("instance_crashed", "starbound", timestamp: "2026-06-01T10:00:00.000Z"));

        JournalBackfill.BackfillResult result = Run(retentionDays: 30);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.BeyondRetention);
    }

    [Fact]
    public void A_line_that_is_not_an_envelope_is_skipped_rather_than_ending_the_segment()
    {
        // A journal is append-only text written by several producers. Stopping on one bad line would
        // lose every good line after it, which is the opposite of what a backfill is for.
        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson",
            Envelope("instance_crashed", "starbound"),
            "{ this is not json",
            """{"V":1,"Timestamp":"2026-08-18T10:00:00.000Z"}""",
            Envelope("instance_started", "starbound"));

        JournalBackfill.BackfillResult result = Run();

        Assert.Equal(2, result.Inserted);
        Assert.Equal(2, result.Unreadable);
    }

    [Fact]
    public void A_final_line_with_no_newline_is_left_for_the_writer_to_finish()
    {
        // A segment being appended to right now. Half a line read here would be recorded at the
        // position the complete line is about to occupy, and the complete one could never be inserted.
        string dir = Path.Combine(_root, "state", "kgsm-watchdog", "events");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "2026-08-18.ndjson"),
            Envelope("instance_crashed", "starbound") + "\n" + """{"V":1,"EventType":"instance_st""");

        Assert.Equal(1, Run().Inserted);
    }

    [Fact]
    public void The_classification_is_the_daemons_own()
    {
        // Not a second copy of it. Two readers of one journal that classify differently disagree
        // invisibly — both look like they read the host correctly.
        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson", Envelope("instance_crashed", "starbound"));

        Run();

        using ObservationLedger ledger = OpenLedger();
        (string cls, string kind, string subject) = Assert.Single(ledger.Query(
            "SELECT class, subject_kind, subject FROM observations;",
            _ => { },
            reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2))));

        Assert.Equal(nameof(EventClass.Fault), cls);
        Assert.Equal(nameof(SubjectKind.Instance), kind);
        Assert.Equal("starbound", subject);
    }

    [Fact]
    public void Backfilling_decides_nothing()
    {
        // The line this mode must never cross. An observation restates a line that exists; a decision
        // is a judgment made against a world that answered at the time, and that world is gone.
        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson",
            Envelope("instance_failed", "starbound"),
            Envelope("instance_crashed", "starbound"));

        Run();

        using ObservationLedger ledger = OpenLedger();
        new DecisionStore(ledger).Initialize();

        Assert.Empty(new DecisionStore(ledger).Since(DateTimeOffset.MinValue));
    }

    [Fact]
    public void Discovery_finds_every_producer_and_the_engine()
    {
        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson", Envelope("instance_crashed", "a"));
        WriteSegment("kgsm-monitor", "2026-08-18.ndjson", Envelope("instance_crashed", "b"));
        string engine = Path.Combine(_root, "engine-events");
        Directory.CreateDirectory(engine);

        IReadOnlyList<string> found = JournalBackfill.Discover(Path.Combine(_root, "state"), engine);

        Assert.Equal(3, found.Count);
        Assert.Contains(engine, found, StringComparer.Ordinal);
    }

    [Fact]
    public void The_engine_directory_is_not_read_twice_when_discovery_also_finds_it()
    {
        // On a real host the engine's journal is BOTH named explicitly and sitting under the state
        // root. Listed twice, every engine event would be read twice — harmless for the ledger, which
        // ignores a known position, but it would double the line count the run reports and make a
        // person think the journals held twice what they do.
        WriteSegment("kgsm", "2026-08-18.ndjson", Envelope("instance_crashed", "a"));
        string engineDir = Path.Combine(_root, "state", "kgsm", "events");

        IReadOnlyList<string> found = JournalBackfill.Discover(Path.Combine(_root, "state"), engineDir);

        Assert.Single(found);
    }

    [Fact]
    public void A_backfilled_row_keeps_the_id_the_line_carries()
    {
        // History read late is still history: a line's name is on the line, so reading it in September
        // recovers the same id reading it live in August would have. This is what lets --verify check
        // rows the reactor was not running for — which is most of them on a host that has backfilled.
        const string id = "01a016e9-d535-7b03-8a6a-b26ae718064c";

        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson", Named("instance_crashed", "starbound", id));

        Run();

        Assert.Equal([id], Ids());
    }

    [Fact]
    public void A_backfilled_row_from_a_line_with_no_id_is_null_and_not_a_guess()
    {
        // Six weeks of this host's journals predate the field. Deriving something plausible — a hash of
        // the line, the position spelled as a uuid — would be indistinguishable from a real id
        // afterwards, and --verify would then compare a row against a name nobody minted.
        WriteSegment("kgsm-watchdog", "2026-08-18.ndjson", Envelope("instance_crashed", "starbound"));

        Run();

        Assert.Equal([null], Ids());
    }

    private List<string?> Ids()
    {
        using ObservationLedger ledger = OpenLedger();
        return ledger.Query(
            "SELECT event_id FROM observations ORDER BY producer, offset;",
            _ => { },
            reader => reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* the OS still holds it */ }
    }
}
