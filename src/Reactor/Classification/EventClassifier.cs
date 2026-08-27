using System.Text.Json;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Classification;

/// <summary>What kind of thing an event is about. A bucketing aid, not a judgment.</summary>
/// <remarks>
/// ⚠ <b>This is not severity and it is not a rule.</b> It exists so the population report can group
/// ninety event types into something a person can read while deciding which conditions are worth a
/// rule. Nothing gates on it, and nothing should start to: what matters about an event is decided
/// per rule, against the seven questions, not inherited from a bucket assigned here.
/// </remarks>
internal enum EventClass
{
    /// <summary>A server started, stopped, became ready, was restarted.</summary>
    Lifecycle,

    /// <summary>Something failed: a crash, a give-up, an operation that did not complete.</summary>
    Fault,

    /// <summary>A metric crossed a threshold, or came back.</summary>
    Threshold,

    /// <summary>An update, a backup, a restore, an install, a deploy — work on a server.</summary>
    Maintenance,

    /// <summary>Somebody joined, left, or was moderated.</summary>
    Player,

    /// <summary>Ports opened, closed, or re-asserted.</summary>
    Network,

    /// <summary>A KGSM component came up, degraded, recovered or stopped.</summary>
    Leaf,

    /// <summary>Configuration or a blueprint changed.</summary>
    Configuration,

    /// <summary>An account, a session or an identity link.</summary>
    Identity,

    /// <summary>The assistant reporting on itself.</summary>
    Assistant,

    /// <summary>
    /// This leaf reporting its own judgments.
    /// </summary>
    /// <remarks>
    /// The reactor tails every producer's journal, its own included, so what it writes comes back to
    /// it. Recorded like anything else — a decision is a real fact about the host — but no rule may
    /// wake on one, which is where the loop is stopped rather than here.
    /// </remarks>
    Reactor,

    /// <summary>A type this build does not recognise. Recorded, never dropped.</summary>
    Other,
}

/// <summary>What an event is about: a game server, the host, or a component.</summary>
internal enum SubjectKind
{
    /// <summary>A named game server instance.</summary>
    Instance,

    /// <summary>The host itself, or one of its measured references.</summary>
    Host,

    /// <summary>A KGSM component on this host.</summary>
    Leaf,

    /// <summary>Nothing in the payload named a subject.</summary>
    Unknown,
}

/// <summary>One event, reduced to the few facts the ledger records.</summary>
/// <param name="Class">The bucket, for reporting only.</param>
/// <param name="SubjectKind">What sort of thing it is about.</param>
/// <param name="Subject">Which one, or empty when the payload named none.</param>
internal readonly record struct EventFacts(EventClass Class, SubjectKind SubjectKind, string Subject);

/// <summary>
/// Reduces an event envelope to the handful of facts the ledger records.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads the payload, it does not deserialize it.</b> The subject is pulled out of the
/// envelope's <see cref="JsonElement"/> by property name, so an event type this build has never
/// heard of is still recorded with its subject — which is the whole point of recording at the raw
/// level. A typed path would drop exactly the events a later rule might be about.
/// </para>
/// <para>
/// <b>Nothing here is inferred.</b> A payload with no subject property yields
/// <see cref="SubjectKind.Unknown"/> and an empty subject rather than a guess at which server was
/// meant. An unrecognised type is <see cref="EventClass.Other"/> and is recorded exactly like any
/// other — a class this build does not know is not the same as an event that did not happen.
/// </para>
/// </remarks>
internal static class EventClassifier
{
    /// <summary>The property carrying the server name, in every instance-scoped payload.</summary>
    private const string InstanceNameProperty = "InstanceName";

    /// <summary>The threshold payload's server, when the episode is scoped to one.</summary>
    private const string ServerIdProperty = "ServerId";

    /// <summary>
    /// The subject named by this leaf's own events, which carry it under its own name.
    /// </summary>
    /// <remarks>
    /// Read rather than ignored: a decision about <c>starbound</c> is genuinely about
    /// <c>starbound</c>, and filing it under nothing would make the ledger's history of a server
    /// incomplete for no gain. Safe because nothing wakes on a <c>reactor.*</c> event.
    /// </remarks>
    private const string SubjectProperty = "Subject";

    /// <summary>The threshold payload's measured reference (a sensor, a mount).</summary>
    private const string RefProperty = "Ref";

    /// <summary>The threshold payload's metric, the fallback subject when there is no reference.</summary>
    private const string MetricProperty = "Metric";

    /// <summary>
    /// Reduce one event to its facts.
    /// </summary>
    /// <param name="eventType">The event type, as the line carries it.</param>
    /// <param name="data">The payload, as it arrived.</param>
    /// <param name="producer">
    /// Whose journal it came from. Used only as the subject of a <c>leaf.*</c> event, which is about
    /// the component that wrote it and names no subject of its own.
    /// </param>
    public static EventFacts Classify(string eventType, JsonElement data, string producer)
    {
        EventClass cls = ClassOf(eventType);

        // A leaf event is about the component that wrote it. The payload names a component only in
        // the degraded/recovered case, and that is a part of the leaf rather than the subject.
        if (cls == EventClass.Leaf)
            return new EventFacts(cls, SubjectKind.Leaf, producer);

        if (cls == EventClass.Threshold)
            return new EventFacts(cls, ThresholdSubjectKind(data), ThresholdSubject(data));

        // This leaf's own events name both the subject and what kind of thing it is, because whatever
        // decided already knew. Taking its word rather than re-deriving is the same rule that applies
        // everywhere else here: the classification travels with the observation.
        if (cls == EventClass.Reactor)
        {
            string decided = StringProperty(data, SubjectProperty);
            return decided.Length > 0
                ? new EventFacts(cls, ReactorSubjectKind(data), decided)
                : new EventFacts(cls, SubjectKind.Unknown, string.Empty);
        }

        string instance = StringProperty(data, InstanceNameProperty);
        return instance.Length > 0
            ? new EventFacts(cls, SubjectKind.Instance, instance)
            : new EventFacts(cls, SubjectKind.Unknown, string.Empty);
    }

