using System.Globalization;
using System.Text;

using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Reporting;

/// <summary>
/// Every judgment the reactor has reached, laid out to be argued with.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so the review gate can be performed rather than declared.</b> Nothing may move to
/// propose or act until a week of decisions has been read against what a person would actually have
/// done — and a gate whose only tooling is a hand-written SQL session is a gate that becomes a
/// formality on the first busy afternoon.
/// </para>
/// <para>
/// It answers a different question from the population report. That one asks what this host does;
/// this asks <em>what the reactor made of it</em>, which is the thing that has to be wrong in an
/// obvious way before anybody notices it is wrong at all.
/// </para>
/// <para>
/// ⚠ <b>It reports, it does not recommend</b> — the same rule the population report holds to. The
/// suppression window and the ceiling are decision #8 and they are read off these numbers by a
/// person; a report that proposed values would be answering the question with arithmetic and the
/// authority of a printed figure.
/// </para>
/// </remarks>
internal static class DecisionReport
{
    /// <summary>The window the ceiling is stated over, and therefore the one it is measured over.</summary>
    private static readonly TimeSpan CeilingWindow = TimeSpan.FromHours(1);

    /// <summary>One decision as the report reads it.</summary>
    private readonly record struct Row(
        string RuleId,
        string Subject,
        string SubjectKind,
        string Severity,
        string Outcome,
        string Reason,
        string Action,
        long OpenedAt,
        long DecidedAt,
        string Source);

    /// <summary>Renders the whole review for the last <paramref name="days"/> days.</summary>
    public static string Render(ObservationLedger ledger, int days, DateTimeOffset now)
    {
        long since = now.AddDays(-days).ToUnixTimeMilliseconds();

        List<Row> rows = ledger.Query(
            """
            SELECT rule_id, subject, subject_kind, severity, outcome, reason, action,
                   opened_at, decided_at, src_producer || ':' || src_segment || ':' || src_offset
            FROM decisions
            WHERE decided_at >= $since
            ORDER BY decided_at;
            """,
            command => command.Parameters.AddWithValue("$since", since),
            reader => new Row(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt64(7), reader.GetInt64(8), reader.GetString(9)));

        var report = new StringBuilder();
        report.AppendLine($"kgsm-reactor — decisions over the last {days} day(s)");
        report.AppendLine($"read at {now:yyyy-MM-dd HH:mm:ss} UTC from {ledger.Path}");
        report.AppendLine();

        if (rows.Count == 0)
        {
            report.AppendLine("No decisions in the window.");
            report.AppendLine();
            report.AppendLine("A reading, not an error, and an ambiguous one: a rule decides nothing");
            report.AppendLine("when its condition has not occurred AND when the event that wakes it has");
            report.AppendLine("never arrived. Those are different, and the population report separates");
            report.AppendLine("them — a wake event absent from it is one no rule here can ever fire on.");
            return report.ToString();
        }

        report.AppendLine($"{rows.Count} decision(s), "
            + $"{Instant(rows[0].DecidedAt)} → {Instant(rows[^1].DecidedAt)}");
        report.AppendLine();

        AppendOutcomeMix(report, rows);
        AppendCeilingPressure(report, rows, days);
        AppendRepeatSpacing(report, rows);
        AppendSilentRules(report, rows);
        AppendLog(report, rows);

        return report.ToString();
    }

