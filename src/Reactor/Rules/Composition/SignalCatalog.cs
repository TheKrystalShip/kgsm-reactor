using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// Everything a rule can ask about the world.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three sources, and each fails differently.</b> The supervisor answers about what is running; the
/// monitor answers about what has been measured and is a leaf that may not be installed; the ledger
/// answers about what has been observed and is bounded by how long this daemon has been running.
/// Every one of them reaches a rule three-valued, so a source being absent produces "cannot tell"
/// rather than a condition quietly failing to hold.
/// </para>
/// <para>
/// ⚠ <b>Absent is a value here, and unreadable is not.</b> A blueprint that declares no minimum is a
/// measurement — the answer is "there is none". A blueprint that could not be read is a failure. The
/// four rules turn on that distinction in five separate places, and a catalog that collapsed it would
/// make every one of them refuse instances it was built to judge.
/// </para>
/// </remarks>
internal static class SignalCatalog
{
    // ---- the supervisor ----

    private const string SupervisorUnread = "the supervisor could not be read";

    private static async ValueTask<SignalReading> Supervisor(
        SignalRequest request, CancellationToken token, Func<InstanceRunState, SignalValue> read)
    {
        Reading<InstanceRunState> reading = await request.Scope.InstanceAsync(token).ConfigureAwait(false);

        return reading.State == ReadingState.Measured
            ? SignalReading.Of(read(reading.Value))
            : SignalReading.Unreadable($"{SupervisorUnread}: {reading.Reason ?? "no reason given"}");
    }

    // ---- the monitor ----

    private static async ValueTask<SignalReading> Footprint(
        SignalRequest request, CancellationToken token, Func<InstanceFootprint, SignalValue> read)
    {
        (InstanceFootprint? value, string? problem) =
            await request.Scope.FootprintAsync(token).ConfigureAwait(false);

        return value is { } footprint
            ? SignalReading.Of(read(footprint))
            : SignalReading.Unreadable(problem ?? "no footprint");
    }

    // ---- the blueprint and the launch line ----

    private static async ValueTask<SignalReading> Declaration(
        SignalRequest request, CancellationToken token, Func<MemoryDeclaration, SignalValue> read)
    {
        Reading<MemoryDeclaration> reading =
            await request.Scope.DeclarationAsync(token).ConfigureAwait(false);

        return reading.State == ReadingState.Measured
            ? SignalReading.Of(read(reading.Value))
            : SignalReading.Unreadable($"what {request.Subject} declares could not be read: {reading.Reason}");
    }

    /// <summary>
    /// How far a measured working set sits from what the blueprint declares, as a percentage.
    /// </summary>
    /// <remarks>
    /// Derived rather than composed, which is why signals are compiled. Two readings from two leaves
    /// combined into one figure is not something a clause language expresses without becoming an
    /// expression language, and the alternative — writing <c>abs(peak - declared) / declared</c> into
    /// a rule — is a sentence that parses and can mean something other than it reads.
    /// </remarks>
    private static async ValueTask<SignalReading> Drift(
        SignalRequest request, CancellationToken token, bool absolute)
    {
        (InstanceFootprint? found, string? problem) =
            await request.Scope.FootprintAsync(token).ConfigureAwait(false);

        if (found is not { } footprint)
            return SignalReading.Unreadable(problem ?? "no footprint");

        if (footprint.WorkingSetPeakMb is not { } observedMb)
            return SignalReading.Unreadable($"no working set has been measured for {request.Subject}");

        Reading<MemoryDeclaration> declared =
            await request.Scope.DeclarationAsync(token).ConfigureAwait(false);

        if (declared.State != ReadingState.Measured)
            return SignalReading.Unreadable(
                $"what {request.Subject} declares could not be read: {declared.Reason}");

        if (declared.Value.MinRamMb is not { } declaredMb || declaredMb == 0)
            return SignalReading.Unreadable(
                $"{request.Subject}'s blueprint declares no minimum to compare against");

        double pct = (observedMb - declaredMb) / (double)declaredMb * 100.0;
        return SignalReading.Of(SignalValue.OfNumber(absolute ? Math.Abs(pct) : pct));
    }

