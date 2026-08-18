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
        string type = "instance_started",
        string subject = "factorio",
        DateTimeOffset? occurredAt = null) =>
        new(producer, segment, offset, type, EventClass.Lifecycle, SubjectKind.Instance, subject,
            Actor: "system:watchdog", Origin: "system",
            OccurredAt: occurredAt ?? Now, ObservedAt: occurredAt ?? Now);

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
            Row(offset: 0, type: "instance_player_joined", subject: "Ketchup"),
            Row(offset: 220, type: "instance_player_joined", subject: "Ketchup"),
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
            new Observation("kgsm", "s.ndjson", 0, "instance_started", EventClass.Lifecycle,
                SubjectKind.Instance, "factorio", Actor: null, Origin: null, Now, Now),
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
