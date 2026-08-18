using TheKrystalShip.Kgsm.Reactor.Rules;

namespace TheKrystalShip.Kgsm.Reactor;

/// <summary>
/// What the daemon actually runs on: <see cref="ReactorSettings"/> after clamping, defaulting and
/// path resolution.
/// </summary>
/// <remarks>
/// Separate from the settings type on purpose. Settings hold what an operator wrote, which may be
/// blank, absent or out of range; this holds a value every consumer can use without re-checking it.
/// A bad number therefore starts the daemon on a sane one and says so, rather than failing a service
/// over a typo in an env file.
/// </remarks>
internal sealed record ReactorOptions
{
    /// <summary>The shortest retention that leaves anything worth measuring. A day of observations
    /// cannot show an inter-arrival distribution or a weekly rhythm.</summary>
    public const int MinRetentionDays = 1;

    /// <summary>The default retention: long enough to hold a month's rhythm, which is the window the
    /// gate's own windows are derived from.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>The shortest commit interval. Below this the batching stops being batching.</summary>
    public const int MinFlushIntervalSeconds = 1;

    /// <summary>The default commit interval.</summary>
    public const int DefaultFlushIntervalSeconds = 5;

    /// <summary>The shortest sweep. Below this the engine spends more time waking than judging.</summary>
    public const int MinSweepIntervalSeconds = 5;

    /// <summary>How often rules are evaluated by default.</summary>
    public const int DefaultSweepIntervalSeconds = 30;

    /// <summary>⚠ PLACEHOLDER — derived from the population report, not chosen.</summary>
    public const int DefaultSuppressionWindowMinutes = 30;

    /// <summary>⚠ PLACEHOLDER — derived from the population report, not chosen.</summary>
    public const int DefaultMaxActionsPerHour = 4;

    /// <summary>The environment variable systemd exports from <c>StateDirectory=</c>.</summary>
    private const string StateDirectoryVariable = "STATE_DIRECTORY";

    /// <summary>Where the ledger goes when nothing names it and systemd has not exported one.</summary>
    private const string FallbackStateDirectory = "/var/lib/kgsm-reactor";

    /// <summary>The ledger's filename inside whichever state directory holds it.</summary>
    private const string LedgerFileName = "reactor.db";

    public required bool Enabled { get; init; }
    public required string KgsmPath { get; init; }
    public required string JournalDir { get; init; }

    /// <summary>Where producer state directories are discovered, or null for the library default.</summary>
    public required string? StateRoot { get; init; }

    public required string LedgerPath { get; init; }
    public required int RetentionDays { get; init; }
    public required int FlushIntervalSeconds { get; init; }
    public required string WatchdogSocketPath { get; init; }
    public required int SweepIntervalSeconds { get; init; }
    public required int SuppressionWindowMinutes { get; init; }
    public required int MaxActionsPerHour { get; init; }

    /// <summary>Rule ids by the mode each was configured in.</summary>
    public required IReadOnlyDictionary<string, Rules.RuleMode> RuleModes { get; init; }

    /// <summary>
    /// The mode a rule runs in, or <see langword="null"/> when it is not enabled at all.
    /// </summary>
    public Rules.RuleMode? ModeFor(string ruleId) =>
        RuleModes.TryGetValue(ruleId, out Rules.RuleMode mode) ? mode : null;

    public static ReactorOptions FromSettings(ReactorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ReactorOptions
        {
            // Observing is the default. A reactor installed and then silently doing nothing would be
            // indistinguishable from one that is broken, and P1 takes no action whatever this says.
            Enabled = settings.Enabled ?? true,
            KgsmPath = Blank(settings.KgsmPath) ? "/usr/bin/kgsm" : settings.KgsmPath.Trim(),
            JournalDir = Blank(settings.JournalDir) ? "/var/lib/kgsm/events" : settings.JournalDir.Trim(),
            // Null, not a literal: the library owns what the default state root is, and repeating it
            // here is how the two come to disagree after one of them moves.
            StateRoot = Blank(settings.StateRoot) ? null : settings.StateRoot.Trim(),
            LedgerPath = ResolveLedgerPath(settings.LedgerPath),
            RetentionDays = AtLeast(settings.RetentionDays ?? DefaultRetentionDays, MinRetentionDays),
            FlushIntervalSeconds =
                AtLeast(settings.FlushIntervalSeconds ?? DefaultFlushIntervalSeconds, MinFlushIntervalSeconds),
            WatchdogSocketPath = Blank(settings.WatchdogSocketPath)
                ? "/run/kgsm-watchdog/control.sock"
                : settings.WatchdogSocketPath.Trim(),
            SweepIntervalSeconds =
                AtLeast(settings.SweepIntervalSeconds ?? DefaultSweepIntervalSeconds, MinSweepIntervalSeconds),
            SuppressionWindowMinutes =
                AtLeast(settings.SuppressionWindowMinutes ?? DefaultSuppressionWindowMinutes, 0),
            MaxActionsPerHour = AtLeast(settings.MaxActionsPerHour ?? DefaultMaxActionsPerHour, 0),
            RuleModes = ResolveModes(settings),
        };
    }

    /// <summary>
    /// Which rules are enabled, and in which mode.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A rule named in more than one list gets the safest of them.</b> Two lists disagreeing is a
    /// configuration mistake, and the only safe way to resolve one is downwards — an operator who
    /// meant to grant more authority will notice that nothing acted, where one who meant to grant less
    /// would not notice that something did.
    /// </remarks>
    private static IReadOnlyDictionary<string, RuleMode> ResolveModes(ReactorSettings settings)
    {
        Dictionary<string, RuleMode> modes = new(StringComparer.OrdinalIgnoreCase);

        // Written most-permissive first, so the safer assignment below always wins on a collision.
        foreach ((string list, RuleMode mode) in new[]
                 {
                     (settings.RulesAct, RuleMode.Act),
                     (settings.RulesPropose, RuleMode.Propose),
                     (settings.RulesObserve, RuleMode.Observe),
                 })
        {
            foreach (string id in Split(list))
                modes[id] = mode;
        }

        return modes;
    }

    private static IEnumerable<string> Split(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Where the ledger lives: what was configured, else the directory systemd made for this service.
    /// </summary>
    /// <remarks>
    /// <c>$STATE_DIRECTORY</c> rather than a hard-coded path, because the unit declares
    /// <c>StateDirectory=kgsm-reactor</c> and systemd creates and owns that directory before
    /// <c>ExecStart</c>. Reading it back is what keeps the daemon working when the unit's user, or
    /// the state root, is not the one this file would have guessed.
    /// </remarks>
    private static string ResolveLedgerPath(string configured)
    {
        if (!Blank(configured))
            return configured.Trim();

        string state = Environment.GetEnvironmentVariable(StateDirectoryVariable) is { } exported
                       && !Blank(exported)
            // systemd may export several, colon-separated. The first is this unit's own.
            ? exported.Split(':', StringSplitOptions.RemoveEmptyEntries)[0]
            : FallbackStateDirectory;

        return Path.Combine(state, LedgerFileName);
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static int AtLeast(int value, int floor) => value < floor ? floor : value;
}