    private static async ValueTask<SignalReading> Trend(
        SignalRequest request, CancellationToken token, Func<MemoryTrend, SignalValue> read)
    {
        Reading<MemoryTrend> reading = await request.Scope.TrendAsync(token).ConfigureAwait(false);

        // The reader's own words, verbatim. A row that wants to say more says it in its own
        // unreadable message, where it can name the figures a decrement would have moved.
        return reading.State == ReadingState.Measured
            ? SignalReading.Of(read(reading.Value))
            : SignalReading.Unreadable(reading.Reason ?? "the working-set trend could not be read");
    }

    // ---- the ledger ----

    private static readonly IReadOnlyList<SignalArgument> Lookback =
    [
        new("eventType", "Event", ArgumentKind.EventType,
            Description: "Which event to look back for."),
        new("withinMinutes", "Within the last", ArgumentKind.Number, Default: "60",
            Description: "How far back to look, in minutes. Bounded by the ledger's own retention."),
    ];

    private static readonly IReadOnlyList<SignalArgument> EpisodeArguments =
    [
        new("opensWith", "Opens with", ArgumentKind.EventType,
            Description: "The event that starts an episode."),
        new("closesWith", "Closes with", ArgumentKind.EventType,
            Description: "The event that ends one."),
        new("withinDays", "Look back", ArgumentKind.Number, Default: "30",
            Description: "How far back to look, in days."),
    ];

    private static HistoryEvent? LastOccurrence(SignalRequest request)
    {
        double minutes = request.Arguments.Number("withinMinutes", 60);

        return request.Scope.History.LastOccurrence(
            request.Arguments.Text("eventType"),
            request.Subject,
            request.Now - TimeSpan.FromMinutes(minutes));
    }

    private static (IReadOnlyList<OpenEpisode> Open, DateTimeOffset NotBefore) Episodes(SignalRequest request)
    {
        DateTimeOffset notBefore = request.Now - TimeSpan.FromDays(request.Arguments.Number("withinDays", 30));

        return (request.Scope.History.OpenEpisodes(
            request.Arguments.Text("opensWith"), request.Arguments.Text("closesWith"), notBefore), notBefore);
    }

    private static OpenEpisode? OpenFor(SignalRequest request)
    {
        (IReadOnlyList<OpenEpisode> open, _) = Episodes(request);

        foreach (OpenEpisode episode in open)
        {
            if (string.Equals(episode.Subject, request.Subject, StringComparison.Ordinal))
                return episode;
        }

        return null;
    }

