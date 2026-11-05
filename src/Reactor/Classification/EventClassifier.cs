
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Classification;

/// <summary>What kind of thing an event is about. A bucketing aid, not a judgment.</summary>
/// <remarks>
/// <b>This is not severity and it is not a rule.</b> It exists so the population report can group
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
/// <b>It reads the subject off the payload's registered type.</b> An event type this build has never
/// heard of reads as <see cref="UnrecognizedEventData"/>, which carries the names a subject is keyed
/// on, so it is still recorded with its subject — the whole point of recording at the raw level.
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
    /// <summary>
    /// Reduce one event to its facts.
    /// </summary>
    /// <param name="eventType">The event type, as the line carries it.</param>
    /// <param name="data">The payload, as the registered type its event names; null when the line carried none.</param>
    /// <param name="producer">
    /// Whose journal it came from. Used only as the subject of a <c>leaf.*</c> event, which is about
    /// the component that wrote it and names no subject of its own.
    /// </param>
    public static EventFacts Classify(string eventType, KgsmEventDataBase? data, string producer)
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
            string decided = SubjectOf(data);
            return decided.Length > 0
                ? new EventFacts(cls, ReactorSubjectKind(data), decided)
                : new EventFacts(cls, SubjectKind.Unknown, string.Empty);
        }

        string instance = InstanceOf(data);
        return instance.Length > 0
            ? new EventFacts(cls, SubjectKind.Instance, instance)
            : new EventFacts(cls, SubjectKind.Unknown, string.Empty);
    }

    /// <summary>
    /// The bucket an event type falls in.
    /// </summary>
    /// <remarks>
    /// Prefix matching rather than an exhaustive list of the types the event catalog declares, and
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
    private static SubjectKind ReactorSubjectKind(KgsmEventDataBase? data) =>
        Enum.TryParse(
            SubjectKindOf(data),
            ignoreCase: true,
            out SubjectKind kind)
            ? kind
            : SubjectKind.Unknown;

    /// <summary>
    /// A threshold episode is about one server when it names one, and about the host otherwise.
    /// </summary>
    private static SubjectKind ThresholdSubjectKind(KgsmEventDataBase? data) =>
        ServerIdOf(data).Length > 0 ? SubjectKind.Instance : SubjectKind.Host;

    /// <summary>
    /// Which server, or which measured reference of the host.
    /// </summary>
    /// <remarks>
    /// The reference rather than the metric where there is one: two sensors breaching the same
    /// metric are two episodes, and a subject that collapsed them would make one look like a repeat
    /// of the other — which is exactly the reading a suppression window is derived from.
    /// </remarks>
    private static string ThresholdSubject(KgsmEventDataBase? data)
    {
        string server = ServerIdOf(data);
        if (server.Length > 0)
            return server;

        string reference = RefOf(data);
        return reference.Length > 0 ? reference : MetricOf(data);
    }

    private static string InstanceOf(KgsmEventDataBase? data) => data switch
    {
        EventDataBase instance => instance.InstanceName,
        UnrecognizedEventData unrecognized => unrecognized.InstanceName,
        _ => null,
    } ?? string.Empty;

    private static string ServerIdOf(KgsmEventDataBase? data) => data switch
    {
        HostThresholdEventDataBase threshold => threshold.ServerId,
        UnrecognizedEventData unrecognized => unrecognized.ServerId,
        _ => null,
    } ?? string.Empty;

    private static string RefOf(KgsmEventDataBase? data) => data switch
    {
        HostThresholdEventDataBase threshold => threshold.Ref,
        UnrecognizedEventData unrecognized => unrecognized.Ref,
        _ => null,
    } ?? string.Empty;

    private static string MetricOf(KgsmEventDataBase? data) => data switch
    {
        HostThresholdEventDataBase threshold => threshold.Metric,
        UnrecognizedEventData unrecognized => unrecognized.Metric,
        _ => null,
    } ?? string.Empty;

    private static string SubjectOf(KgsmEventDataBase? data) => data switch
    {
        ReactorEventData reactor => reactor.Subject,
        UnrecognizedEventData unrecognized => unrecognized.Subject,
        _ => null,
    } ?? string.Empty;

    private static string SubjectKindOf(KgsmEventDataBase? data) => data switch
    {
        ReactorDecidedEventData decided => decided.SubjectKind,
        ReactorProposedEventData proposed => proposed.SubjectKind,
        UnrecognizedEventData unrecognized => unrecognized.SubjectKind,
        _ => null,
    } ?? string.Empty;
}
