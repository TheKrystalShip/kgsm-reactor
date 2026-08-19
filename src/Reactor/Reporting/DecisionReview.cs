using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Reporting;

/// <summary>
/// Every judgment the reactor has reached over a window, as data.
/// </summary>
/// <remarks>
/// <para>
/// <b>The arithmetic of the review, separated from the rendering of it.</b> Two surfaces ask this
/// question — a person at a terminal running <c>--decisions</c>, and the Control Panel over the status
/// socket — and the readings are the same readings. Computing them twice is how the two come to
/// disagree about the busiest hour, which is precisely the figure a ceiling is set from.
/// </para>
/// <para>
/// ⚠ <b>It reports, it does not recommend.</b> No window, threshold or ceiling is suggested here. The
/// numbers are what a person reads the gate's tuning off; a payload that proposed values would be
/// answering the question with arithmetic and the authority of a printed figure.
/// </para>
/// <para>
/// The four readings and why each is here are documented on the properties. The one to read first is
/// <see cref="Rules"/> — the share of a rule's decisions that never reached an action is what says
/// whether its windows are right.
/// </para>
/// </remarks>
/// <param name="WindowDays">How far back this was read.</param>
/// <param name="Since">The start of the window.</param>
/// <param name="Until">When it was read.</param>
/// <param name="LedgerPath">Where the rows came from.</param>
/// <param name="Total">
/// Decisions in the window. ⚠ Compare against the length of <see cref="Decisions"/> — that list is
/// capped and this number is not, so the two differing means the log is showing the newest of more.
/// </param>
/// <param name="Rules">Reading 1 — what each rule concluded, and how often.</param>
/// <param name="Ceiling">Reading 2 — what a host-wide ceiling would have had to tolerate.</param>
/// <param name="Repeats">Reading 3 — how soon a rule would speak about one subject again.</param>
/// <param name="Silent">Reading 4 — the rules that decided nothing at all.</param>
/// <param name="Decisions">The decisions themselves, newest first.</param>
internal sealed record DecisionReview(
    [property: JsonPropertyName("windowDays")] int WindowDays,
    [property: JsonPropertyName("since")] DateTimeOffset Since,
    [property: JsonPropertyName("until")] DateTimeOffset Until,
    [property: JsonPropertyName("ledgerPath")] string LedgerPath,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("rules")] IReadOnlyList<RuleOutcomes> Rules,
    [property: JsonPropertyName("ceiling")] CeilingPressure? Ceiling,
    [property: JsonPropertyName("repeats")] IReadOnlyList<RepeatSpacing> Repeats,
    [property: JsonPropertyName("silent")] IReadOnlyList<string> Silent,
    [property: JsonPropertyName("decisions")] IReadOnlyList<DecisionRow> Decisions)
{
    /// <summary>The window the ceiling is stated over, and therefore the one it is measured over.</summary>
    private static readonly TimeSpan CeilingWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Reads the review for the last <paramref name="days"/> days.
    /// </summary>
    /// <param name="ledger">The ledger to read.</param>
    /// <param name="days">How far back to look.</param>
    /// <param name="now">The reading instant. Passed rather than read, so a test owns the clock.</param>
    /// <param name="limit">
    /// How many decisions the log carries at most, newest first.
    /// </param>
    /// <remarks>
    /// ⚠ <b>The limit caps the log and never the arithmetic.</b> Every reading is computed over every
    /// row in the window; only the list at the end is trimmed. Measuring the busiest hour over a
    /// truncated sample would under-report exactly the peak a ceiling has to be set above — which is
    /// the one number this whole payload exists to establish.
    /// </remarks>
    public static DecisionReview Read(ObservationLedger ledger, int days, DateTimeOffset now, int limit)
    {
        long since = now.AddDays(-days).ToUnixTimeMilliseconds();

        List<DecisionRow> rows = ledger.Query(
            """
            SELECT rule_id, subject, subject_kind, severity, mode, outcome, reason,
                   action, action_name, action_inst, action_state,
                   opened_at, decided_at,
                   src_producer || ':' || src_segment || ':' || src_offset, src_event_id
            FROM decisions
            WHERE decided_at >= $since
            ORDER BY decided_at;
            """,
            command => command.Parameters.AddWithValue("$since", since),
            reader => new DecisionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));

        return new DecisionReview(
            days,
            DateTimeOffset.FromUnixTimeMilliseconds(since),
            now,
            ledger.Path,
            rows.Count,
            OutcomeMix(rows),
            Pressure(rows, days),
            RepeatGaps(rows),
            SilentRules(rows),
            [.. rows.OrderByDescending(r => r.DecidedAt).Take(limit)]);
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
    private static IReadOnlyList<RuleOutcomes> OutcomeMix(List<DecisionRow> rows) =>
    [
        .. rows
            .GroupBy(r => r.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(rule => new RuleOutcomes(
                rule.Key,
                rule.Count(),
                [
                    .. rule
                        .GroupBy(r => r.Outcome, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count())
                        .Select(o => new OutcomeCount(o.Key.ToLowerInvariant(), o.Count())),
                ])),
    ];

    /// <summary>
    /// Reading 2 — what a host-wide ceiling would have had to tolerate.
    /// </summary>
    /// <remarks>
    /// The busiest hour on record, against which a ceiling is either a safety net or a gag. Counted
    /// over fired decisions only, because those are the ones a ceiling exists to bound — an
    /// evaluation that settled cost the host nothing. Null when nothing fired: there is no pressure
    /// to measure and no basis for a ceiling either way, which is a different statement from a peak
    /// of zero.
    /// </remarks>
    private static CeilingPressure? Pressure(List<DecisionRow> rows, int days)
    {
        List<long> fired = [.. rows.Where(Fired).Select(r => r.DecidedAt.ToUnixTimeMilliseconds())];
        if (fired.Count == 0)
            return null;

        (int peak, long at) = PeakInWindow(fired);

        return new CeilingPressure(
            fired.Count,
            days > 0 ? (double)fired.Count / days : fired.Count,
            peak,
            DateTimeOffset.FromUnixTimeMilliseconds(at));
    }

    /// <summary>
    /// Reading 3 — how far apart a rule's repeats on one subject actually are.
    /// </summary>
    /// <remarks>
    /// What the suppression window is derived from, and deliberately measured on fired decisions
    /// rather than on the raw events: the question is not how often the host repeats itself, it is
    /// how often a <em>rule</em> would have spoken about the same subject twice. A pair that fired
    /// once contributes nothing — a spacing derived from a single fire is a spacing derived from
    /// nothing.
    /// </remarks>
    private static IReadOnlyList<RepeatSpacing> RepeatGaps(List<DecisionRow> rows)
    {
        var spacings = new List<RepeatSpacing>();

        foreach (IGrouping<(string Rule, string Subject), DecisionRow> pair in rows
            .Where(Fired)
            .GroupBy(r => (r.RuleId, r.Subject))
            .OrderBy(g => g.Key, Comparer<(string, string)>.Default))
        {
            List<long> times = [.. pair.Select(r => r.DecidedAt.ToUnixTimeMilliseconds()).Order()];
            if (times.Count < 2)
                continue;

            List<long> gaps = [.. times.Zip(times.Skip(1), (a, b) => b - a).Order()];

            spacings.Add(new RepeatSpacing(
                pair.Key.Rule, pair.Key.Subject, times.Count,
                gaps[0], gaps[gaps.Count / 2], gaps[^1]));
        }

        return spacings;
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
    private static IReadOnlyList<string> SilentRules(List<DecisionRow> rows)
    {
        HashSet<string> spoke = new(rows.Select(r => r.RuleId), StringComparer.Ordinal);
        return
        [
            .. TheKrystalShip.Kgsm.Reactor.Rules.RuleCatalog.All
                .Select(r => r.Id)
                .Where(id => !spoke.Contains(id))
                .Order(StringComparer.Ordinal),
        ];
    }

    private static bool Fired(DecisionRow row) =>
        string.Equals(row.Outcome, nameof(DecisionOutcome.Fired), StringComparison.OrdinalIgnoreCase);

    /// <summary>The most decisions inside any one-hour window, and when that window ended.</summary>
    /// <remarks>
    /// A sliding window over the sorted times rather than fixed buckets: a burst that straddles a
    /// bucket boundary is split in half by bucketing, which under-reports exactly the peak a ceiling
    /// has to be set above.
    /// </remarks>
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
}

/// <summary>What one rule concluded over the window.</summary>
/// <param name="Id">The rule.</param>
/// <param name="Total">Its decisions in the window.</param>
/// <param name="Outcomes">Each outcome and its count, commonest first.</param>
internal sealed record RuleOutcomes(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("outcomes")] IReadOnlyList<OutcomeCount> Outcomes);

/// <summary>One outcome and how often a rule reached it.</summary>
/// <remarks>
/// The count rather than the share, because a share is derived and a reader that wants one can divide
/// by the rule's total — where a share carried on the wire at one decimal place cannot be turned back
/// into the count it came from.
/// </remarks>
internal sealed record OutcomeCount(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("count")] int Count);

