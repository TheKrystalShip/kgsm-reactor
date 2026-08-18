using System.Globalization;
using System.Text;
using System.Text.Json;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Ingest;

/// <summary>
/// Fills the observation ledger from journal history the reactor was not running for.
/// </summary>
/// <remarks>
/// <para>
/// The reactor tails, so it knows only what has happened since it started. The journals are older
/// than it is — the engine's go back weeks — and every one of those lines is a fact about this host
/// that the population report's readings are supposed to be derived from. This reads them.
/// </para>
/// <para>
/// ⚠ <b>It fills observations and nothing else. No rule is evaluated and no event is written.</b>
/// That is not a limitation to be lifted later. An observation is a restatement of a line that
/// exists, so reading it late changes nothing about it; a <em>decision</em> is a judgment made at a
/// moment against a world that answered — and the rules ask the live world, which today would answer
/// about today. Re-deriving old decisions would produce a record of judgments that were never made,
/// on evidence that no longer exists, and it would be indistinguishable afterwards from the real
/// ones.
/// </para>
/// <para>
/// Safe to run against a live ledger and safe to run twice: a row's identity is its position, and the
/// insert ignores a position already held. It is also why this can be re-run as more history accrues
/// without anybody tracking what was covered last time.
/// </para>
/// </remarks>
internal static class JournalBackfill
{
    /// <summary>What one pass read and wrote.</summary>
    /// <param name="Files">Segments opened.</param>
    /// <param name="Lines">Lines read.</param>
    /// <param name="Inserted">Observations new to the ledger.</param>
    /// <param name="Skipped">Lines already held, by position.</param>
    /// <param name="Unreadable">Lines that were not an event envelope.</param>
    /// <param name="Earliest">The oldest event read, or null if none.</param>
    /// <param name="BeyondRetention">
    /// How many of the inserted rows are older than the retention window and will be removed by the
    /// next prune. Reported rather than silently written, because a backfill whose result disappears
    /// overnight is worse than one that refused.
    /// </param>
    internal readonly record struct BackfillResult(
        int Files,
        int Lines,
        int Inserted,
        int Skipped,
        int Unreadable,
        DateTimeOffset? Earliest,
        int BeyondRetention);

    /// <summary>How many observations are committed per transaction.</summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Read every segment of every producer's journal into <paramref name="ledger"/>.
    /// </summary>
    /// <param name="ledger">The ledger to fill. Opened by the caller.</param>
    /// <param name="directories">Journal directories, one per producer.</param>
    /// <param name="notBefore">Ignore events older than this.</param>
    /// <param name="retentionDays">Used only to report what the next prune would remove.</param>
    /// <param name="now">Read as the observation time — see the remarks on <see cref="Observation"/>.</param>
    /// <param name="progress">Called once per segment, for a person watching a long run.</param>
    public static BackfillResult Run(
        ObservationLedger ledger,
        IReadOnlyList<string> directories,
        DateTimeOffset notBefore,
        int retentionDays,
        DateTimeOffset now,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(directories);

        var files = 0;
        var lines = 0;
        var inserted = 0;
        var skipped = 0;
        var unreadable = 0;
        var beyondRetention = 0;
        DateTimeOffset? earliest = null;
        DateTimeOffset retentionEdge = now.AddDays(-retentionDays);

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            // The producer is the state directory's name, exactly as the live reader derives it. Taken
            // from the path rather than from the payload for the same reason the conformance contract
            // gives: a producer named inside a line is a claim a reader cannot check.
            string producer = ProducerOf(directory);

            foreach (string path in Directory.EnumerateFiles(directory, "*.ndjson").Order(StringComparer.Ordinal))
            {
                files++;
                string segment = System.IO.Path.GetFileName(path);
                var batch = new List<Observation>(BatchSize);

                foreach ((long offset, string line) in ReadLines(path))
                {
                    lines++;

                    if (!TryRead(line, producer, offset, segment, now, out Observation? observation)
                        || observation is null)
                    {
                        unreadable++;
                        continue;
                    }

                    if (observation.OccurredAt < notBefore)
                        continue;

                    if (earliest is null || observation.OccurredAt < earliest)
                        earliest = observation.OccurredAt;

                    if (observation.OccurredAt < retentionEdge)
                        beyondRetention++;

                    batch.Add(observation);

                    if (batch.Count >= BatchSize)
                        Commit(ledger, batch, ref inserted, ref skipped);
                }

                Commit(ledger, batch, ref inserted, ref skipped);
                progress?.Invoke($"{producer}/{segment}");
            }
        }

