using Microsoft.Data.Sqlite;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>The store itself: identity, idempotency and retention.</summary>
public class ObservationLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-test-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private ObservationLedger Open() => new(_path);

    private static Observation Row(
        string producer = "kgsm",
        string segment = "2026-08-18.ndjson",
        long offset = 0,
        string type = "server.started",
        string subject = "factorio",
        string? eventId = null,
        DateTimeOffset? occurredAt = null) =>
        new(producer, segment, offset, eventId, type, EventClass.Lifecycle, SubjectKind.Instance, subject,
            Actor: "system:watchdog", Origin: "system",
            OccurredAt: occurredAt ?? Now, ObservedAt: occurredAt ?? Now);

    /// <summary>
    /// Writes the schema as it stood before the line's own id was carried, with one row in it.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than by an older build, because the point of the test is what an existing
    /// FILE looks like — and a host carrying weeks of backfilled observations has exactly this one.
    /// </remarks>
    private void WriteSchemaWithoutTheIdColumn()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version=1;
            CREATE TABLE observations (
                producer     TEXT    NOT NULL,
                segment      TEXT    NOT NULL,
                offset       INTEGER NOT NULL,
                event_type   TEXT    NOT NULL,
                class        TEXT    NOT NULL,
                subject_kind TEXT    NOT NULL,
                subject      TEXT    NOT NULL,
                actor        TEXT,
                origin       TEXT,
                occurred_at  INTEGER NOT NULL,
                observed_at  INTEGER NOT NULL,
                PRIMARY KEY (producer, segment, offset)
            ) WITHOUT ROWID;
            INSERT INTO observations VALUES
                ('kgsm', 'old.ndjson', 0, 'server.started', 'Lifecycle', 'Instance', 'factorio',
                 NULL, NULL, 1755518400000, 1755518400000);
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_ledger_written_before_the_id_column_gains_it_and_keeps_its_rows()
    {
        // CREATE TABLE IF NOT EXISTS leaves an existing table exactly as it is, so without a
        // migration this host would be stamped schema 2 with no column — and the next insert would
        // throw. The rows are what makes rebuilding the wrong answer: every one is derived and could
        // be re-read, except observed_at, which is when the reactor SAW the line and cannot be
        // recovered once thrown away.
        WriteSchemaWithoutTheIdColumn();

        using ObservationLedger ledger = Open();

        Assert.Equal(1, ledger.Count());

        Assert.Contains(
            "event_id",
            ledger.Query("PRAGMA table_info(observations);", _ => { }, r => r.GetString(1)),
            StringComparer.Ordinal);

        // The pre-existing row reads as unknown rather than as anything invented for it.
        Assert.All(
            ledger.Query("SELECT event_id FROM observations;", _ => { }, r => r.IsDBNull(0)),
            Assert.True);

        // And the ledger works: a new row with an id goes in beside the old one.
        Assert.Equal(1, ledger.Record([Row(segment: "new.ndjson", eventId: "01a016e9-d535-7b03-8a6a-b26ae718064c")]));
        Assert.Equal(2, ledger.Count());
    }

    [Fact]
    public void The_migration_runs_once_and_a_second_open_is_a_no_op()
    {
        // ALTER TABLE ADD COLUMN throws on a column that is already there, so the guard is what makes
        // every restart after the first one work at all.
        WriteSchemaWithoutTheIdColumn();

        using (ObservationLedger first = Open())
            Assert.Equal(1, first.Count());

        using ObservationLedger second = Open();
        Assert.Equal(1, second.Count());
    }

    [Fact]
    public void An_id_is_stored_and_read_back_verbatim()
    {
        const string id = "01a016e9-d535-7b03-8a6a-b26ae718064c";

        using ObservationLedger ledger = Open();
        ledger.Record([Row(eventId: id)]);

        Assert.Equal(
            [id],
            ledger.Query("SELECT event_id FROM observations;", _ => { }, r => r.GetString(0)));
    }

    [Fact]
    public void A_line_with_no_id_is_stored_as_null_and_not_as_an_empty_string()
    {
        // The same absent-is-one-spelling rule the journals hold to. An empty string is a third state
        // that compares unequal to both, and --verify compares these.
        using ObservationLedger ledger = Open();
        ledger.Record([Row()]);

        Assert.All(
            ledger.Query("SELECT event_id FROM observations;", _ => { }, r => r.IsDBNull(0)),
            Assert.True);
    }

    [Fact]
    public void Recording_the_same_position_twice_is_a_no_op()
    {
        // A segment re-read must cost nothing. This is why the identity is the position rather than
        // the content — and it is what lets the reader be restarted without care.
        using ObservationLedger ledger = Open();

        Assert.Equal(1, ledger.Record([Row()]));
        Assert.Equal(0, ledger.Record([Row()]));
        Assert.Equal(1, ledger.Count());
    }

    [Fact]
    public void Two_identical_events_in_the_same_second_are_two_rows()
    {
        // The engine's own index derives its id from the content and a one-second timestamp, so it
        // collapses these into one. A rate measured from a ledger with that defect would under-report
        // exactly the bursts a ceiling has to be set above.
        using ObservationLedger ledger = Open();

        int inserted = ledger.Record([
            Row(offset: 0, type: "player.joined", subject: "Ketchup"),
            Row(offset: 220, type: "player.joined", subject: "Ketchup"),
        ]);

        Assert.Equal(2, inserted);
        Assert.Equal(2, ledger.Count());
    }

    [Fact]
    public void Positions_are_distinguished_by_producer_and_segment_as_well_as_offset()
    {
        // Two producers write their own segments and both start at offset 0. Keyed on the offset
        // alone, the second producer's first event of every day would silently vanish.
        using ObservationLedger ledger = Open();

        int inserted = ledger.Record([
            Row(producer: "kgsm", offset: 0),
            Row(producer: "kgsm-watchdog", offset: 0),
            Row(producer: "kgsm", segment: "2026-08-19.ndjson", offset: 0),
        ]);

        Assert.Equal(3, inserted);
    }

    [Fact]
    public void An_empty_batch_touches_nothing()
    {
        using ObservationLedger ledger = Open();
        Assert.Equal(0, ledger.Record([]));
    }

    [Fact]
    public void Pruning_removes_what_is_older_than_the_retention_and_keeps_the_rest()
    {
        using ObservationLedger ledger = Open();

        ledger.Record([
            Row(offset: 1, occurredAt: Now.AddDays(-40)),
            Row(offset: 2, occurredAt: Now.AddDays(-31)),
            Row(offset: 3, occurredAt: Now.AddDays(-29)),
            Row(offset: 4, occurredAt: Now),
        ]);

        Assert.Equal(2, ledger.Prune(TimeSpan.FromDays(30), Now));
        Assert.Equal(2, ledger.Count());
    }

    [Fact]
    public void The_ledger_survives_being_closed_and_reopened()
    {
        using (ObservationLedger first = Open())
            first.Record([Row()]);

        using ObservationLedger second = Open();
        Assert.Equal(1, second.Count());

        // And the position is still the identity across the reopen, not just within one connection.
        Assert.Equal(0, second.Record([Row()]));
    }

    [Fact]
    public void A_null_actor_and_origin_round_trip_as_null_rather_than_as_text()
    {
        // "Never fabricated" reaches the ledger too: a producer that said nothing about who acted
        // must not read back as having said something.
        using ObservationLedger ledger = Open();

        ledger.Record([
            new Observation("kgsm", "s.ndjson", 0, null, "server.started", EventClass.Lifecycle,
                SubjectKind.Instance, "factorio", null, null, Now, Now),
        ]);

        List<(string? Actor, string? Origin)> read = ledger.Query(
            "SELECT actor, origin FROM observations;",
            _ => { },
            reader => (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));

        Assert.Single(read);
        Assert.Null(read[0].Actor);
        Assert.Null(read[0].Origin);
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
