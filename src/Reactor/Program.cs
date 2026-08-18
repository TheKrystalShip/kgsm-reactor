using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Engine;
using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ingest;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Reporting;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Status;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Kgsm.Reactor;

internal sealed class Program
{
    /// <summary>How many days the population report covers when nothing says otherwise.</summary>
    private const int DefaultReportDays = 30;

    private static async Task<int> Main(string[] args)
    {
        // The report reads the ledger and nothing else: no host, no journals, no engine. Handled
        // before the host is built so it works on a machine where the daemon is not running, which is
        // most of the times somebody wants to read it.
        if (args.Length > 0 && args[0] is "--report" or "report")
            return Report(args);

        // Anything else is refused rather than ignored. Without this, a mistyped flag starts a SECOND
        // daemon against the same SQLite ledger — two writers, one of them unsupervised, and no
        // message to say so.
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"unrecognised argument: {args[0]}");
            Console.Error.WriteLine("usage: kgsm-reactor [--report [--days N] [--ledger PATH]]");
            return 2;
        }

        // ContentRootPath is pinned to the binary's own directory rather than left to default to the
        // process working directory. The builder installs its own appsettings.json providers with
        // reloadOnChange:true, which hangs a RECURSIVE FileSystemWatcher off the content root. Rooted
        // at "/", that walk takes an inotify watch per directory and exhausts the per-user
        // fs.inotify.max_user_watches budget the game servers on this host draw from; a game that
        // cannot get a watch fails to boot. AppContext.BaseDirectory is the one directory that is
        // correct no matter where the process was started from.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // The settings file lives beside the binary, which is not necessarily the working directory
        // the unit starts us in, so it is named absolutely. Environment variables are registered after
        // it and therefore win: configuration resolves by source order, and appending the file to the
        // sources the builder already installed puts it ahead of the builder's own environment
        // provider. Without re-registering, the file would outrank every Reactor__* and Logging__*
        // variable and an override would read as applied while changing nothing.
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "kgsm-reactor.settings.json"),
                optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        ReactorSettings settings =
            builder.Configuration.GetSection(ReactorSettings.Section).Get<ReactorSettings>()
            ?? new ReactorSettings();
        ReactorOptions options = ReactorOptions.FromSettings(settings);

        builder.Services.AddSingleton<IOptions<ReactorOptions>>(Options.Create(options));
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Logging.ClearProviders();
        builder.Logging.AddSystemdConsole();

        // The engine, for re-reading the world. Nothing in the observing half calls it; a rule that
        // checks whether a condition still holds does, and it is wired now so that a host where this
        // is misconfigured fails at startup rather than at the moment it matters.
        builder.Services.AddKgsmServices(options.KgsmPath);

        // The supervisor, which owns the answer to "is this actually running". Every rule re-derives
        // from it rather than trusting the event that woke it — an event says what happened, and only
        // a read says what is true now.
        builder.Services.AddKgsmWatchdogClient(options.WatchdogSocketPath);

        // Every producer's journal behind one IEventSource, read from the TAIL with NO cursor.
        //
        // ⚠ Both halves of that are deliberate and neither is a default worth changing casually.
        // Federated, because the events this leaf exists for are not the engine's: the supervisor
        // records the crash and the give-up, the monitor records a threshold episode, the firewall
        // records the port edges. Reading only the engine's journal would be a reactor that cannot
        // see a single fault.
        //
        // Tail with no cursor, because this leaf exists to ACT, and a replayed action is performed
        // again for real. What it costs is events that arrive while the process is down — which are
        // still in the journals that hold them, since an observation is derived and the journal is
        // the record.
        builder.Services.AddKgsmJournalFederation(
            cursorPath: null,
            startPosition: EventStartPosition.Tail,
            engineJournalDirectory: options.JournalDir,
            stateRoot: options.StateRoot);

        // This leaf's own journal. It records nothing about game servers — their own producers own
        // that — only what this leaf saw, decided and did.
        builder.Services.AddKgsmJournal("kgsm-reactor", typeof(Program).Assembly);

        builder.Services.AddSingleton(sp => new LeafLifecycle(
            sp.GetRequiredService<IEventJournalWriter>(),
            sp.GetRequiredService<ILogger<LeafLifecycle>>(),
            clock: null,
            startedAt: () => startedAt));

        // Opened before the host runs, so a ledger that cannot be created fails the start rather than
        // producing a daemon that reports itself up and records nothing.
        builder.Services.AddSingleton(_ => new ObservationLedger(options.LedgerPath));

        // One connection, two tables. The gate's questions cross both — "has this rule fired for this
        // subject lately" is a decisions query and "since when has the condition held" is an
        // observations one — and answering them through two connections is how a reader comes to see a
        // half-written view of one file.
        builder.Services.AddSingleton<DecisionStore>();

        // Decisions go on the journal as well as in the ledger, because the ledger is this leaf's own
        // working state and the journal is what the rest of the host can read.
        builder.Services.AddSingleton<IDecisionEmitter, DecisionEmitter>();
        builder.Services.AddSingleton<IWorldView, WatchdogWorldView>();
        builder.Services.AddSingleton<IRuleHistory, LedgerRuleHistory>();

        // Registered as singletons and then handed to the host, rather than AddHostedService<T>()
        // alone: that registers them only as IHostedService, and the status endpoint has to be able to
        // ask the actual instances what they are holding.
        builder.Services.AddSingleton<EventIngestService>();
        builder.Services.AddSingleton<RuleEngine>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventIngestService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RuleEngine>());

        builder.Services.AddSingleton(sp => new StatusReporter(
            sp.GetRequiredService<EventIngestService>(),
            sp.GetRequiredService<RuleEngine>(),
            sp.GetRequiredService<IOptions<ReactorOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            startedAt));

        // Server to client only, and only over a unix socket — no TCP anywhere. Nothing off this host
        // has any business asking a leaf what it is thinking, so the socket's filesystem permissions
        // are the entire access boundary rather than one layer of several.
        if (options.StatusSocketPath.Length > 0)
        {
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                // A socket file left behind by a killed process would otherwise make the bind fail and
                // take the daemon down over an artefact of the last run.
                if (File.Exists(options.StatusSocketPath))
                    File.Delete(options.StatusSocketPath);

                kestrel.ListenUnixSocket(options.StatusSocketPath);
            });
        }

        WebApplication host = builder.Build();

        if (options.StatusSocketPath.Length > 0)
        {
            // The socket only exists once the host is listening, so the mode is set here rather than
            // before Run — which would be an ENOENT on a file that is not there yet.
            host.Lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    if (OperatingSystem.IsLinux() && File.Exists(options.StatusSocketPath))
                        File.SetUnixFileMode(options.StatusSocketPath, options.StatusSocketMode);
                }
                catch (Exception ex)
                {
                    host.Logger.LogWarning(
                        ex, "could not set mode on {Socket}", options.StatusSocketPath);
                }
            });

            // Liveness. Deliberately not an alias for /status: this answers "the process is up and
            // serving", and a reactor that is up while unable to read its ledger must still be able to
            // say so on /status rather than failing this and looking dead.
            host.MapGet("/health", () => Results.Text("ok\n"));

            // What it is doing right now — the counters, the live rules and their modes, and the
            // evaluations waiting out their settle windows.
            host.MapGet("/status", (StatusReporter reporter) =>
                Results.Json(reporter.Read(), ReactorStatusJsonContext.Default.ReactorStatus));
        }

        // Before anything evaluates. The table is created here rather than lazily so a permission
        // problem on the ledger fails the start, where a first decision hours later would fail
        // quietly and look like a host with nothing to decide about.
        host.Services.GetRequiredService<DecisionStore>().Initialize();

        // The last thing this daemon says. A consumer reading it knows the reactor went away because
        // somebody stopped it, rather than because it died while watching.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            host.Services.GetRequiredService<LeafLifecycle>().MarkStopping(LeafStopReason.Signal));

        if (!File.Exists(options.KgsmPath))
        {
            Console.Error.WriteLine(
                $"[FATAL] kgsm not found at '{options.KgsmPath}'. Set Reactor__KgsmPath.");
            return 1;
        }

        await host.RunAsync();
        return 0;
    }

    /// <summary>
    /// Prints the population report and exits.
    /// </summary>
    /// <remarks>
    /// A mode of the binary rather than a socket or an endpoint, because the reactor serves neither
    /// yet and this is read by a person at a terminal deciding what the rules should be — not by
    /// another service. It opens the same ledger the daemon writes; SQLite in WAL mode is happy with
    /// a reader alongside the writer, so it needs nothing stopped.
    /// </remarks>
    private static int Report(string[] args)
    {
        int days = DefaultReportDays;
        string? ledgerPath = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--days" when i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed):
                    days = Math.Max(1, parsed);
                    i++;
                    break;
                case "--ledger" when i + 1 < args.Length:
                    ledgerPath = args[i + 1];
                    i++;
                    break;
                case "--help" or "-h":
                    Console.WriteLine(
                        "usage: kgsm-reactor --report [--days N] [--ledger PATH]");
                    return 0;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    Console.Error.WriteLine("usage: kgsm-reactor --report [--days N] [--ledger PATH]");
                    return 2;
            }
        }

        // The same resolution the daemon uses, so reading the report on the host needs no arguments:
        // the settings file beside the binary, then the environment, then the state directory.
        if (string.IsNullOrWhiteSpace(ledgerPath))
        {
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "kgsm-reactor.settings.json"),
                    optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            ReactorSettings settings =
                configuration.GetSection(ReactorSettings.Section).Get<ReactorSettings>()
                ?? new ReactorSettings();
            ledgerPath = ReactorOptions.FromSettings(settings).LedgerPath;
        }

        if (!File.Exists(ledgerPath))
        {
            Console.Error.WriteLine($"no ledger at '{ledgerPath}'.");
            Console.Error.WriteLine(
                "that is where the reactor records what it observed — either it has never run, or it "
                + "is configured to keep the ledger somewhere else (Reactor__LedgerPath).");
            return 1;
        }

        using var ledger = new ObservationLedger(ledgerPath);
        Console.Write(PopulationReport.Render(ledger, days, DateTimeOffset.UtcNow));
        return 0;
    }
}
