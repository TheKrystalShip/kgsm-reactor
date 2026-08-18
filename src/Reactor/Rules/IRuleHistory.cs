namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>One observed event, as a rule needs to see it.</summary>
internal readonly record struct HistoryEvent(string EventType, string Subject, DateTimeOffset OccurredAt);

/// <summary>A condition that opened and has not closed.</summary>
/// <param name="Subject">What it is about.</param>
/// <param name="SubjectKind">
/// What sort of thing that is, as it was classified when the opening line was read. Carried rather
/// than re-derived from the name: the same string can be a server here and a sensor reference
/// elsewhere, and only the observation knows which it was.
/// </param>
/// <param name="OpenedAt">When it opened.</param>
/// <param name="Source">The journal line that opened it — the episode's identity.</param>
internal readonly record struct OpenEpisode(
    string Subject,
    Classification.SubjectKind SubjectKind,
    DateTimeOffset OpenedAt,
    Ledger.EventSource Source);

/// <summary>
/// What has been observed, for the questions a single event cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// This is the ledger's second job, and the one that justifies it being a database rather than a
/// second log. A rule asking <em>"has this been true for longer than it usually is here"</em> needs a
/// distribution, and one asking <em>"since when"</em> needs to survive a restart of this daemon —
/// neither is answerable from memory or from the event that woke it.
/// </para>
/// <para>
/// ⚠ Everything here is bounded by the retention window and by how long this leaf has been running.
/// A query that finds nothing is reporting exactly that, and a rule reading it must not treat an
/// empty answer as evidence of absence.
/// </para>
/// </remarks>
internal interface IRuleHistory
{
    /// <summary>The most recent occurrence of <paramref name="eventType"/> for a subject, if any.</summary>
    HistoryEvent? LastOccurrence(string eventType, string subject, DateTimeOffset notBefore);

    /// <summary>
    /// Subjects with an episode currently open: an <paramref name="opensWith"/> more recent than any
    /// <paramref name="closesWith"/>, and the instant it opened.
    /// </summary>
    /// <remarks>
    /// The opening line's position comes back with it, because that position is the episode's
    /// identity — what makes a re-evaluation on the next sweep the same decision refined rather than
    /// a second one.
    /// </remarks>
    IReadOnlyList<OpenEpisode> OpenEpisodes(string opensWith, string closesWith, DateTimeOffset notBefore);

    /// <summary>
    /// How long episodes of this kind usually last for one subject, from the closed ones on record.
    /// </summary>
    /// <returns>
    /// The p95 duration and how many closed episodes it was computed from. <b>The count is returned
    /// with it because a percentile over three samples is not a distribution</b>, and a rule
    /// comparing against one has to be able to refuse rather than pretend.
    /// </returns>
    (TimeSpan P95, int Samples) EpisodeDuration(
        string opensWith, string closesWith, string subject, DateTimeOffset notBefore);
}
