using System.Globalization;
using System.Text;

using TheKrystalShip.Kgsm.Reactor.Ledger;

using TheKrystalShip.KGSM.Events;

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
/// <b>The rendering only.</b> The readings themselves are <see cref="DecisionReview"/>, which the
/// status socket serves to the Control Panel — so a terminal and a browser are reading one
/// measurement rather than two implementations of it.
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
    /// <summary>Renders the whole review for the last <paramref name="days"/> days.</summary>
    public static string Render(ObservationLedger ledger, int days, DateTimeOffset now)
    {
        // No cap: a person reading the review wants every decision in the window, and the terminal is
        // where the whole log belongs. The socket's caller is the one that asks for a bounded list.
        DecisionReview review = DecisionReview.Read(ledger, days, now, int.MaxValue);

        var report = new StringBuilder();
        report.AppendLine($"kgsm-reactor — decisions over the last {days} day(s)");
        report.AppendLine($"read at {now:yyyy-MM-dd HH:mm:ss} UTC from {review.LedgerPath}");
        report.AppendLine();

        if (review.Total == 0)
        {
            report.AppendLine("No decisions in the window.");
            report.AppendLine();
            report.AppendLine("A reading, not an error, and an ambiguous one: a rule decides nothing");
            report.AppendLine("when its condition has not occurred AND when the event that wakes it has");
            report.AppendLine("never arrived. Those are different, and the population report separates");
            report.AppendLine("them — a wake event absent from it is one no rule here can ever fire on.");
            return report.ToString();
        }

        // The log is newest-first; the header states the window the rows actually span.
        report.AppendLine($"{review.Total} decision(s), "
            + $"{Instant(review.Decisions[^1].DecidedAt)} → {Instant(review.Decisions[0].DecidedAt)}");
        report.AppendLine();

        AppendOutcomeMix(report, review);
        AppendCeilingPressure(report, review);
        AppendRepeatSpacing(report, review);
        AppendSilentRules(report, review);
        AppendLog(report, review);

        return report.ToString();
    }

    /// <summary>Reading 1 — what each rule concluded, and how often.</summary>
    private static void AppendOutcomeMix(StringBuilder report, DecisionReview review)
    {
        report.AppendLine("── What each rule concluded ────────────────────────────────────────");
        report.AppendLine("Read the share that did NOT fire. Mostly suppressed means the window is too");
        report.AppendLine("wide; mostly unreadable means the rule rests on something unanswerable here.");
        report.AppendLine();

        foreach (RuleOutcomes rule in review.Rules)
        {
            report.AppendLine($"   {rule.Id}  ({rule.Total} decision(s))");

            foreach (OutcomeCount outcome in rule.Outcomes)
            {
                double share = 100.0 * outcome.Count / rule.Total;
                report.AppendLine($"      {outcome.Count,5}  {share,5:F1}%  {outcome.Outcome}");
            }

            report.AppendLine();
        }
    }

    /// <summary>Reading 2 — what a host-wide ceiling would have had to tolerate.</summary>
    private static void AppendCeilingPressure(StringBuilder report, DecisionReview review)
    {
        report.AppendLine("── What a ceiling would have had to tolerate ───────────────────────");
        report.AppendLine("The busiest rolling hour of FIRED decisions. A ceiling below this figure");
        report.AppendLine("would have gagged the reactor during the hour it had most to say.");
        report.AppendLine();

        if (review.Ceiling is not CeilingPressure pressure)
        {
            report.AppendLine("   nothing fired in the window — no pressure to measure, and no basis");
            report.AppendLine("   for a ceiling either way.");
            report.AppendLine();
            return;
        }

        report.AppendLine($"   {pressure.Fired,5}  fired in total, {pressure.PerDay:F1} per day");
        report.AppendLine(
            $"   {pressure.PeakInHour,5}  in the busiest hour, ending {Instant(pressure.PeakEndedAt)}");
        report.AppendLine();
    }

    /// <summary>Reading 3 — how far apart a rule's repeats on one subject actually are.</summary>
    private static void AppendRepeatSpacing(StringBuilder report, DecisionReview review)
    {
        report.AppendLine("── How soon a rule would speak about one subject again ─────────────");
        report.AppendLine("The gap between consecutive FIRED decisions per (rule, subject). A window");
        report.AppendLine("shorter than these says the same thing twice; much longer swallows news.");
        report.AppendLine();

        foreach (RepeatSpacing spacing in review.Repeats)
        {
            report.AppendLine($"   {spacing.Rule} on {spacing.Subject}  ({spacing.Fires} fires)");
            report.AppendLine(
                $"      shortest {Span(spacing.ShortestMs)}   median {Span(spacing.MedianMs)}   "
                + $"longest {Span(spacing.LongestMs)}");
        }

        if (review.Repeats.Count == 0)
        {
            report.AppendLine("   no rule fired twice about the same subject in the window.");
            report.AppendLine("   ⚠ A suppression window derived from this would be derived from nothing;");
            report.AppendLine("     it stays a placeholder until a repeat has actually been measured.");
        }

        report.AppendLine();
    }

    /// <summary>Reading 4 — the rules that said nothing at all.</summary>
    private static void AppendSilentRules(StringBuilder report, DecisionReview review)
    {
        if (review.Silent.Count == 0)
            return;

        report.AppendLine("── Rules that decided nothing ──────────────────────────────────────");
        report.AppendLine("Enabled, listed everywhere, and silent for the whole window. Check each");
        report.AppendLine("against the population report: a wake event that never appears there is one");
        report.AppendLine("the rule cannot fire on, whatever the catalog says.");
        report.AppendLine();

        foreach (string id in review.Silent)
            report.AppendLine($"   {id}");

        report.AppendLine();
    }

    /// <summary>The decisions themselves, newest first — the thing actually being reviewed.</summary>
    private static void AppendLog(StringBuilder report, DecisionReview review)
    {
        report.AppendLine("── The decisions ───────────────────────────────────────────────────");
        report.AppendLine("Newest first. `from` is the journal line it was derived from — go and read");
        report.AppendLine("it before disagreeing with the verdict.");
        report.AppendLine();

        foreach (DecisionRow row in review.Decisions)
        {
            report.AppendLine(
                $"   {Instant(row.DecidedAt)}  {row.Outcome.ToLowerInvariant(),-11} "
                + $"{row.RuleId} on {row.Subject} ({row.SubjectKind.ToLowerInvariant()}, {row.Severity.ToLowerInvariant()})");
            report.AppendLine($"      would: {row.Action}");
            report.AppendLine($"      why:   {row.Reason}");
            report.AppendLine(
                $"      open:  {Span((long)(row.DecidedAt - row.OpenedAt).TotalMilliseconds)} before it was judged");
            report.AppendLine($"      from:  {row.Source}");
            report.AppendLine();
        }
    }

    private static string Instant(DateTimeOffset at) =>
        at.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Span(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);

        return span.TotalDays >= 1 ? $"{span.TotalDays:F1}d"
            : span.TotalHours >= 1 ? $"{span.TotalHours:F1}h"
            : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:F1}m"
            : $"{span.TotalSeconds:F0}s";
    }
}
