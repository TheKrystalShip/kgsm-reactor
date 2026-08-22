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

    /// <summary>
    /// The watchdog's control socket, which run state is read from.
    /// </summary>
    /// <remarks>
    /// Every rule re-derives from the live world rather than trusting the event that woke it, and the
    /// supervisor is the authority on whether a native instance is running. Unreachable is reported as
    /// "cannot tell" and stops the evaluation — never as "the condition does not hold".
    /// </remarks>
    /// <panel>The watchdog's control socket. The reactor asks it how a server actually stands before
    /// deciding anything, so this has to match the path the watchdog listens on.</panel>
    [LeafField("watchdogSocket", "Watchdog control socket", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string WatchdogSocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>The metrics socket kgsm-monitor serves on.</summary>
    /// <remarks>
    /// Read for what each instance has been measured to hold, which no event carries and no other
    /// component can answer. The monitor is a leaf: absent or unreachable, the one rule that reads it
    /// reports "cannot tell" and nothing else about this daemon changes.
    /// </remarks>
    /// <panel>The monitor's metrics socket. The reactor reads it for what each server has actually been
    /// measured using, which is what the memory-drift rule compares against. With no monitor on this
    /// host, that rule simply has nothing to read.</panel>
    [LeafField("monitorSocket", "Monitor metrics socket", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string MonitorSocketPath { get; set; } = "/run/kgsm-monitor/metrics.sock";

    /// <summary>
    /// The unix socket the status endpoint listens on.
    /// </summary>
    /// <remarks>
    /// A socket rather than a port: nothing off this host has any business asking a leaf what it is
    /// thinking, and the filesystem permissions are the whole access boundary. It lives in the
    /// systemd runtime directory, so it is created on start and gone on stop — a stale socket file
    /// can never be mistaken for a running reactor.
    /// </remarks>
    /// <panel>Where the reactor answers questions about what it is doing right now. A local socket,
    /// readable by anything in its group; it is never exposed on the network.</panel>
    [LeafField("statusSocket", "Status socket", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string StatusSocketPath { get; set; } = "/run/kgsm-reactor/status.sock";

    /// <summary>
    /// Permission bits set on the socket once it exists, as an octal string.
    /// </summary>
    /// <remarks>
    /// ⚠ Written as text (<c>660</c>) rather than a number, because a leading zero in JSON is not
    /// legal and an unquoted <c>660</c> read as decimal would be a different mode entirely.
    /// </remarks>
    /// <panel>Who may read the status socket, as octal permission bits. The default lets a service in
    /// the same group ask; it is not readable by everyone on the host.</panel>
    [LeafField("statusSocketMode", "Status socket permissions", Group = "wiring")]
    public string StatusSocketMode { get; set; } = "660";

    /// <summary>Rules that evaluate and record, dispatching nothing.</summary>
    /// <remarks>
    /// The rule catalog ships in code; this decides which of it is live. A rule named in none of the
    /// three mode lists is off. ⚠ A rule named in more than one gets the <b>safest</b> of them.
    /// </remarks>
    /// <panel>Which rules are watching. They record what they would have done and change nothing, which
    /// is how a rule earns the right to act.</panel>
    [LeafField("rulesObserve", "Rules in observe", Group = "rules", Type = LeafType.Csv)]
    public string RulesObserve { get; set; } = "give_up_backup,update_regression,threshold_stuck,memory_declaration_drift";

    /// <summary>Rules that stage their action for a human to confirm.</summary>
    /// <remarks>⚠ Unbuilt. A rule named here is clamped to observe, loudly.</remarks>
    /// <panel>Which rules may propose an action for you to approve. Nothing happens without your
    /// confirmation.</panel>
    [LeafField("rulesPropose", "Rules in propose", Group = "rules", Type = LeafType.Csv, NoDefault = true)]
    public string RulesPropose { get; set; } = string.Empty;

    /// <summary>Rules that perform their action.</summary>
    /// <remarks>⚠ Unbuilt. A rule named here is clamped to observe, loudly.</remarks>
    /// <panel>Which rules may act on their own. Only put a rule here once you have read what it decided
    /// while it was only watching.</panel>
    [LeafField("rulesAct", "Rules in act", Group = "rules", Type = LeafType.Csv, Risk = LeafRisk.Wiring,
        NoDefault = true)]
    public string RulesAct { get; set; } = string.Empty;

    /// <summary>How often rules are evaluated, in seconds.</summary>
    /// <panel>How often the reactor re-checks the conditions it is watching.</panel>
    [LeafField("sweepIntervalSec", "Evaluate rules every", Group = "rules",
        Min = ReactorOptions.MinSweepIntervalSeconds, Unit = "s")]
    public int? SweepIntervalSeconds { get; set; }

    /// <summary>How long one rule stays quiet about one subject after firing, in minutes.</summary>
    /// <remarks>
    /// The host-wide fallback, measured from the spacing between repeat events for one subject. A rule
    /// whose waking event repeats on a different scale carries its own window in the rule table and
    /// ignores this.
    /// </remarks>
    /// <panel>How long a rule stays quiet about the same server after it has spoken once. Too short and
    /// you hear the same thing repeatedly; too long and the second occurrence goes unmentioned.</panel>
    [LeafField("suppressionWindowMin", "Stay quiet for", Group = "rules", Min = 0, Unit = "min")]
    public int? SuppressionWindowMinutes { get; set; }

    /// <summary>The most decisions that may fire host-wide in a rolling hour.</summary>
    /// <remarks>
    /// Counted in decisions rather than events: the busiest hour measured held 36 events a rule wakes
    /// on across only 4 subjects, since suppression collapses one server's crash-loop into one
    /// decision. Zero disables the ceiling.
    /// </remarks>
    /// <panel>The most the reactor may decide in an hour, across the whole host. A host that loses
    /// every server at once is one story, and this is what stops it becoming forty.</panel>
    [LeafField("maxDecisionsPerHour", "Decisions per hour", Group = "rules", Min = 0)]
    public int? MaxActionsPerHour { get; set; }
}