/// <summary>What a host-wide ceiling would have had to tolerate.</summary>
/// <param name="Fired">Decisions that fired in the window.</param>
/// <param name="PerDay">Those, averaged over the window's days.</param>
/// <param name="PeakInHour">The most that fired inside any one rolling hour.</param>
/// <param name="PeakEndedAt">When that hour ended.</param>
internal sealed record CeilingPressure(
    [property: JsonPropertyName("fired")] int Fired,
    [property: JsonPropertyName("perDay")] double PerDay,
    [property: JsonPropertyName("peakInHour")] int PeakInHour,
    [property: JsonPropertyName("peakEndedAt")] DateTimeOffset PeakEndedAt);

/// <summary>How far apart one rule's fires about one subject were.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Subject">What it kept speaking about.</param>
/// <param name="Fires">How many times, which is at least two.</param>
/// <param name="ShortestMs">The smallest gap between consecutive fires.</param>
/// <param name="MedianMs">The middle gap.</param>
/// <param name="LongestMs">The largest.</param>
internal sealed record RepeatSpacing(
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("fires")] int Fires,
    [property: JsonPropertyName("shortestMs")] long ShortestMs,
    [property: JsonPropertyName("medianMs")] long MedianMs,
    [property: JsonPropertyName("longestMs")] long LongestMs);

