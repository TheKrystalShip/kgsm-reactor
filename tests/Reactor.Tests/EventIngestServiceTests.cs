using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Ingest;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The path from a journal line to a committed row.
/// </summary>
/// <remarks>
/// Driven through the real <see cref="IEventService"/> seam with a fake that hands envelopes to
/// whatever handler was registered, so what is exercised is the registration and the handler the
/// daemon actually uses — not a method called directly.
/// </remarks>
public class EventIngestServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-ingest-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An <see cref="IEventService"/> that only remembers who registered, and replays.</summary>
    private sealed class FakeEventService : IEventService
    {
        private readonly List<Func<EventWrapper, EventPosition, Task>> _raw = [];

        public bool Initialized { get; private set; }

        public void Initialize() => Initialized = true;

        public void Initialize(EventStartPosition startPosition) => Initialized = true;

        public void RegisterHandler<T>(Func<T, Task> handler) where T : KgsmEventDataBase { }

        public void RegisterRawHandler(Func<EventWrapper, EventPosition, Task> handler) => _raw.Add(handler);

        public void RegisterGapHandler(Func<EventJournalGap, Task> handler) { }

        /// <summary>Hand an envelope to everything registered, as the read loop would.</summary>
        public async Task EmitAsync(EventWrapper wrapper, EventPosition position)
        {
            foreach (Func<EventWrapper, EventPosition, Task> handler in _raw)
                await handler(wrapper, position);
        }

        /// <summary>Whether anything is listening at all.</summary>
        public bool HasRawHandler => _raw.Count > 0;

        // IEventService is disposable because the real one owns a read loop. This one owns nothing.
        public void Dispose() => GC.SuppressFinalize(this);

        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A journal writer that keeps what it was told, so lifecycle reporting can be asserted.</summary>
    private sealed class FakeJournalWriter : IEventJournalWriter
    {
        public List<string> Written { get; } = [];

        public string Producer => "kgsm-reactor";

        public ValueTask<bool> AppendAsync(
            EventName eventType, JsonElement data, string? actor = null, string? origin = null,
            EventSeverity? severity = null, EventOutcome? outcome = null, string? summary = null,
            CancellationToken token = default)
        {
            Written.Add(eventType.Value);
            return ValueTask.FromResult(true);
        }
    }

    private static EventWrapper Envelope(string type, string payload, DateTimeOffset? at = null) => new()
    {
        EventType = type,
        Data = JsonDocument.Parse(payload).RootElement,
        Timestamp = at ?? Now,
        Actor = "system:watchdog",
        Origin = "system",
    };

    private (EventIngestService Service, FakeEventService Events, ObservationLedger Ledger,
        FakeJournalWriter Journal) Build(bool enabled = true)
    {
        var events = new FakeEventService();
        var ledger = new ObservationLedger(_path);
        var journal = new FakeJournalWriter();
        var lifecycle = new LeafLifecycle(journal, NullLogger<LeafLifecycle>.Instance);

        var options = ReactorOptions.FromSettings(new ReactorSettings { Enabled = enabled });

        var service = new EventIngestService(
            events, ledger, lifecycle, Options.Create(options), TimeProvider.System,
            NullLogger<EventIngestService>.Instance);

        return (service, events, ledger, journal);
    }

    /// <summary>
    /// Runs the hosted service, waits until it is genuinely up, runs the body, then stops it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b><see cref="BackgroundService.StartAsync"/> returning does not mean
    /// <c>ExecuteAsync</c> has begun.</b> The host starts it without waiting, so a test that emits
    /// immediately after <c>StartAsync</c> can hand an envelope to a service that has not registered
    /// its handler yet — which passes on an idle machine and fails under a parallel run, the worst
    /// shape a test failure comes in. <paramref name="isUp"/> is the condition that says it is
    /// actually running; the body does not start until it holds.
    /// <para>
    /// Nothing about the daemon depends on this. Registration and <c>Initialize</c> are both inside
    /// <c>ExecuteAsync</c>, in that order, so no journal line can arrive before the handler exists
    /// however late the method starts.
    /// </para>
    /// </remarks>
    private static async Task StartAndStopAsync(
        EventIngestService service, Func<bool> isUp, Func<Task> whileRunning)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(isUp);
            await whileRunning();
        }
        finally
        {
            // The stop path commits what is buffered, which is the behaviour a redeploy relies on.
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Polls until a condition holds, or fails the test saying which one did not.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(5);
        }

        Assert.Fail("the ingest service did not start within 10s");
    }

    [Fact]
    public async Task An_observed_event_becomes_a_committed_row()
    {
        (EventIngestService service, FakeEventService events, ObservationLedger ledger, _) = Build();
        using (ledger)
        {
            await StartAndStopAsync(service, () => events.HasRawHandler, async () =>
            {
                Assert.True(events.Initialized);

                await events.EmitAsync(
                    Envelope("server.crash.exhausted", """{"InstanceName":"Ketchup","ExitCode":"137"}"""),
                    new EventPosition("kgsm-watchdog", "2026-08-18.ndjson", 512));
            });

            Assert.Equal(1, ledger.Count());
            Assert.Equal(1, service.Recorded);
        }
    }

    [Fact]
    public async Task Registration_happens_before_the_read_loop_starts()
    {
        // The other way round, everything read between Initialize and the registration reaches no
        // handler and is lost with no trace — and the loss would look like an idle host.
        (EventIngestService service, FakeEventService events, ObservationLedger ledger, _) = Build();
        using (ledger)
        {
            await StartAndStopAsync(service, () => events.HasRawHandler, () =>
            {
                Assert.True(events.Initialized);
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task An_event_with_no_producer_is_recorded_as_unknown_rather_than_attributed()
    {
        // A position that did not name a producer has not told us it was the engine. Filling that in
        // would be a fabricated provenance in the one column the row's identity is built on.
        (EventIngestService service, FakeEventService events, ObservationLedger ledger, _) = Build();
        using (ledger)
        {
            await StartAndStopAsync(service, () => events.HasRawHandler, async () =>
                await events.EmitAsync(
                    Envelope("server.started", """{"InstanceName":"factorio"}"""),
                    new EventPosition("seg.ndjson", 0)));

            List<string> producers = ledger.Query(
                "SELECT producer FROM observations;", _ => { }, reader => reader.GetString(0));

            Assert.Equal(["unknown"], producers);
        }
    }

    [Fact]
    public async Task The_producers_own_timestamp_is_what_is_recorded()
    {
        // Every reading in the population report is asked in terms of when a thing happened, not when
        // the reactor got round to reading it.
        DateTimeOffset happened = Now.AddHours(-3);

        (EventIngestService service, FakeEventService events, ObservationLedger ledger, _) = Build();
        using (ledger)
        {
            await StartAndStopAsync(service, () => events.HasRawHandler, async () =>
                await events.EmitAsync(
                    Envelope("server.started", """{"InstanceName":"factorio"}""", happened),
                    new EventPosition("kgsm", "seg.ndjson", 0)));

            List<long> occurred = ledger.Query(
                "SELECT occurred_at FROM observations;", _ => { }, reader => reader.GetInt64(0));

            Assert.Equal([happened.ToUnixTimeMilliseconds()], occurred);
        }
    }

    [Fact]
    public async Task Being_switched_off_still_reports_ready_and_records_nothing()
    {
        // A leaf that is deliberately quiet and one that is broken must not look the same from
        // outside — which is the whole reason this reports rather than simply returning.
        (EventIngestService service, FakeEventService events, ObservationLedger ledger,
            FakeJournalWriter journal) = Build(enabled: false);
        using (ledger)
        {
            await StartAndStopAsync(
                service, () => journal.Written.Contains("leaf.ready"), () => Task.CompletedTask);

            Assert.False(events.HasRawHandler);
            Assert.False(events.Initialized);
            Assert.Contains("leaf.ready", journal.Written);
            Assert.Equal(0, ledger.Count());
        }
    }

    [Fact]
    public async Task Coming_up_is_reported_once_ingestion_is_actually_running()
    {
        (EventIngestService service, _, ObservationLedger ledger, FakeJournalWriter journal) = Build();
        using (ledger)
        {
            await StartAndStopAsync(
                service, () => journal.Written.Contains("leaf.ready"), () => Task.CompletedTask);
            Assert.Contains("leaf.ready", journal.Written);
        }
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
