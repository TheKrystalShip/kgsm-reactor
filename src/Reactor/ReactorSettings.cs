using TheKrystalShip.KGSM.LeafConfig;

namespace TheKrystalShip.Kgsm.Reactor;

/// <summary>
/// The reactor's configuration surface, shaped 1:1 to the <c>Reactor</c> section of
/// <c>kgsm-reactor.settings.json</c>. Every knob the daemon has is a property here and a key there;
/// nothing is read by string lookup, so a knob cannot exist in one place and not the other. An
/// environment variable overrides one key by spelling its path with <c>__</c>
/// (<c>Reactor__RetentionDays</c>).
/// </summary>
/// <remarks>
/// This type holds what was <em>written</em>, not what the daemon runs on: values arrive
/// unvalidated, exactly as the file or the environment spelled them. <see cref="ReactorOptions"/> is
/// the validated form — clamping and fallbacks live in <see cref="ReactorOptions.FromSettings"/>, so
/// the daemon starts with something sane rather than not at all.
/// <para>
/// Numbers are <b>nullable</b>, and null means "not written" — the coded default applies. Two binder
/// behaviours make that load-bearing rather than stylistic: a blank value
/// (<c>Reactor__RetentionDays=</c>, one stray line in an env file) binds to a non-nullable
/// <see cref="int"/> by throwing, taking the daemon down at startup; and a JSON null binds to
/// <c>0</c>, silently discarding the default a property initializer would have carried. Nullable
/// turns both into "unset". A value that is present but is not a number still fails loudly, which is
/// the point of typing it at all.
/// </para>
/// </remarks>
[LeafSection(Section)]
internal sealed class ReactorSettings
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string Section = "Reactor";

    /// <summary>
    /// Whether the reactor observes at all. Off leaves the daemon running and recording nothing.
    /// </summary>
    /// <remarks>
    /// A switch rather than "stop the unit", because a leaf that is stopped and a leaf that is
    /// deliberately quiet look identical from outside — and this one writes <c>leaf_ready</c> either
    /// way, so a host can tell them apart.
    /// </remarks>
    /// <panel>Whether the reactor watches the host's events at all. With this off it keeps running and
    /// records nothing, which is how you silence it without stopping the service.</panel>
    [LeafField("reactorEnabled", "Observe events", Group = "general", Type = LeafType.Bool)]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Path to the KGSM executable. Checked at startup — the daemon refuses to run if nothing is
    /// there.
    /// </summary>
    /// <remarks>
    /// Nothing in the observing half calls it. It is required anyway, because a reactor that cannot
    /// reach the engine cannot re-read the world to see whether a condition still holds, and cannot
    /// act on one — so a host where this is wrong has a reactor that will fail at the moment it
    /// matters rather than at startup.
    /// </remarks>
    /// <panel>Path to the KGSM executable. The reactor re-reads the world through it before deciding
    /// anything, so it is checked at startup and the daemon refuses to run if nothing is there.</panel>
    [LeafField("kgsmPath", "KGSM executable", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string KgsmPath { get; set; } = "/usr/bin/kgsm";

    /// <summary>Where the engine's own event journal lives.</summary>
    /// <remarks>
    /// The engine is the one producer whose journal is not found by scanning state directories, so it
    /// is named. Every other producer is discovered.
    /// </remarks>
    /// <panel>Where the KGSM engine writes its event journal. Every other component's journal is found
    /// automatically; the engine's is the one that has to be named.</panel>
    [LeafField("journalDir", "Engine event journal", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string JournalDir { get; set; } = "/var/lib/kgsm/events";

    /// <summary>
    /// Where each producer's state directory lives, each holding its journal in an <c>events</c>
    /// subdirectory. Blank uses the ecosystem default.
    /// </summary>
    /// <panel>Where the host keeps each KGSM component's state directory. The reactor scans it to find
    /// every component's event journal, so pointing it elsewhere makes the reactor deaf to everything
    /// except the engine.</panel>
    [LeafField("stateRoot", "Component state root", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string StateRoot { get; set; } = string.Empty;

    /// <summary>
    /// The observation ledger. Blank resolves to <c>reactor.db</c> in the systemd state directory,
    /// which is where it belongs on a real host.
    /// </summary>
    /// <panel>The database file the reactor records what it saw in. Leave it blank to keep it in this
    /// service's own state directory, which is what a normal install wants.</panel>
    [LeafField("ledgerPath", "Observation ledger", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string LedgerPath { get; set; } = string.Empty;

    /// <summary>How long an observation is kept, in days.</summary>
    /// <remarks>
    /// The ledger is <b>derived</b> — every row restates something a producer's journal already holds
    /// — so pruning it loses no record. What it costs is the window the gate's own windows can be
    /// measured over.
    /// </remarks>
    /// <panel>How long the reactor keeps what it observed. This is working data, not a record: every
    /// row restates something a component's own journal already holds, so shortening it loses nothing
    /// except how far back the reactor can measure its own thresholds.</panel>
    [LeafField("retentionDays", "Keep observations for", Group = "retention",
        Min = ReactorOptions.MinRetentionDays, Unit = "days")]
    public int? RetentionDays { get; set; }

    /// <summary>How often buffered observations are committed, in seconds.</summary>
    /// <remarks>
    /// Observations are batched rather than written one transaction per event: a busy evening on a
    /// popular server is hundreds of player events an hour, and the reactor must not be the reason
    /// the host's disk is busy. The window bounds how much is lost if the process is killed —
    /// nothing that matters, since the journals are the record and a restart re-reads from the tail.
    /// </remarks>
    /// <panel>How often the reactor commits what it has seen. Longer means fewer, larger writes; the
    /// only thing at risk is the last few seconds of working data, never a record.</panel>
    [LeafField("flushIntervalSec", "Commit observations every", Group = "retention",
        Min = ReactorOptions.MinFlushIntervalSeconds, Unit = "s")]
    public int? FlushIntervalSeconds { get; set; }
}
