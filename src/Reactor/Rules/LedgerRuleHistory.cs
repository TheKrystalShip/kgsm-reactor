using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// <see cref="IRuleHistory"/> over the observation ledger.
/// </summary>
/// <remarks>
/// Every question here is a query rather than a scan, which is the whole reason the ledger is a
/// database: "has this been open longer than usual" over a month of observations is not something an
/// append-only file answers without rebuilding an index on every boot.
/// </remarks>
internal sealed class LedgerRuleHistory(ObservationLedger ledger) : IRuleHistory
{
    public HistoryEvent? LastOccurrence(string eventType, string subject, DateTimeOffset notBefore)
    {
        List<HistoryEvent> rows = ledger.Query(
            """
            SELECT event_type, subject, occurred_at
            FROM observations
            WHERE event_type = $type AND subject = $subject AND occurred_at >= $since
            ORDER BY occurred_at DESC
            LIMIT 1;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$type", eventType);
                command.Parameters.AddWithValue("$subject", subject);
                command.Parameters.AddWithValue("$since", notBefore.ToUnixTimeMilliseconds());
            },
            reader => new HistoryEvent(
                reader.GetString(0), reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));

        return rows.Count > 0 ? rows[0] : null;
    }

    public IReadOnlyList<OpenEpisode> OpenEpisodes(
        string opensWith, string closesWith, DateTimeOffset notBefore)
    {
        // An episode is open when the subject's most recent opening is more recent than its most
        // recent closing — or when it has never closed at all. Computed per subject rather than
        // globally, because two subjects breaching the same metric are two episodes and collapsing
        // them would make one look like a repeat of the other.
        return ledger.Query(
            """
            SELECT o.subject, o.subject_kind, MAX(o.occurred_at) AS opened_at,
                   o.producer, o.segment, o.offset, o.event_id
            FROM observations o
            WHERE o.event_type = $opens AND o.occurred_at >= $since
            GROUP BY o.subject
            HAVING opened_at > COALESCE((
                SELECT MAX(c.occurred_at) FROM observations c
                WHERE c.event_type = $closes AND c.subject = o.subject AND c.occurred_at >= $since
            ), 0);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$opens", opensWith);
                command.Parameters.AddWithValue("$closes", closesWith);
                command.Parameters.AddWithValue("$since", notBefore.ToUnixTimeMilliseconds());
            },
            reader => new OpenEpisode(
                reader.GetString(0),
                ParseSubjectKind(reader.GetString(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                new EventSource(
                    reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6))));
    }

    public (TimeSpan P95, int Samples) EpisodeDuration(
        string opensWith, string closesWith, string subject, DateTimeOffset notBefore)
    {
        // Pair each opening with the first closing after it. Done in memory over one subject's rows
        // rather than in SQL: the set is small, and a correlated subquery per row would be both
        // slower and considerably harder to read six months from now.
        List<(string Type, long At)> rows = ledger.Query(
            """
            SELECT event_type, occurred_at
            FROM observations
            WHERE subject = $subject AND event_type IN ($opens, $closes) AND occurred_at >= $since
            ORDER BY occurred_at;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$subject", subject);
                command.Parameters.AddWithValue("$opens", opensWith);
                command.Parameters.AddWithValue("$closes", closesWith);
                command.Parameters.AddWithValue("$since", notBefore.ToUnixTimeMilliseconds());
            },
            reader => (reader.GetString(0), reader.GetInt64(1)));

        List<long> durations = [];
        long? openedAt = null;
        foreach ((string type, long at) in rows)
        {
            if (type == opensWith)
            {
                // A second opening before a close continues the same episode. The first is when the
                // condition began, which is what a duration has to be measured from.
                openedAt ??= at;
            }
            else if (openedAt is { } start)
            {
                durations.Add(at - start);
                openedAt = null;
            }
        }

        if (durations.Count == 0)
            return (TimeSpan.Zero, 0);

        durations.Sort();
        int rank = (int)Math.Ceiling(0.95 * durations.Count) - 1;
        return (TimeSpan.FromMilliseconds(durations[Math.Clamp(rank, 0, durations.Count - 1)]), durations.Count);
    }

    /// <summary>
    /// Read back a subject kind the ledger stored.
    /// </summary>
    /// <remarks>
    /// A value this build does not recognise reads as <see cref="SubjectKind.Unknown"/> rather than
    /// throwing: the row is real and its subject is real, and refusing the whole episode because one
    /// column has an unfamiliar spelling would lose a condition that is genuinely open.
    /// </remarks>
    private static SubjectKind ParseSubjectKind(string stored) =>
        Enum.TryParse(stored, ignoreCase: true, out SubjectKind kind) ? kind : SubjectKind.Unknown;

}
