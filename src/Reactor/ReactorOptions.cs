using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    /// The host-wide fallback for how long a rule stays quiet about one subject after firing.
    /// </summary>
    /// <remarks>
    /// Measured from the spacing between repeat events for one subject: a crash repeats every 25
    /// seconds at p50, and this covers it comfortably. A rule whose own waking event repeats on a
    /// different scale names its own window instead — see <c>Rule.Suppression</c>.
    /// </remarks>
    public const int DefaultSuppressionWindowMinutes = 30;

    /// <summary>The most decisions that may fire host-wide in a rolling hour.</summary>
    /// <remarks>
    /// <para>
    /// Counted in <em>decisions</em>, not events, and the two differ by a lot. The busiest hour in 30
    /// days held 36 events a rule wakes on — but across only <b>4 distinct subjects</b>, because most
    /// of it was one server crash-looping, and suppression collapses that to one decision. The daily
    /// worst is 7 subjects.
    /// </para>
    /// <para>
    /// So this covers the whole fleet failing at once with room for host sensors alongside it, at
    /// roughly three times the worst hour actually observed. Set at the observed figure instead, a
    /// host that lost every server would go quiet after the fourth — which is the story the ceiling
    /// most needs to let through, not the one it should cut off. Zero disables it.
    /// </para>
    /// </remarks>
    public const int DefaultMaxActionsPerHour = 12;

    /// <summary>The environment variable systemd exports from <c>StateDirectory=</c>.</summary>
    private const string StateDirectoryVariable = "STATE_DIRECTORY";

    /// <summary>Where the ledger goes when nothing names it and systemd has not exported one.</summary>
    private const string FallbackStateDirectory = "/var/lib/kgsm-reactor";

    /// <summary>The ledger's filename inside whichever state directory holds it.</summary>
    private const string LedgerFileName = "reactor.db";

    /// <summary>The rules file's name inside whichever state directory holds it.</summary>
    private const string RulesFileName = "rules.json";

    public required bool Enabled { get; init; }
    public required string KgsmPath { get; init; }
    public required string JournalDir { get; init; }

    /// <summary>Where producer state directories are discovered, or null for the library default.</summary>
    public required string? StateRoot { get; init; }

    public required string LedgerPath { get; init; }
    public required int RetentionDays { get; init; }
    public required int FlushIntervalSeconds { get; init; }
    public required string WatchdogSocketPath { get; init; }

    /// <summary>The metrics socket kgsm-monitor serves on.</summary>
    public required string MonitorSocketPath { get; init; }

    /// <summary>Where the status endpoint listens. Blank means it does not listen at all.</summary>
    public required string StatusSocketPath { get; init; }

    /// <summary>Permission bits applied to the status socket once it exists.</summary>
    public required UnixFileMode StatusSocketMode { get; init; }
    public required int SweepIntervalSeconds { get; init; }
    public required int SuppressionWindowMinutes { get; init; }
    public required int MaxActionsPerHour { get; init; }

    /// <summary>
    /// Where the rules this host runs are read from.
    /// </summary>
    /// <remarks>
    /// Absent is the ordinary case: the host then runs the rules this build seeds, every one of them
    /// observing, which is the state a rule has to earn its way out of.
    /// </remarks>
    public required string RulesPath { get; init; }

    /// <summary>The status socket's mode when nothing configures one: owner and group, read/write.</summary>
    private const UnixFileMode DefaultSocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// Read permission bits written as octal text.
    /// </summary>
    /// <remarks>
    /// Anything unparseable falls back to the default rather than throwing or widening. A typo in a
    /// permission string must not be able to open a socket wider than it was meant to be, and a
    /// daemon that refused to start over one would be a worse answer than one that started safe and
    /// said so.
    /// </remarks>
    private static UnixFileMode ParseMode(string? octal) =>
        !Blank(octal) && TryOctal(octal.Trim(), out int bits)
            ? (UnixFileMode)bits
            : DefaultSocketMode;

    private static bool TryOctal(string text, out int value)
    {
        value = 0;
        try
        {
            value = Convert.ToInt32(text, 8);
            return value is > 0 and <= 0b111_111_111;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

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
            LedgerPath = ResolveStatePath(settings.LedgerPath, LedgerFileName),
            RetentionDays = AtLeast(settings.RetentionDays ?? DefaultRetentionDays, MinRetentionDays),
            FlushIntervalSeconds =
                AtLeast(settings.FlushIntervalSeconds ?? DefaultFlushIntervalSeconds, MinFlushIntervalSeconds),
            MonitorSocketPath = Blank(settings.MonitorSocketPath)
                ? "/run/kgsm-monitor/metrics.sock"
                : settings.MonitorSocketPath.Trim(),
            WatchdogSocketPath = Blank(settings.WatchdogSocketPath)
                ? "/run/kgsm-watchdog/control.sock"
                : settings.WatchdogSocketPath.Trim(),
            StatusSocketPath = Blank(settings.StatusSocketPath)
                ? string.Empty
                : settings.StatusSocketPath.Trim(),
            StatusSocketMode = ParseMode(settings.StatusSocketMode),
            SweepIntervalSeconds =
                AtLeast(settings.SweepIntervalSeconds ?? DefaultSweepIntervalSeconds, MinSweepIntervalSeconds),
            SuppressionWindowMinutes =
                AtLeast(settings.SuppressionWindowMinutes ?? DefaultSuppressionWindowMinutes, 0),
            MaxActionsPerHour = AtLeast(settings.MaxActionsPerHour ?? DefaultMaxActionsPerHour, 0),
            RulesPath = ResolveStatePath(settings.RulesPath, RulesFileName),
        };
    }

    /// <summary>
    /// Where a state file lives: what was configured, else the directory systemd made for this service.
    /// </summary>
    /// <remarks>
    /// <c>$STATE_DIRECTORY</c> rather than a hard-coded path, because the unit declares
    /// <c>StateDirectory=kgsm-reactor</c> and systemd creates and owns that directory before
    /// <c>ExecStart</c>. Reading it back is what keeps the daemon working when the unit's user, or
    /// the state root, is not the one this file would have guessed.
    /// </remarks>
    private static string ResolveStatePath(string configured, string fileName)
    {
        if (!Blank(configured))
            return configured.Trim();

        string state = Environment.GetEnvironmentVariable(StateDirectoryVariable) is { } exported
                       && !Blank(exported)
            // systemd may export several, colon-separated. The first is this unit's own.
            ? exported.Split(':', StringSplitOptions.RemoveEmptyEntries)[0]
            : FallbackStateDirectory;

        return Path.Combine(state, fileName);
    }

    /// <remarks>
    /// The attribute is what lets every caller dereference the argument in the false branch without a
    /// null-forgiving operator — which is otherwise sprinkled through this file and silences real
    /// warnings along with the noise.
    /// </remarks>
    private static bool Blank([NotNullWhen(false)] string? value) => string.IsNullOrWhiteSpace(value);

    private static int AtLeast(int value, int floor) => value < floor ? floor : value;
}