    /// <summary>
    /// The bucket an event type falls in.
    /// </summary>
    /// <remarks>
    /// Prefix matching rather than an exhaustive list of the ~90 types kgsm-lib declares, and
    /// deliberately: the engine gains types faster than this leaf is rebuilt, and a new
    /// <c>backup.*</c> should land in Maintenance the day it is emitted rather than in Other
    /// until somebody notices. The exact names below are the ones whose prefix would put them in the
    /// wrong bucket.
    /// </remarks>
    private static EventClass ClassOf(string eventType) => LegacyEventNames.Canonical(eventType) switch
    {
        // Faults first: several of them share a namespace with the operation they are the failure of.
        "server.crashed" or "server.crash.exhausted" => EventClass.Fault,
        var t when t.EndsWith(".failed", StringComparison.Ordinal) => EventClass.Fault,

        "server.started" or "server.stopped" or "server.ready" or "server.restarted"
            => EventClass.Lifecycle,
        var t when t.StartsWith("server.stop.", StringComparison.Ordinal)
                || t.StartsWith("server.restart.", StringComparison.Ordinal)
            => EventClass.Lifecycle,

        var t when t.StartsWith("host.threshold.", StringComparison.Ordinal) => EventClass.Threshold,

        var t when t.StartsWith("player.", StringComparison.Ordinal) => EventClass.Player,

        var t when t.StartsWith("network.", StringComparison.Ordinal) => EventClass.Network,

        var t when t.StartsWith("leaf.", StringComparison.Ordinal) => EventClass.Leaf,

        "config.changed" or "service.config_changed" => EventClass.Configuration,
        var t when t.StartsWith("blueprint.", StringComparison.Ordinal) => EventClass.Configuration,

        var t when t.StartsWith("assistant.", StringComparison.Ordinal) => EventClass.Assistant,

        var t when t.StartsWith(Events.ReactorEvents.Prefix, StringComparison.Ordinal)
            => EventClass.Reactor,

        var t when t.StartsWith("account.", StringComparison.Ordinal)
                || t.StartsWith("auth.", StringComparison.Ordinal)
                || t.StartsWith("user.", StringComparison.Ordinal)
                || t.StartsWith("identity.", StringComparison.Ordinal)
            => EventClass.Identity,

        var t when t.StartsWith("server.update", StringComparison.Ordinal)
                || t.StartsWith("server.install", StringComparison.Ordinal)
                || t.StartsWith("server.uninstall", StringComparison.Ordinal)
                || t.StartsWith("server.deploy", StringComparison.Ordinal)
                || t.StartsWith("server.download", StringComparison.Ordinal)
                || t.StartsWith("backup.", StringComparison.Ordinal)
            => EventClass.Maintenance,

        _ => EventClass.Other,
    };

    /// <summary>
    /// What kind of thing one of this leaf's own events was about, as that event says.
    /// </summary>
    /// <remarks>
    /// A spelling this build does not recognise reads as <see cref="SubjectKind.Unknown"/>. That is a
    /// newer reactor's vocabulary reaching an older one, and guessing at it would be a fabrication
    /// indistinguishable from a reading afterwards.
    /// </remarks>
    private static SubjectKind ReactorSubjectKind(JsonElement data) =>
        Enum.TryParse(
            StringProperty(data, Events.ReactorEventFields.SubjectKind),
            ignoreCase: true,
            out SubjectKind kind)
            ? kind
            : SubjectKind.Unknown;

    /// <summary>
    /// A threshold episode is about one server when it names one, and about the host otherwise.
    /// </summary>
    private static SubjectKind ThresholdSubjectKind(JsonElement data) =>
        StringProperty(data, ServerIdProperty).Length > 0 ? SubjectKind.Instance : SubjectKind.Host;

    /// <summary>
    /// Which server, or which measured reference of the host.
    /// </summary>
    /// <remarks>
    /// The reference rather than the metric where there is one: two sensors breaching the same
    /// metric are two episodes, and a subject that collapsed them would make one look like a repeat
    /// of the other — which is exactly the reading a suppression window is derived from.
    /// </remarks>
    private static string ThresholdSubject(JsonElement data)
    {
        string server = StringProperty(data, ServerIdProperty);
        if (server.Length > 0)
            return server;

        string reference = StringProperty(data, RefProperty);
        return reference.Length > 0 ? reference : StringProperty(data, MetricProperty);
    }

    /// <summary>
    /// One string property of the payload, or empty.
    /// </summary>
    /// <remarks>
    /// Empty for absent, for null and for a non-string alike. The caller's question is always "did
    /// the payload name this", and a JSON null answers it the same way a missing property does —
    /// several payloads carry an explicit <c>null</c> subject (a threshold episode scoped to the
    /// host writes <c>"ServerId": null</c>).
    /// </remarks>
    private static string StringProperty(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (!data.TryGetProperty(name, out JsonElement property))
            return string.Empty;
        if (property.ValueKind != JsonValueKind.String)
            return string.Empty;
        return property.GetString() ?? string.Empty;
    }
}
