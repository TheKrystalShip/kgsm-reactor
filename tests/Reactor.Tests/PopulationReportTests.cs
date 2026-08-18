using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Reporting;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The three readings later decisions are derived from.
/// </summary>
/// <remarks>
/// Each test here is a way the report could be quietly wrong in the direction that matters: a burst
/// under-counted, a repeat interval measured across two different servers, or an episode counted as
/// resolved when it never was. A wrong number in this report becomes a wrong window in a rule.
/// </remarks>
public class PopulationReportTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-report-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private ObservationLedger Open() => new(_path);

    private static Observation At(
        DateTimeOffset when, string type, string subject, long offset,
        EventClass cls = EventClass.Lifecycle) =>
        new("kgsm", "seg.ndjson", offset, type, cls, SubjectKind.Instance, subject,
            Actor: null, Origin: null, OccurredAt: when, ObservedAt: when);

    [Fact]
    public void An_empty_window_says_so_without_pretending_the_host_was_idle()
    {
        using ObservationLedger ledger = Open();

        string report = PopulationReport.Render(ledger, days: 30, Now);

        Assert.Contains("No observations in the window", report);
        // The distinction that matters: nothing recorded is not the same as nothing happening.
        Assert.Contains("how long the unit", report);
    }

    [Fact]
    public void A_burst_that_straddles_a_minute_boundary_is_counted_whole()
    {
        // The reason the window slides rather than bucketing. Five events at :59.6, :59.8, :00.0,
        // :00.2, :00.4 are one burst of five; fixed minute buckets would report two of two and three.
        using ObservationLedger ledger = Open();
        DateTimeOffset boundary = Now.AddMinutes(-10);

        ledger.Record([
            At(boundary.AddMilliseconds(-400), "instance_player_joined", "Ketchup", 1),
            At(boundary.AddMilliseconds(-200), "instance_player_joined", "Ketchup", 2),
            At(boundary, "instance_player_joined", "Ketchup", 3),
            At(boundary.AddMilliseconds(200), "instance_player_joined", "Ketchup", 4),
            At(boundary.AddMilliseconds(400), "instance_player_joined", "Ketchup", 5),
        ]);

        string report = PopulationReport.Render(ledger, days: 30, Now);

        Assert.Contains("host-wide  : 5 event(s) in one minute", report);
    }

    [Fact]
    public void Repeat_intervals_are_measured_per_subject_not_across_the_fleet()
    {
        // Two servers each crashing once an hour is not one server crashing every half hour. Measured
        // across subjects, the interval halves and a suppression window derived from it would let
        // through exactly what it exists to hold back.
        using ObservationLedger ledger = Open();
        DateTimeOffset start = Now.AddHours(-4);

        ledger.Record([
            At(start, "instance_crashed", "alpha", 1, EventClass.Fault),
            At(start.AddMinutes(30), "instance_crashed", "beta", 2, EventClass.Fault),
            At(start.AddMinutes(60), "instance_crashed", "alpha", 3, EventClass.Fault),
            At(start.AddMinutes(90), "instance_crashed", "beta", 4, EventClass.Fault),
        ]);

        string report = PopulationReport.Render(ledger, days: 30, Now);

        // Two repeats (one per server), each an hour apart — not four events 30 minutes apart.
        Assert.Matches(@"\s+2\s+60\.0m\s+60\.0m\s+60\.0m\s+instance_crashed", report);
    }

    [Fact]
    public void An_episode_that_never_closed_is_reported_as_open_rather_than_resolved()
    {
        // The honest half of the settle-window reading. Counting an unclosed episode as instantly
        // resolved — or omitting it — would make every condition look like it fixes itself.
        using ObservationLedger ledger = Open();
        DateTimeOffset start = Now.AddHours(-2);

        ledger.Record([
            At(start, "instance_crashed", "alpha", 1, EventClass.Fault),
            At(start.AddSeconds(20), "instance_ready", "alpha", 2),
            At(start.AddMinutes(30), "instance_crashed", "beta", 3, EventClass.Fault),
        ]);

        string report = PopulationReport.Render(ledger, days: 30, Now);

        Assert.Contains("1 closed", report);
        Assert.Contains("1 never closed", report);
    }

    [Fact]
    public void A_second_opening_before_a_close_continues_the_episode()
    {
        // A server crashing three times before it comes back is one episode that took the whole span,
        // not three that took a third of it each. Measured the other way, a settle window derived
        // from it would be far too short.
        using ObservationLedger ledger = Open();
        DateTimeOffset start = Now.AddHours(-1);

        ledger.Record([
            At(start, "instance_crashed", "alpha", 1, EventClass.Fault),
            At(start.AddSeconds(10), "instance_crashed", "alpha", 2, EventClass.Fault),
            At(start.AddSeconds(20), "instance_crashed", "alpha", 3, EventClass.Fault),
            At(start.AddSeconds(60), "instance_ready", "alpha", 4),
        ]);

        string report = PopulationReport.Render(ledger, days: 30, Now);

        // One episode of sixty seconds, measured from the first crash.
        Assert.Contains("1 closed — min 60.0s", report);
    }

    [Fact]
    public void Observations_outside_the_window_are_not_read()
    {
        using ObservationLedger ledger = Open();

        ledger.Record([
            At(Now.AddDays(-40), "instance_started", "alpha", 1),
            At(Now.AddHours(-1), "instance_started", "alpha", 2),
        ]);

        string report = PopulationReport.Render(ledger, days: 7, Now);

        Assert.Contains("1 observation(s)", report);
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