        return new BackfillResult(files, lines, inserted, skipped, unreadable, earliest, beyondRetention);
    }

    /// <summary>
    /// Every producer's journal directory, discovered the way the federation discovers them.
    /// </summary>
    /// <param name="stateRoot">Where state directories live.</param>
    /// <param name="engineJournalDir">The engine's journal, which is named rather than discovered.</param>
    public static IReadOnlyList<string> Discover(string stateRoot, string engineJournalDir)
    {
        var found = new List<string>();

        if (!string.IsNullOrWhiteSpace(engineJournalDir) && Directory.Exists(engineJournalDir))
            found.Add(engineJournalDir);

        if (Directory.Exists(stateRoot))
        {
            foreach (string dir in Directory.EnumerateDirectories(stateRoot).Order(StringComparer.Ordinal))
            {
                string events = System.IO.Path.Combine(dir, "events");
                if (Directory.Exists(events) && !found.Contains(events, StringComparer.Ordinal))
                    found.Add(events);
            }
        }

        return found;
    }

    private static void Commit(
        ObservationLedger ledger, List<Observation> batch, ref int inserted, ref int skipped)
    {
        if (batch.Count == 0)
            return;

        int written = ledger.Record(batch);
        inserted += written;
        skipped += batch.Count - written;
        batch.Clear();
    }

    /// <summary>
    /// Each line with the byte offset it starts at.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The offset must be the start of the line</b>, which is what the live reader records and
    /// therefore what a row's identity is built from. Any other convention would make a backfilled row
    /// a different row from the live one covering the same event, and the ledger would hold both.
    /// Counted in bytes, not characters, because a multi-byte name in a payload would otherwise drift
    /// the offset of every line after it.
    /// </remarks>
    private static IEnumerable<(long Offset, string Line)> ReadLines(string path)
    {
        using FileStream stream = File.Open(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        long offset = 0;
        var buffer = new List<byte>(512);
        var read = new byte[64 * 1024];
        int count;

        while ((count = stream.Read(read, 0, read.Length)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (read[i] == (byte)'\n')
                {
                    long start = offset - buffer.Count;
                    string line = Encoding.UTF8.GetString(CollectionsMarshalAsSpan(buffer));
                    buffer.Clear();
                    offset++;

                    if (line.Length > 0)
                        yield return (start, line);

                    continue;
                }

                buffer.Add(read[i]);
                offset++;
            }
        }

        // A final line with no newline is a segment still being appended to, and it is deliberately
        // left alone: the writer may be midway through it, and half a line read now would be recorded
        // at a position the complete line will later occupy.
    }

    private static ReadOnlySpan<byte> CollectionsMarshalAsSpan(List<byte> list) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

    /// <summary>
    /// Turn one journal line into an observation, or refuse it.
    /// </summary>
    /// <remarks>
    /// Refusing is not an error path worth failing the run over. A journal is append-only text that
    /// several producers write; a line that will not parse is one line, and stopping the backfill over
    /// it would lose every line after it in the segment.
    /// </remarks>
    private static bool TryRead(
        string line,
        string producer,
        long offset,
        string segment,
        DateTimeOffset now,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Observation? observation)
    {
        observation = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("EventType", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string eventType = typeElement.GetString()!;
            if (eventType.Length == 0)
                return false;

            JsonElement data = root.TryGetProperty("Data", out JsonElement dataElement)
                ? dataElement.Clone()
                : default;

            EventFacts facts = EventClassifier.Classify(eventType, data, producer);

            observation = new Observation(
                Producer: producer,
                Segment: segment,
                Offset: offset,
                EventId: Text(root, "Id"),
                EventType: eventType,
                Class: facts.Class,
                SubjectKind: facts.SubjectKind,
                Subject: facts.Subject,
                Actor: Text(root, "Actor"),
                Origin: Text(root, "Origin"),
                OccurredAt: Instant(root, "Timestamp") ?? now,
                // When the reactor read it, which for a backfill is now — and the gap to OccurredAt is
                // the honest measure of how long it was not watching.
                ObservedAt: now);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static DateTimeOffset? Instant(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            element.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;

    /// <summary>The producer id a journal directory belongs to: <c>/var/lib/&lt;producer&gt;/events</c>.</summary>
    private static string ProducerOf(string eventsDirectory)
    {
        string? parent = System.IO.Path.GetDirectoryName(
            eventsDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));

        string name = parent is null ? string.Empty : System.IO.Path.GetFileName(parent);

        return name.Length > 0 ? name : "unknown";
    }
}