    public static IReadOnlyList<Signal> All { get; } =
    [
        // ---- what is being judged ----

        // The subject itself, so a rule can be written about one named server and decline everywhere
        // else. Scoping a rule is then an ordinary guard row that previews, reads in the editor and
        // states its own reason — where a separate "applies to" field beside the rows would be a second
        // place a rule can decline from, invisible to the preview that is supposed to explain it.
        new("subject.id", "Subject", SignalKind.Text,
            (r, _) => ValueTask.FromResult(SignalReading.Of(SignalValue.OfText(r.Subject))),
            Description: "What this evaluation is about — an instance name, a sensor reference, a "
                + "component. Comparing against it is how a rule is narrowed to one server."),

        // ---- what the supervisor says ----

        new("world.phase", "Supervisor phase", SignalKind.Text,
            (r, t) => Supervisor(r, t, s => SignalValue.OfText(s.Phase)),
            Description: "running, stopped, failed or unknown, as the supervisor sees it right now."),

        new("world.running", "Is running", SignalKind.Flag,
            (r, t) => Supervisor(r, t, s => SignalValue.OfFlag(s.Running)),
            Description: "Whether the supervisor reports it running at this instant, re-read rather "
                + "than taken from the event that woke the rule."),

        new("world.gaveUp", "Given up on", SignalKind.Flag,
            (r, t) => Supervisor(r, t, s => SignalValue.OfFlag(s.GaveUp)),
            Description: "The supervisor exhausted its retries and stopped trying. A latch, not a "
                + "moment: nothing on a timer leaves it, and only an operator start clears it."),

        new("world.restarts", "Consecutive failures", SignalKind.Number,
            (r, t) => Supervisor(r, t, s => SignalValue.OfNumber(s.Restarts)),
            Description: "How many times in a row the supervisor has restarted it and seen it fail."),

        // ---- what the monitor has measured ----

        new("footprint.spanDays", "Observed across", SignalKind.Number,
            (r, t) => Footprint(r, t, f => SignalValue.OfNumber(f.SpanDays)),
            Unit: "days",
            Description: "Calendar days between the first and last observation. Separate from the "
                + "hours below: a server played an hour an evening for a fortnight has little "
                + "measurement spread across a lot of calendar, and those are different evidence."),

        new("footprint.observedHours", "Measured for", SignalKind.Number,
            (r, t) => Footprint(r, t, f => SignalValue.OfNumber(f.ObservedHours)),
            Unit: "h",
            Description: "Cumulative hours the instance was observed running, summed across sessions — "
                + "five evenings of an hour count the same as one five-hour run."),

        new("footprint.runs", "Runs observed", SignalKind.Number,
            (r, t) => Footprint(r, t, f => SignalValue.OfNumber(f.Runs)),
            Description: "Times the instance was seen to start. Zero for one running since before this "
                + "host began measuring, which is not the same as one that has never run."),

        new("footprint.oomKills", "Killed for memory", SignalKind.Number,
            (r, t) => Footprint(r, t, f => SignalValue.OfNumber(f.OomKills)),
            Description: "Processes the kernel killed in this instance's cgroup for want of memory. "
                + "The one figure that bounds what an instance needs rather than describing what it used."),

        new("footprint.stallSeconds", "Stalled on memory", SignalKind.Number,
            (r, t) => Footprint(r, t, f => SignalValue.OfNumber(f.StallSeconds)),
            Unit: "s",
            Description: "Cumulative time every task in the cgroup spent stalled waiting on memory."),

        new("footprint.maxEvents", "Hit its ceiling", SignalKind.Number,
            (r, t) => Footprint(r, t, f => SignalValue.OfNumber(f.MaxEvents)),
            Description: "Times allocation reached the instance's memory ceiling without anything dying."),

        new("footprint.samples", "Observations", SignalKind.Number,
            (r, t) => Footprint(r, t, f => SignalValue.OfNumber(f.Samples)),
            Description: "How many measurements the figures above were computed from."),

        new("footprint.workingSetPeakMb", "Peak working set", SignalKind.Number,
            (r, t) => Footprint(r, t, f => f.WorkingSetPeakMb is { } mb
                ? SignalValue.OfNumber(mb)
                : SignalValue.None(SignalKind.Number)),
            Unit: "MB",
            Description: "The largest working set observed. Anonymous memory rather than the total, "
                + "which charges reclaimable page cache and grows to fill whatever it is given."),

        // ---- what it is declared to need ----

        new("declaration.minRamMb", "Declared minimum", SignalKind.Number,
            (r, t) => Declaration(r, t, d => d.MinRamMb is { } mb
                ? SignalValue.OfNumber(mb)
                : SignalValue.None(SignalKind.Number)),
            Unit: "MB",
            Description: "The blueprint's advisory minimum. Curated from vendor documentation, so it "
                + "describes a game where a footprint describes one world."),

        new("declaration.recommendedRamMb", "Declared recommendation", SignalKind.Number,
            (r, t) => Declaration(r, t, d => d.RecommendedRamMb is { } mb
                ? SignalValue.OfNumber(mb)
                : SignalValue.None(SignalKind.Number)),
            Unit: "MB",
            Description: "The blueprint's advisory recommendation."),

        new("declaration.heapFlag", "Heap argument", SignalKind.Text,
            (r, t) => Declaration(r, t, d => d.HeapFlag is { } flag
                ? SignalValue.OfText(flag)
                : SignalValue.None(SignalKind.Text)),
            Description: "The maximum-heap argument this instance launches with. Its presence makes "
                + "the footprint unusable as a requirement — a JVM told to hold four gigabytes will. "
                + "⚠ Absent means none was found on the launch line, never that none exists: a game "
                + "whose own start script sets it is invisible here."),

        // ---- the two compared ----

        new("drift.pctVsDeclared", "Drift from declared", SignalKind.Number,
            (r, t) => Drift(r, t, absolute: false),
            Unit: "%",
            Description: "How far the peak working set sits from the declared minimum. Positive when "
                + "the instance holds more than it was declared to need."),

        new("drift.absPctVsDeclared", "Drift from declared, either way", SignalKind.Number,
            (r, t) => Drift(r, t, absolute: true),
            Unit: "%",
            Description: "The same gap without its direction, for asking only whether two figures "
                + "have moved far enough apart to be worth mentioning."),

        new("trend.growthPct", "Working-set growth", SignalKind.Number,
            (r, t) => Trend(r, t, m => SignalValue.OfNumber(m.GrowthPct)),
            Unit: "%",
            Description: "How much the later half of the trend window sits above the earlier half. A "
                + "peak alone cannot say whether an instance has found its ceiling or is still climbing "
                + "toward one, and those are opposite decisions."),

        new("trend.points", "Trend samples", SignalKind.Number,
            (r, t) => Trend(r, t, m => SignalValue.OfNumber(m.Points)),
            Description: "How many samples the growth figure was computed from."),

        // ---- what has been observed ----

        new("history.lastOccurrence", "Last happened", SignalKind.Instant,
            (r, _) => ValueTask.FromResult(SignalReading.Of(
                LastOccurrence(r) is { } found
                    ? SignalValue.OfInstant(found.OccurredAt)
                    : SignalValue.None(SignalKind.Instant))),
            Arguments: Lookback,
            Description: "When an event last happened for this subject inside the window. ⚠ Absent is "
                + "\"not in the ledger\", which is not the same as \"did not happen\" — the ledger only "
                + "knows what this daemon has seen."),

        new("history.minutesSince", "Minutes since", SignalKind.Number,
            (r, _) => ValueTask.FromResult(SignalReading.Of(
                LastOccurrence(r) is { } found
                    ? SignalValue.OfNumber(Math.Floor((r.Now - found.OccurredAt).TotalMinutes))
                    : SignalValue.None(SignalKind.Number))),
            Unit: "m",
            Arguments: Lookback,
            Description: "How long ago that was."),

        new("episode.isOpen", "Episode is open", SignalKind.Flag,
            (r, _) => ValueTask.FromResult(SignalReading.Of(SignalValue.OfFlag(OpenFor(r) is not null))),
            Arguments: EpisodeArguments,
            Description: "Whether an opening event was seen for this subject with no closing event "
                + "after it. Read from the ledger, so one that began before this daemon started counts."),

        new("episode.openAge", "Open for", SignalKind.Duration,
            (r, _) => ValueTask.FromResult(SignalReading.Of(
                OpenFor(r) is { } episode
                    ? SignalValue.OfDuration(r.Now - episode.OpenedAt)
                    : SignalValue.None(SignalKind.Duration))),
            Arguments: EpisodeArguments,
            Description: "How long the open episode has been open, measured from when it actually "
                + "opened rather than from when this daemon noticed."),

        new("episode.durationP95", "Usually lasts up to", SignalKind.Duration,
            (r, _) => ValueTask.FromResult(SignalReading.Of(
                SignalValue.OfDuration(EpisodeStats(r).P95))),
            Arguments: EpisodeArguments,
            Description: "The p95 duration of episodes of this kind that already closed for this "
                + "subject. What \"unusually long\" means here rather than anywhere else."),

        new("episode.closedSamples", "Closed episodes on record", SignalKind.Number,
            (r, _) => ValueTask.FromResult(SignalReading.Of(
                SignalValue.OfNumber(EpisodeStats(r).Samples))),
            Arguments: EpisodeArguments,
            Description: "How many closed episodes the figure above was computed from. ⚠ A percentile "
                + "over three samples is not a distribution, so a rule comparing against one has to be "
                + "able to refuse rather than pretend."),
    ];

    private static (TimeSpan P95, int Samples) EpisodeStats(SignalRequest request) =>
        request.Scope.History.EpisodeDuration(
            request.Arguments.Text("opensWith"),
            request.Arguments.Text("closesWith"),
            request.Subject,
            request.Now - TimeSpan.FromDays(request.Arguments.Number("withinDays", 30)));

    public static Signal? ById(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}