    /// <summary>
    /// Reading 1 — what each rule concluded, and how often.
    /// </summary>
    /// <remarks>
    /// The share that never reached an action is the one to read first. A rule suppressed four times
    /// in five is telling you its window is too wide; one that is almost always unreadable is telling
    /// you it depends on something this host cannot answer, which is a rule that will keep failing
    /// quietly after it is allowed to act.
    /// </remarks>
    private static void AppendOutcomeMix(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("── What each rule concluded ────────────────────────────────────────");
        report.AppendLine("Read the share that did NOT fire. Mostly suppressed means the window is too");
        report.AppendLine("wide; mostly unreadable means the rule rests on something unanswerable here.");
        report.AppendLine();

        foreach (IGrouping<string, Row> rule in rows.GroupBy(r => r.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int total = rule.Count();
            report.AppendLine($"   {rule.Key}  ({total} decision(s))");

            foreach (IGrouping<string, Row> outcome in rule
                .GroupBy(r => r.Outcome)
                .OrderByDescending(g => g.Count()))
            {
                double share = 100.0 * outcome.Count() / total;
                report.AppendLine(
                    $"      {outcome.Count(),5}  {share,5:F1}%  {outcome.Key.ToLowerInvariant()}");
            }

            report.AppendLine();
        }
    }

    /// <summary>
    /// Reading 2 — what a host-wide ceiling would have caught.
    /// </summary>
    /// <remarks>
    /// The busiest hour on record, against which a ceiling is either a safety net or a gag. Counted
    /// over fired decisions only, because those are the ones a ceiling exists to bound — an
    /// evaluation that settled cost the host nothing.
    /// </remarks>
    private static void AppendCeilingPressure(StringBuilder report, List<Row> rows, int days)
    {
        List<long> fired = [.. rows.Where(r => Fired(r)).Select(r => r.DecidedAt)];

        report.AppendLine("── What a ceiling would have had to tolerate ───────────────────────");
        report.AppendLine("The busiest rolling hour of FIRED decisions. A ceiling below this figure");
        report.AppendLine("would have gagged the reactor during the hour it had most to say.");
        report.AppendLine();

        if (fired.Count == 0)
        {
            report.AppendLine("   nothing fired in the window — no pressure to measure, and no basis");
            report.AppendLine("   for a ceiling either way.");
            report.AppendLine();
            return;
        }

        (int peak, long at) = PeakInWindow(fired);

        report.AppendLine($"   {fired.Count,5}  fired in total, {(double)fired.Count / days:F1} per day");
        report.AppendLine($"   {peak,5}  in the busiest hour, ending {Instant(at)}");
        report.AppendLine();
    }

    /// <summary>
    /// Reading 3 — how far apart a rule's repeats on one subject actually are.
    /// </summary>
    /// <remarks>
    /// This is what the suppression window is derived from, and it is deliberately measured on fired
    /// decisions rather than on the raw events: the question is not how often the host repeats
    /// itself, it is how often a <em>rule</em> would have spoken about the same subject twice.
    /// </remarks>
    private static void AppendRepeatSpacing(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("── How soon a rule would speak about one subject again ─────────────");
        report.AppendLine("The gap between consecutive FIRED decisions per (rule, subject). A window");
        report.AppendLine("shorter than these says the same thing twice; much longer swallows news.");
        report.AppendLine();

        var any = false;

        foreach (IGrouping<(string, string), Row> pair in rows
            .Where(Fired)
            .GroupBy(r => (r.RuleId, r.Subject))
            .OrderBy(g => g.Key, Comparer<(string, string)>.Default))
        {
            List<long> times = [.. pair.Select(r => r.DecidedAt).Order()];
            if (times.Count < 2)
                continue;

            any = true;
            List<long> gaps = [.. times.Zip(times.Skip(1), (a, b) => b - a).Order()];

            report.AppendLine(
                $"   {pair.Key.Item1} on {pair.Key.Item2}  ({times.Count} fires)");
            report.AppendLine(
                $"      shortest {Span(gaps[0])}   median {Span(gaps[gaps.Count / 2])}   "
                + $"longest {Span(gaps[^1])}");
        }

        if (!any)
        {
            report.AppendLine("   no rule fired twice about the same subject in the window.");
            report.AppendLine("   ⚠ A suppression window derived from this would be derived from nothing;");
            report.AppendLine("     it stays a placeholder until a repeat has actually been measured.");
        }

        report.AppendLine();
    }

    /// <summary>
    /// Reading 4 — the rules that said nothing at all.
    /// </summary>
    /// <remarks>
    /// A rule with no decisions is the failure that looks most like success: it is enabled, it appears
    /// in the descriptor and on the status socket, and it will go on deciding nothing forever. This
    /// names it. <b>What it does not do is say why</b> — whether the condition never occurred or the
    /// waking event never arrived is a question for the population report, and guessing between them
    /// here would be the same fabrication the leaf refuses everywhere else.
    /// </remarks>
    private static void AppendSilentRules(StringBuilder report, List<Row> rows)
    {
        HashSet<string> spoke = new(rows.Select(r => r.RuleId), StringComparer.Ordinal);
        string[] silent = [.. Rules.RuleCatalog.All
            .Select(r => r.Id)
            .Where(id => !spoke.Contains(id))
            .Order(StringComparer.Ordinal)];

        if (silent.Length == 0)
            return;

        report.AppendLine("── Rules that decided nothing ──────────────────────────────────────");
        report.AppendLine("Enabled, listed everywhere, and silent for the whole window. Check each");
        report.AppendLine("against the population report: a wake event that never appears there is one");
        report.AppendLine("the rule cannot fire on, whatever the catalog says.");
        report.AppendLine();

        foreach (string id in silent)
            report.AppendLine($"   {id}");

        report.AppendLine();
    }

    /// <summary>
    /// The decisions themselves, newest first — the thing actually being reviewed.
    /// </summary>
    /// <remarks>
    /// The source position travels with every line because invariant 1 says a decision is never the
    /// only record. A reviewer disagreeing with a judgment needs to read what it was made from, and
    /// this is what makes that a lookup rather than an archaeology.
    /// </remarks>
    private static void AppendLog(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("── The decisions ───────────────────────────────────────────────────");
        report.AppendLine("Newest first. `from` is the journal line it was derived from — go and read");
        report.AppendLine("it before disagreeing with the verdict.");
        report.AppendLine();

        foreach (Row row in rows.OrderByDescending(r => r.DecidedAt))
        {
            report.AppendLine(
                $"   {Instant(row.DecidedAt)}  {row.Outcome.ToLowerInvariant(),-11} "
                + $"{row.RuleId} on {row.Subject} ({row.SubjectKind.ToLowerInvariant()}, {row.Severity.ToLowerInvariant()})");
            report.AppendLine($"      would: {row.Action}");
            report.AppendLine($"      why:   {row.Reason}");
            report.AppendLine($"      open:  {Span(row.DecidedAt - row.OpenedAt)} before it was judged");
            report.AppendLine($"      from:  {row.Source}");
            report.AppendLine();
        }
    }

    private static bool Fired(Row row) =>
        string.Equals(row.Outcome, nameof(DecisionOutcome.Fired), StringComparison.OrdinalIgnoreCase);

    /// <summary>The most decisions inside any one-hour window, and when that window ended.</summary>
    private static (int Peak, long At) PeakInWindow(List<long> times)
    {
        long window = (long)CeilingWindow.TotalMilliseconds;
        var peak = 0;
        long at = times[0];
        var start = 0;

        for (var end = 0; end < times.Count; end++)
        {
            while (times[end] - times[start] > window)
                start++;

            if (end - start + 1 > peak)
            {
                peak = end - start + 1;
                at = times[end];
            }
        }

        return (peak, at);
    }

    private static string Instant(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            .ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Span(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);

        return span.TotalDays >= 1 ? $"{span.TotalDays:F1}d"
            : span.TotalHours >= 1 ? $"{span.TotalHours:F1}h"
            : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:F1}m"
            : $"{span.TotalSeconds:F0}s";
    }
}
