using System.Globalization;
using System.Text;

using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Reporting;

/// <summary>
/// What the host actually does, measured from the ledger.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the point of the observing phase.</b> A rule table invented at a desk is a list of
/// things that sound sensible; the one that works is derived from what this host actually does. Three
/// later decisions read this report, and it carries exactly the three readings they need:
/// </para>
/// <list type="number">
/// <item><description><b>Rate and burst shape per type</b> — what a host-wide action ceiling has to
/// tolerate without tripping on a legitimate evening.</description></item>
/// <item><description><b>Inter-arrival time of repeats on one subject</b> — what a suppression window
/// has to span for "this again" to stop being news.</description></item>
/// <item><description><b>How long a condition takes to resolve itself</b> — what a settle window has
/// to wait before speaking is worth anything, and which conditions can be expressed as state a rule
/// re-derives rather than an edge it has to catch.</description></item>
/// </list>
/// <para>
/// ⚠ <b>It reports, it does not recommend.</b> No threshold, window or ceiling is suggested here. The
/// numbers are the input to the seven questions each candidate rule has to answer, and a report that
/// proposed values would be answering them on the strength of arithmetic alone.
/// </para>
/// </remarks>
internal static class PopulationReport
{
    /// <summary>The window bursts are counted in. One minute is the span an action ceiling is
    /// naturally stated over, and short enough that a real burst is still one burst.</summary>
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The condition pairs whose duration is worth knowing, as an opening event and what closes it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Candidates, not a rule table.</b> Each is a question — "how long does this take to fix
    /// itself here" — and the answers are what a settle window would be derived from IF the condition
    /// survives the seven questions. Nothing gates on this list.
    /// </remarks>
    private static readonly (string Opens, string Closes, string Question)[] ConditionPairs =
    [
        ("server.crashed", "server.ready",
            "a crash, until the server is playable again"),
        ("server.crashed", "server.started",
            "a crash, until the process is back"),
        ("server.crash.exhausted", "server.started",
            "a give-up, until anything starts it again"),
        ("host.threshold.breached", "host.threshold.cleared",
            "a threshold episode, until it clears"),
        ("leaf.degraded", "leaf.recovered",
            "a component degrading, until it recovers"),
        ("server.stop.started", "server.stop.finished",
            "a stop, until it completes"),
        ("server.update.started", "server.update.finished",
            "an update, until it completes"),
    ];

    /// <summary>One event as the report reads it.</summary>
    private readonly record struct Row(string EventType, string Class, string Subject, long OccurredAt);

    /// <summary>Renders the whole report for the last <paramref name="days"/> days.</summary>
    public static string Render(ObservationLedger ledger, int days, DateTimeOffset now)
    {
        long since = now.AddDays(-days).ToUnixTimeMilliseconds();

        List<Row> rows = ledger.Query(
            """
            SELECT event_type, class, subject, occurred_at
            FROM observations
            WHERE occurred_at >= $since
            ORDER BY occurred_at;
            """,
            command => command.Parameters.AddWithValue("$since", since),
            reader => new Row(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));

        var report = new StringBuilder();
        report.AppendLine($"kgsm-reactor — event population over the last {days} day(s)");
        report.AppendLine($"read at {now:yyyy-MM-dd HH:mm:ss} UTC from {ledger.Path}");
        report.AppendLine();

        if (rows.Count == 0)
        {
            report.AppendLine("No observations in the window.");
            report.AppendLine();
            report.AppendLine("That is a reading, not an error: either the reactor has not been running");
            report.AppendLine("for long, or nothing has happened on this host. Check how long the unit");
            report.AppendLine("has been up before concluding the second.");
            return report.ToString();
        }

        report.AppendLine($"{rows.Count} observation(s), "
            + $"{FormatInstant(rows[0].OccurredAt)} → {FormatInstant(rows[^1].OccurredAt)}");
        report.AppendLine();

        AppendRates(report, rows, days);
        AppendBursts(report, rows);
        AppendInterArrivals(report, rows);
        AppendConditions(report, rows);

        return report.ToString();
    }

