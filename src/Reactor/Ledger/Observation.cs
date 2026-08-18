using TheKrystalShip.Kgsm.Reactor.Classification;

namespace TheKrystalShip.Kgsm.Reactor.Ledger;

/// <summary>
/// One event, as the reactor recorded it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An observation is derived, never a record.</b> Every field restates something the producer's
/// own journal already holds, and the journal is what an incident is reconstructed from. What this
/// exists for is the questions a journal answers badly: how often does this type fire, how long
/// between two of them on the same server, how long does this condition take to resolve itself. Those
/// are queries, and they are why the ledger is a database rather than a second log.
/// </para>
/// <para>
/// <b>The identity is the position, not the content.</b> <see cref="Producer"/>, <see cref="Segment"/>
/// and <see cref="Offset"/> name one line of one file exactly once, so re-reading a segment can only
/// ever be a no-op. Deriving an id from the content instead would collapse two identical events in the
/// same second into one row — the engine's own index has that defect, and a rate measured from a
/// ledger with it would under-report exactly the bursts the ceiling is meant to bound.
/// </para>
/// </remarks>
/// <param name="Producer">Whose journal it came from.</param>
/// <param name="Segment">The segment file it was read from.</param>
/// <param name="Offset">Its byte offset in that segment.</param>
/// <param name="EventType">The event type, underscore-separated.</param>
/// <param name="Class">The reporting bucket. Not a judgment — see <see cref="EventClass"/>.</param>
/// <param name="SubjectKind">Whether it is about a server, the host, or a component.</param>
/// <param name="Subject">Which one, or empty when the payload named none.</param>
/// <param name="Actor">Who triggered it, verbatim from the envelope. Null when the producer said none.</param>
/// <param name="Origin">The surface it came through, verbatim. Null when the producer said none.</param>
/// <param name="OccurredAt">When the producer says it happened.</param>
/// <param name="ObservedAt">When the reactor read it. Differs from <paramref name="OccurredAt"/> by the
/// tail latency, and by everything that happened while the reactor was not running.</param>
internal sealed record Observation(
    string Producer,
    string Segment,
    long Offset,
    string EventType,
    EventClass Class,
    SubjectKind SubjectKind,
    string Subject,
    string? Actor,
    string? Origin,
    DateTimeOffset OccurredAt,
    DateTimeOffset ObservedAt);
