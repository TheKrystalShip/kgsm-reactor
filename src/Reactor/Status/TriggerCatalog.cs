using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ledger;

namespace TheKrystalShip.Kgsm.Reactor.Status;

/// <summary>
/// The events a rule may wake on, read off what this host has actually seen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived, not maintained.</b> The ledger already records every event type it has observed, with
/// its producer and its count. A hand-written list of triggers would drift from that the first time
/// any producer emitted something new; a list read from the ledger expands on its own, and a producer
/// that joins this host becomes available to build rules on the day it starts writing.
/// </para>
/// <para>
/// ⚠ <b>Read from the ledger, never from the journals.</b> The ledger holds one event vocabulary and
/// folds a renamed event onto its current name; a segment keeps whatever its producer wrote. A catalog
/// built from raw journals would offer two spellings of the same trigger and a person would pick one
/// of them by chance.
/// </para>
/// <para>
/// ⚠ <b>Each entry names its producer, and the rate is the point.</b> Scope is otherwise invisible: an
/// engine event is about the fleet, and a leaf's is often about that leaf's own business. And a rule
/// built on something that fires two hundred times a week is a different proposition from one built on
/// something that fires twice — a person should be able to see that before they build it, not after.
/// </para>
/// <para>
/// ⚠ <b><c>reactor.*</c> is absent, which is how the feedback loop stays impossible.</b> This leaf
/// tails its own journal, so a rule woken by a decision it wrote would decide about its own decision,
/// write that, and be woken by it — at the sweep interval, forever, with a plausible-looking ledger.
/// </para>
/// </remarks>
public sealed record TriggerCatalog
{
    /// <summary>How far back the counts were read over.</summary>
    [JsonPropertyName("days")]
    public required int Days { get; init; }

    /// <summary>Every event type observed in that window, busiest first.</summary>
    [JsonPropertyName("triggers")]
    public required IReadOnlyList<TriggerInfo> Triggers { get; init; }

    /// <summary>The most event types one answer carries.</summary>
    /// <remarks>
    /// Well above what any host has been seen to produce. It exists so a ledger that has somehow
    /// accumulated thousands of distinct names cannot turn a page view into a wall of them.
    /// </remarks>
    public const int MaxTriggers = 500;

    /// <summary>Read the vocabulary this host has observed.</summary>
    internal static TriggerCatalog Read(ObservationLedger ledger, int days, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        long since = now.AddDays(-Math.Max(days, 1)).ToUnixTimeMilliseconds();

        IReadOnlyList<TriggerInfo> rows = ledger.Query(
            """
            SELECT event_type, producer, COUNT(*), MIN(occurred_at), MAX(occurred_at)
            FROM observations
            WHERE occurred_at >= $since
            GROUP BY event_type, producer
            ORDER BY COUNT(*) DESC
            LIMIT $limit;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$limit", MaxTriggers);
            },
            reader => new TriggerInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                Math.Round(reader.GetInt64(2) / (double)Math.Max(days, 1) * 7, 1)));

        return new TriggerCatalog
        {
            Days = Math.Max(days, 1),
            Triggers =
            [
                // The loop guard, enforced by the catalog rather than by a rule somebody has to
                // remember not to write.
                .. rows.Where(t => !t.Type.StartsWith(ReactorEvents.Prefix, StringComparison.Ordinal)),
            ],
        };
    }
}

/// <summary>One event type this host has seen.</summary>
/// <param name="Type">The event's current name — what a rule wakes on.</param>
/// <param name="Producer">
/// Which journal it came from. Carried because scope is otherwise invisible: an engine event is about
/// the fleet, and a leaf's is often about that leaf's own business.
/// </param>
/// <param name="Count">How many were observed in the window.</param>
/// <param name="FirstSeen">The earliest one in the window.</param>
/// <param name="LastSeen">
/// The most recent. A type last seen weeks ago is one a rule can be built on and may wait a long time
/// for, which is a different thing from one that never happens.
/// </param>
/// <param name="PerWeek">
/// The count as a weekly rate, so a window of any length reads the same way. ⚠ An average over the
/// window, not a prediction: a hundred events in one afternoon and a hundred spread over a month
/// report the same figure, and the two are very different rules to build.
/// </param>
public sealed record TriggerInfo(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("firstSeen")] DateTimeOffset FirstSeen,
    [property: JsonPropertyName("lastSeen")] DateTimeOffset LastSeen,
    [property: JsonPropertyName("perWeek")] double PerWeek);

/// <summary>
/// The serializer for the trigger endpoint.
/// </summary>
/// <remarks>
/// Source-generated, because this binary is Native AOT: there is no reflection fallback, and a type
/// nobody registered throws at runtime rather than degrading.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TriggerCatalog))]
public partial class TriggerCatalogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