    /// <summary>Reading 1a — what fires, and how much of it there is.</summary>
    private static void AppendRates(StringBuilder report, List<Row> rows, int days)
    {
        report.AppendLine("── Rate by event type ──────────────────────────────────────────────");
        report.AppendLine("What fires here at all, and how often. A type absent from this list is one");
        report.AppendLine("no rule can be built on, whatever the catalog says it could emit.");
        report.AppendLine();
        report.AppendLine($"{"count",8}  {"per day",9}  {"class",-13}  event type");

        IEnumerable<IGrouping<string, Row>> byType = rows
            .GroupBy(r => r.EventType)
            .OrderByDescending(g => g.Count());

        foreach (IGrouping<string, Row> group in byType)
        {
            double perDay = days > 0 ? group.Count() / (double)days : group.Count();
            report.AppendLine(
                $"{group.Count(),8}  {perDay,9:F1}  {group.First().Class,-13}  {group.Key}");
        }
        report.AppendLine();
    }

    /// <summary>Reading 1b — the shape a ceiling has to tolerate.</summary>
    private static void AppendBursts(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("── Burst shape ─────────────────────────────────────────────────────");
        report.AppendLine("The most events seen in any single minute. A host-wide action ceiling set");
        report.AppendLine("below the busiest legitimate minute would fire on an ordinary evening.");
        report.AppendLine();

        (int peak, long at) = PeakInWindow(rows.Select(r => r.OccurredAt).ToList());
        report.AppendLine($"  host-wide  : {peak} event(s) in one minute, at {FormatInstant(at)}");

        var perType = rows
            .GroupBy(r => r.EventType)
            .Select(g => (Type: g.Key, Peak: PeakInWindow(g.Select(r => r.OccurredAt).ToList())))
            .Where(x => x.Peak.Peak > 1)
            .OrderByDescending(x => x.Peak.Peak)
            .Take(10)
            .ToList();

        if (perType.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("  busiest single minute, per type:");
            foreach ((string type, (int typePeak, long typeAt)) in perType)
                report.AppendLine($"    {typePeak,5}  {type,-38}  {FormatInstant(typeAt)}");
        }
        report.AppendLine();
    }

    /// <summary>Reading 2 — how long between two of the same thing on one subject.</summary>
    private static void AppendInterArrivals(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("── Repeat interval, per (type, subject) ────────────────────────────");
        report.AppendLine("How long between one event and the next of the same type on the same");
        report.AppendLine("subject. A suppression window shorter than the p50 suppresses nothing;");
        report.AppendLine("one longer than the p95 hides the second occurrence of almost everything.");
        report.AppendLine();
        report.AppendLine($"{"repeats",8}  {"min",10}  {"p50",10}  {"p95",10}  event type");

        var gapsByType = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        foreach (IGrouping<(string, string), Row> pair in rows
                     .Where(r => r.Subject.Length > 0)
                     .GroupBy(r => (r.EventType, r.Subject)))
        {
            List<long> times = pair.Select(r => r.OccurredAt).OrderBy(t => t).ToList();
            for (int i = 1; i < times.Count; i++)
            {
                if (!gapsByType.TryGetValue(pair.Key.Item1, out List<long>? gaps))
                    gapsByType[pair.Key.Item1] = gaps = [];
                gaps.Add(times[i] - times[i - 1]);
            }
        }

        foreach ((string type, List<long> gaps) in gapsByType.OrderByDescending(kv => kv.Value.Count))
        {
            gaps.Sort();
            report.AppendLine(
                $"{gaps.Count,8}  {FormatSpan(gaps[0]),10}  {FormatSpan(Percentile(gaps, 50)),10}  "
                + $"{FormatSpan(Percentile(gaps, 95)),10}  {type}");
        }

        if (gapsByType.Count == 0)
            report.AppendLine("  (nothing repeated on the same subject in this window)");
        report.AppendLine();
    }

