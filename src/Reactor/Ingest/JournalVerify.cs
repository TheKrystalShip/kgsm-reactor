using System.Text;
using System.Text.Json;

using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Ingest;

/// <summary>
/// Checks that every stored journal position still names the event it was stored for.
/// </summary>
/// <remarks>
/// <para>
/// A row's identity is <c>(producer, segment, offset)</c>, which is exactly right while a segment is
/// only ever appended to and deleted whole — the rule the shared retention layer holds to and states
/// in its own remarks. <b>A segment that is rewritten breaks it silently.</b> Deleting one line shifts
/// every byte after it, and a stored position then resolves to a real, parseable line that is simply
/// not the one it was recorded for.
/// </para>
/// <para>
/// That silence is the whole reason this exists. Two of the three ways a position can go wrong
/// announce themselves on the next read — an offset past the end of the file, or one landing in the
/// middle of a line. The third does not: it returns a valid event of the wrong kind, and every reading
/// derived from it looks exactly as trustworthy as one derived from the truth.
/// </para>
/// <para>
/// ⚠ <b>Read-only. It repairs nothing.</b> The remedy for drift is to rebuild the ledger from the
/// journals, which is safe precisely because the ledger is derived — but rebuilding is a decision
/// about data somebody may be reading a report from, and this tool's job is to make the problem
/// visible rather than to act on it.
/// </para>
/// </remarks>
internal static class JournalVerify
{
    /// <summary>How a stored position stands against the journal it names.</summary>
    internal enum PositionState
    {
        /// <summary>The line at that offset is the event the row records.</summary>
        Intact,

        /// <summary>
        /// The offset resolves to a different event. <b>The dangerous one</b> — a valid line, silently
        /// not the one the row was written for.
        /// </summary>
        WrongEvent,

        /// <summary>The offset lands inside a line rather than at its start.</summary>
        MidLine,

        /// <summary>The offset is past the end of the segment.</summary>
        PastEnd,

        /// <summary>The segment is gone. Expected for anything retention has pruned.</summary>
        SegmentMissing,
    }

    /// <summary>One position that did not survive.</summary>
    /// <param name="Producer">Whose journal.</param>
    /// <param name="Segment">Which segment.</param>
    /// <param name="Offset">The stored offset.</param>
    /// <param name="Expected">The event the row records.</param>
    /// <param name="Found">What is actually there, or a note when nothing readable is.</param>
    /// <param name="State">How it failed.</param>
    internal readonly record struct Drift(
        string Producer, string Segment, long Offset, string Expected, string Found, PositionState State);

    /// <summary>What one verification pass found.</summary>
    /// <param name="Checked">Positions examined.</param>
    /// <param name="Intact">Positions still naming their event.</param>
    /// <param name="Drifted">Every position that did not, in the order found.</param>
    /// <param name="SegmentsMissing">
    /// Positions whose segment is gone. Counted apart from drift: a pruned segment is retention doing
    /// its job, and calling that corruption would cry wolf on every host older than its window.
    /// </param>
    internal readonly record struct VerifyResult(
        int Checked, int Intact, IReadOnlyList<Drift> Drifted, int SegmentsMissing)
    {
        /// <summary>Whether anything was found that a rewritten segment would explain.</summary>
        public bool FoundDrift => Drifted.Count > 0;
    }

    /// <summary>
    /// Check every stored observation, and every decision's source pointer, against the journals.
    /// </summary>
    /// <param name="ledger">The ledger to check. Not written to.</param>
    /// <param name="stateRoot">Where producer state directories live.</param>
    /// <param name="engineJournalDir">The engine's journal directory.</param>
    public static VerifyResult Run(ObservationLedger ledger, string stateRoot, string engineJournalDir)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        // Observations and decisions together: the decision's pointer is the one published outside this
        // leaf, so it is the one whose drift reaches other people's screens.
        //
        // ⚠ The decisions table may not exist. A ledger filled by --backfill on a host where the daemon
        // has never run has observations and nothing else, which is exactly the host this check is most
        // worth running on. Asked for rather than assumed, because creating it here would make a
        // read-only check write to the database it is inspecting.
        var sql = new StringBuilder("SELECT producer, segment, offset, event_type FROM observations");