/// <summary>One decision, as the review reads it.</summary>
/// <remarks>
/// <see cref="Source"/> and <see cref="EventId"/> travel with every row because invariant 1 says a
/// decision is never the only record. A reviewer disagreeing with a judgment needs to read what it was
/// made from, and carrying the position makes that a lookup rather than an archaeology.
/// </remarks>
/// <param name="RuleId">Which rule.</param>
/// <param name="Subject">What it was about.</param>
/// <param name="SubjectKind">What sort of thing that is — an instance, a host reference, a leaf.</param>
/// <param name="Severity">How loudly the rule speaks.</param>
/// <param name="Mode">The authority it ran under when this was decided.</param>
/// <param name="Outcome">What was decided.</param>
/// <param name="Reason">Why, in words. Always present, whichever way it went.</param>
/// <param name="Action">What it would do, described.</param>
/// <param name="ActionName">The same action as a stable name other programs compare against.</param>
/// <param name="ActionInstance">The server it would operate on, or null when it operates on none.</param>
/// <param name="ActionState">How far that got.</param>
/// <param name="OpenedAt">When the condition began.</param>
/// <param name="DecidedAt">When this evaluation ran.</param>
/// <param name="Source">The journal position it traces back to, as <c>producer:segment:offset</c>.</param>
/// <param name="EventId">The id that line's producer minted, or null where it carried none.</param>
internal sealed record DecisionRow(
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("subjectKind")] string SubjectKind,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("actionName")] string ActionName,
    [property: JsonPropertyName("actionInstance")] string? ActionInstance,
    [property: JsonPropertyName("actionState")] string ActionState,
    [property: JsonPropertyName("openedAt")] DateTimeOffset OpenedAt,
    [property: JsonPropertyName("decidedAt")] DateTimeOffset DecidedAt,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("eventId")] string? EventId);

/// <summary>
/// The serializer for the decisions endpoint.
/// </summary>
/// <remarks>
/// Source-generated, because this binary is Native AOT: there is no reflection fallback, and a type
/// nobody registered throws at runtime rather than degrading.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(DecisionReview))]
internal partial class DecisionReviewJsonContext : JsonSerializerContext;