    /// <summary>Reading 3 — how long a condition takes to resolve itself.</summary>
    private static void AppendConditions(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("── Self-resolve time, per candidate condition ──────────────────────");
        report.AppendLine("How long each condition took to end on its own. This is where a settle");
        report.AppendLine("window comes from — and where the edge/state split is decided: a condition");
        report.AppendLine("that resolves faster than a rule could act on it is one a rule should");
        report.AppendLine("re-derive from the world, not one it should catch the announcement of.");
        report.AppendLine();
        report.AppendLine("⚠ 'never closed' includes conditions still open at the end of the window,");
        report.AppendLine("  and conditions whose closing event the reactor was not running to see.");
        report.AppendLine();

        foreach ((string opens, string closes, string question) in ConditionPairs)
        {
            List<long> durations = [];
            int unclosed = 0;

            foreach (IGrouping<string, Row> subject in rows
                         .Where(r => r.Subject.Length > 0
                                     && (r.EventType == opens || r.EventType == closes))
                         .GroupBy(r => r.Subject))
            {
                List<Row> ordered = subject.OrderBy(r => r.OccurredAt).ToList();
                long? openedAt = null;

                foreach (Row row in ordered)
                {
                    if (row.EventType == opens)
                    {
                        // A second opening before a close is the same episode continuing, not a new
                        // one: the first is when the condition began, which is what a settle window
                        // is measured from.
                        openedAt ??= row.OccurredAt;
                    }
                    else if (openedAt is { } start)
                    {
                        durations.Add(row.OccurredAt - start);
                        openedAt = null;
                    }
                }

                if (openedAt is not null)
                    unclosed++;
            }

            report.AppendLine($"  {opens} → {closes}");
            report.AppendLine($"    {question}");

            if (durations.Count == 0)
            {
                report.AppendLine($"    no closed episodes in this window ({unclosed} never closed)");
                report.AppendLine();
                continue;
            }

            durations.Sort();
            report.AppendLine(
                $"    {durations.Count} closed — min {FormatSpan(durations[0])}, "
                + $"p50 {FormatSpan(Percentile(durations, 50))}, "
                + $"p95 {FormatSpan(Percentile(durations, 95))}, "
                + $"max {FormatSpan(durations[^1])}; {unclosed} never closed");
            report.AppendLine();
        }
    }

    /// <summary>The most timestamps falling inside any one <see cref="BurstWindow"/>, and when.</summary>
    /// <remarks>
    /// A sliding window over the sorted times rather than fixed buckets: a burst that straddles a
    /// bucket boundary is split in half by bucketing, which under-reports exactly the peak a ceiling
    /// has to be set above.
    /// </remarks>
    private static (int Peak, long At) PeakInWindow(List<long> times)
    {
        if (times.Count == 0)
            return (0, 0);

        times.Sort();
        long span = (long)BurstWindow.TotalMilliseconds;
        int best = 0;
        long bestAt = times[0];
        int start = 0;

        for (int end = 0; end < times.Count; end++)
        {
            while (times[end] - times[start] > span)
                start++;
            int count = end - start + 1;
            if (count > best)
            {
                best = count;
                bestAt = times[start];
            }
        }

        return (best, bestAt);
    }

    /// <summary>Nearest-rank percentile over a sorted list.</summary>
    private static long Percentile(List<long> sorted, int percentile)
    {
        if (sorted.Count == 0)
            return 0;
        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static string FormatInstant(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
            .ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A duration in the largest unit that still says something useful.</summary>
    private static string FormatSpan(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        if (span.TotalSeconds < 90)
            return $"{span.TotalSeconds:F1}s";
        if (span.TotalMinutes < 90)
            return $"{span.TotalMinutes:F1}m";
        if (span.TotalHours < 48)
            return $"{span.TotalHours:F1}h";
        return $"{span.TotalDays:F1}d";
    }
}
