using System.Collections.Concurrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Kgsm.Reactor.Ingest;

/// <summary>
/// Reads every producer's journal and records what it saw.
/// </summary>
/// <remarks>
/// <para>
/// <b>It decides nothing and it acts on nothing.</b> This is the observing half: what it produces is
/// the measurement the rule table is designed against — how often each event type fires, how long
/// between two of them on one server, and how long a condition takes to resolve itself. A number
/// chosen before that data exists is a guess wearing a default's clothing.
/// </para>
/// <para>
/// <b>Raw handling, not typed dispatch.</b> The handler takes every envelope, whether or not this
/// build has a type for it — a typed path would silently skip exactly the events a later rule might
/// be about, and the skip would look like an event that never happened.
/// </para>
/// <para>
/// <b>It reads at the tail and keeps no cursor.</b> That is the ecosystem's rule for a consumer that
/// acts, because a replayed action is performed again for real, and it is deliberate here even
/// though this build takes no action: the reactor exists to act, and a cursor added now would have to
/// be taken away later. What it costs is events that arrive while the process is down, which are
/// still in the journals that hold them — an observation is derived, and the journal is the record.
/// </para>
/// </remarks>
internal sealed class EventIngestService : BackgroundService
{
    /// <summary>
    /// The most observations held in memory before the oldest are dropped.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than an unbounded queue: if the ledger cannot be written, the alternative is
    /// a daemon that grows until the host kills it — on a box whose memory is reserved for game
    /// servers. Dropping is survivable precisely because these rows are derived; the drop is counted
    /// and reported, so a gap in the ledger is never silent.
    /// </remarks>
    private const int MaxBuffered = 20_000;

    /// <summary>How often expired observations are removed. Retention is measured in days, so this
    /// only has to be more often than that and less often than it costs anything.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    /// <summary>The lifecycle component id reported when the ledger cannot be written.</summary>
    private const string LedgerComponent = "ledger";

    private readonly IEventService _events;
    private readonly ObservationLedger _ledger;
    private readonly LeafLifecycle _lifecycle;
    private readonly ReactorOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<EventIngestService> _logger;

    private readonly ConcurrentQueue<Observation> _buffered = new();
    private int _bufferedCount;
    private long _dropped;
    private long _recorded;
    private bool _ledgerDegraded;

    public EventIngestService(
        IEventService events,
        ObservationLedger ledger,
        LeafLifecycle lifecycle,
        IOptions<ReactorOptions> options,
        TimeProvider clock,
        ILogger<EventIngestService> logger)
    {
        _events = events;
        _ledger = ledger;
        _lifecycle = lifecycle;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>How many observations have been committed since start. Read by tests.</summary>
    internal long Recorded => Interlocked.Read(ref _recorded);

    /// <summary>How many were dropped because the buffer was full. Read by tests.</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            // Ready, and honest about being deliberately quiet. A leaf that is off and a leaf that is
            // broken must not look the same from outside, which is the whole reason this reports
            // rather than simply returning.
            _logger.LogInformation(
                "Observation is off (Reactor__Enabled=false) — nothing is being recorded.");
            _lifecycle.MarkReady("observation disabled by configuration");
            return;
        }

        // Registered BEFORE Initialize, which is what starts the read loop. The other way round, the
        // events read between the two would reach no handler and be lost with no trace.
        _events.RegisterRawHandler(OnEventAsync);
        _events.Initialize();

        _logger.LogInformation(
            "Observing every producer's journal (engine at {JournalDir}); ledger {Ledger}, "
            + "committing every {Flush}s, keeping {Retention} days.",
            _options.JournalDir, _ledger.Path, _options.FlushIntervalSeconds, _options.RetentionDays);

        // Ready once the read loop is running and the ledger is open — the two things that have to be
        // true for this leaf to be doing its job. Reported from here rather than from the host's own
        // started signal, which fires before either.
        _lifecycle.MarkReady($"observing into {_ledger.Path}");

        var flush = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
        DateTimeOffset nextPrune = _clock.GetUtcNow().Add(PruneInterval);

        using var timer = new PeriodicTimer(flush, _clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;

                Flush();

                if (_clock.GetUtcNow() >= nextPrune)
                {
                    Prune();
                    nextPrune = _clock.GetUtcNow().Add(PruneInterval);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad pass must not end the loop: the reactor going quiet is worse than a missed
                // commit, because everything after it is silence that looks like an idle host.
                _logger.LogError(ex, "An observation pass failed; retrying on the next tick.");
            }
        }