        if (HasDecisions(ledger))
        {
            sql.Append(
                " UNION ALL SELECT src_producer, src_segment, src_offset, 'decision source' FROM decisions");
        }

        sql.Append(" ORDER BY 1, 2, 3;");

        List<(string Producer, string Segment, long Offset, string Expected)> positions = ledger.Query(
            sql.ToString(),
            _ => { },
            reader => (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));

        var drifted = new List<Drift>();
        var intact = 0;
        var missing = 0;

        string? openPath = null;
        byte[]? contents = null;

        foreach ((string producer, string segment, long offset, string expected) in positions)
        {
            string path = PathFor(producer, segment, stateRoot, engineJournalDir);

            // One read per segment: the list is ordered by (producer, segment), so a segment is opened
            // once however many positions point into it.
            if (!string.Equals(path, openPath, StringComparison.Ordinal))
            {
                openPath = path;
                contents = File.Exists(path) ? File.ReadAllBytes(path) : null;
            }

            if (contents is null)
            {
                missing++;
                continue;
            }

            (PositionState state, string found) = Inspect(contents, offset, expected);

            if (state == PositionState.Intact)
                intact++;
            else
                drifted.Add(new Drift(producer, segment, offset, expected, found, state));
        }

        return new VerifyResult(positions.Count, intact, drifted, missing);
    }

    /// <summary>
    /// What is actually at <paramref name="offset"/>, and whether it is what was expected.
    /// </summary>
    /// <remarks>
    /// A decision's pointer records no event type — it names a position and nothing about the line —
    /// so for those the check is weaker by necessity: that the offset still starts a readable event,
    /// not that it starts the right one. <b>That weakness is the argument for an id in the envelope</b>,
    /// and it is stated here rather than hidden behind a passing check.
    /// </remarks>
    private static (PositionState State, string Found) Inspect(byte[] contents, long offset, string expected)
    {
        if (offset < 0 || offset >= contents.Length)
            return (PositionState.PastEnd, "past the end of the segment");

        // A position must name the FIRST byte of a line. Anything else means the bytes before it moved.
        if (offset > 0 && contents[offset - 1] != (byte)'\n')
            return (PositionState.MidLine, "lands inside a line, not at its start");

        int start = (int)offset;
        int end = Array.IndexOf(contents, (byte)'\n', start);
        if (end < 0)
            end = contents.Length;

        string line = Encoding.UTF8.GetString(contents, start, end - start);

        string? found;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            found = document.RootElement.TryGetProperty("EventType", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return (PositionState.MidLine, "does not parse as an event");
        }

        if (found is null)
            return (PositionState.MidLine, "parses, but names no event type");

        // A decision's source carries no event type to compare, so a readable line is all that can be
        // asserted. The row is intact as far as anything stored can tell.
        if (string.Equals(expected, "decision source", StringComparison.Ordinal))
            return (PositionState.Intact, found);

        return string.Equals(found, expected, StringComparison.Ordinal)
            ? (PositionState.Intact, found)
            : (PositionState.WrongEvent, found);
    }

    /// <summary>Whether this ledger has ever held decisions.</summary>
    private static bool HasDecisions(ObservationLedger ledger) =>
        ledger.Query(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='decisions';",
            _ => { },
            reader => reader.GetString(0)).Count > 0;

    private static string PathFor(
        string producer, string segment, string stateRoot, string engineJournalDir)
    {
        string directory = string.Equals(producer, "kgsm", StringComparison.Ordinal)
                           && !string.IsNullOrWhiteSpace(engineJournalDir)
            ? engineJournalDir
            : System.IO.Path.Combine(stateRoot, producer, "events");

        return System.IO.Path.Combine(directory, segment);
    }
}