        // What is still buffered is committed on the way out. A clean stop is the one case where the
        // last few seconds can be kept, and a redeploy is a clean stop.
        Flush();
    }

    /// <summary>
    /// Takes one envelope off the journal and buffers it.
    /// </summary>
    /// <remarks>
    /// Deliberately does no I/O. This runs on the journal read loop, and a handler that wrote to disk
    /// would put the ledger's latency in front of every consumer of that loop.
    /// </remarks>
    private Task OnEventAsync(EventWrapper wrapper, EventPosition position)
    {
        if (wrapper is null || string.IsNullOrWhiteSpace(wrapper.EventType))
            return Task.CompletedTask;

        // Null rather than "kgsm" when the transport did not say: a position with no producer has not
        // told us it was the engine, and filling that in would be a fabricated provenance in the one
        // column the ledger's identity is built on.
        string producer = string.IsNullOrWhiteSpace(position.Producer) ? "unknown" : position.Producer!;

        EventFacts facts = EventClassifier.Classify(wrapper.EventType, wrapper.Data, producer);

        if (Interlocked.Increment(ref _bufferedCount) > MaxBuffered)
        {
            Interlocked.Decrement(ref _bufferedCount);
            long dropped = Interlocked.Increment(ref _dropped);
            // At warning, and only on the round numbers: a full buffer means one thing is wrong, and
            // a line per dropped event would bury the reason underneath the symptom.
            if (dropped == 1 || dropped % 1000 == 0)
            {
                _logger.LogWarning(
                    "The observation buffer is full at {Max} — {Dropped} observation(s) dropped so far. "
                    + "The journals still hold every one of them; the ledger does not.",
                    MaxBuffered, dropped);
            }
            return Task.CompletedTask;
        }

        _buffered.Enqueue(new Observation(
            Producer: producer,
            Segment: position.Segment ?? string.Empty,
            Offset: position.Offset,
            EventId: position.EventId,
            // The current name, not the spelling the line carried: the ledger holds one vocabulary,
            // so a question asked in the name an event is called now reaches every row about it.
            EventType: LegacyEventNames.Canonical(wrapper.EventType),
            Class: facts.Class,
            SubjectKind: facts.SubjectKind,
            Subject: facts.Subject,
            Actor: wrapper.Actor,
            Origin: wrapper.Origin,
            // The producer's own timestamp, not ours: when it happened is the question every reading
            // in the population report is asked in terms of.
            OccurredAt: wrapper.Timestamp ?? _clock.GetUtcNow(),
            ObservedAt: _clock.GetUtcNow()));

        return Task.CompletedTask;
    }

    /// <summary>Commits everything buffered. Internal so a test can drive it against a fake clock.</summary>
    internal void Flush()
    {
        var batch = new List<Observation>();
        while (_buffered.TryDequeue(out Observation? row))
        {
            Interlocked.Decrement(ref _bufferedCount);
            batch.Add(row);
        }

        if (batch.Count == 0)
            return;

        try
        {
            int inserted = _ledger.Record(batch);
            Interlocked.Add(ref _recorded, inserted);

            if (_ledgerDegraded)
            {
                _ledgerDegraded = false;
                _lifecycle.MarkRecovered(LedgerComponent);
                _logger.LogInformation("The observation ledger is writable again.");
            }

            _logger.LogDebug(
                "Committed {Inserted} of {Batch} observation(s).", inserted, batch.Count);
        }
        catch (Exception ex)
        {
            // Reported as a degradation rather than swallowed. A reactor that cannot write its ledger
            // is still running and still reading, and will still say it is up — this is the one
            // signal that says what it has stopped being able to do.
            if (!_ledgerDegraded)
            {
                _ledgerDegraded = true;
                _lifecycle.MarkDegraded(
                    LedgerComponent,
                    $"the observation ledger at {_ledger.Path} cannot be written: {ex.Message}");
            }
            _logger.LogError(ex, "Could not commit {Count} observation(s).", batch.Count);
        }
    }

    private void Prune()
    {
        try
        {
            int removed = _ledger.Prune(TimeSpan.FromDays(_options.RetentionDays), _clock.GetUtcNow());
            if (removed > 0)
            {
                _logger.LogInformation(
                    "Pruned {Removed} observation(s) older than {Days} days.",
                    removed, _options.RetentionDays);
            }
        }
        catch (Exception ex)
        {
            // Pruning is housekeeping. Failing it costs disk, not correctness, so it must not be the
            // reason observation stops.
            _logger.LogWarning(ex, "Could not prune the observation ledger.");
        }
    }
}
