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
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
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

    /// <summary>
    /// How many days the decision review covers when the caller names none.
    /// </summary>
    /// <remarks>
    /// A week, because that is the span the plan's review gate is stated over — nothing moves to
    /// propose or act until a week of decisions has been read against what a person would have done.
    /// The default answering exactly that question is what makes the gate the easy thing to perform.
    /// </remarks>
    private const int DefaultReviewDays = 7;

    /// <summary>How many decisions the review carries when the caller names no limit.</summary>
    private const int DefaultReviewLimit = 200;

    /// <summary>
    /// The most decisions one review will carry.
    /// </summary>
    /// <remarks>
    /// A bound on the response rather than on the reading: every figure in the review is computed
    /// over the whole window regardless, and only the log at the end is trimmed. What this protects
    /// is the socket — a busy month is thousands of rows, and serialising all of them to answer
    /// "what has it been deciding" would make a status surface the most expensive thing on the host.
    /// </remarks>
    private const int MaxReviewLimit = 1000;

    private static async Task<int> Main(string[] args)
    {
        // The report reads the ledger and nothing else: no host, no journals, no engine. Handled
        // before the host is built so it works on a machine where the daemon is not running, which is
        // most of the times somebody wants to read it.
        if (args.Length > 0 && args[0] is "--report" or "report")
            return Report(args, ReportKind.Population);

        // The review the plan gates propose and act mode behind. A mode of the binary for the same
        // reason the population report is: it is read by a person deciding whether the reactor's
        // judgment is sound, and a gate whose only tooling is a hand-written SQL session is a gate
        // that becomes a formality.
        if (args.Length > 0 && args[0] is "--decisions" or "decisions")
            return Report(args, ReportKind.Decisions);

        // The reactor tails, so it knows only what has happened since it started; the journals are
        // older than it is. This reads them into the ledger so the population report describes weeks
        // of this host rather than the hours since the last deploy.
        if (args.Length > 0 && args[0] is "--backfill" or "backfill")
            return Backfill(args);

        // Checks that every stored position still names the event it was stored for. Its own mode
        // rather than a flag on --backfill: it writes nothing, and a read-only check should not share
        // an entry point with the one operation here that fills a database.
        if (args.Length > 0 && args[0] is "--verify" or "verify")
            return Verify(args);

        // Asking what the binary does is not a mistake, and answering it with exit 2 sends somebody
        // looking for a problem that does not exist.
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        // Anything else is refused rather than ignored. Without this, a mistyped flag starts a SECOND
        // daemon against the same SQLite ledger — two writers, one of them unsupervised, and no
        // message to say so.
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"unrecognised argument: {args[0]}");
            Console.Error.WriteLine(Usage);
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

        // What each instance has been measured to hold. A leaf reading a sibling leaf, on the terms
        // that makes acceptable: absent, one rule reports "cannot tell" and the reactor is otherwise
        // exactly what it was.
        builder.Services.AddSingleton<IFootprintSource>(sp => new MonitorFootprintSource(
            options.MonitorSocketPath,
            sp.GetRequiredService<ILogger<MonitorFootprintSource>>()));
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

            // What a rule may be MADE of on this build — every signal, subject source, action,
            // operator and outcome, with its prose. A panel renders an editor from this rather than
            // holding a copy of the catalogs, which is what stops it offering a signal a later build
            // dropped or refusing one a later build added.
            //
            // ⚠ Read-only, like everything else here. Publishing what a rule may be made of is not
            // accepting one: composing and storing is the panel's half, which writes the file and
            // restarts the unit through the grant it already holds.
            host.MapGet("/catalog", () =>
                Results.Json(ReactorCatalog.Read(), ReactorCatalogJsonContext.Default.ReactorCatalog));

            // What it MADE of what it saw — the review the gate before any action mode is performed
            // against. The same four readings `--decisions` prints, off the same arithmetic, so a
            // browser and a terminal cannot disagree about the busiest hour a ceiling is set from.
            //
            // ⚠ The window is clamped rather than refused. A caller asking for a year is asking for
            // everything, and the ledger's retention already bounds what everything is — answering
            // the retention span is the true reading, where a 400 would be a smaller surface saying
            // no to a question it can in fact answer.
            host.MapGet("/decisions", (
                ObservationLedger ledger,
                RuleEngine engine,
                TimeProvider clock,
                IOptions<ReactorOptions> reactorOptions,
                int? days,
                int? limit) =>
            {
                int window = Math.Clamp(
                    days ?? DefaultReviewDays, 1, reactorOptions.Value.RetentionDays);

                DecisionReview review = DecisionReview.Read(
                    ledger, window, clock.GetUtcNow(),
                    Math.Clamp(limit ?? DefaultReviewLimit, 1, MaxReviewLimit),
                    // The rules that are meant to be deciding. A retired or muted one is not silent.
                    [.. engine.Rules.Rules.Select(r => r.Id)]);

                return Results.Json(review, DecisionReviewJsonContext.Default.DecisionReview);
            });
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
    private static int Report(string[] args, ReportKind kind)
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
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        // The same resolution the daemon uses, so reading the report on the host needs no arguments:
        // the settings file beside the binary, then the environment, then the state directory.
        if (string.IsNullOrWhiteSpace(ledgerPath))
            ledgerPath = ResolveOptions().LedgerPath;

        if (!File.Exists(ledgerPath))
        {
            Console.Error.WriteLine($"no ledger at '{ledgerPath}'.");
            Console.Error.WriteLine(
                "that is where the reactor records what it observed — either it has never run, or it "
                + "is configured to keep the ledger somewhere else (Reactor__LedgerPath).");
            return 1;
        }

        using var ledger = new ObservationLedger(ledgerPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Console.Write(kind switch
        {
            // Read from the same file the daemon reads, so the terminal and the running reactor cannot
            // disagree about which rules exist — which is the whole point of the silent reading.
            ReportKind.Decisions => DecisionReport.Render(
                ledger, days, now,
                [.. RuleStore.Load(ResolveOptions().RulesPath).Rules.Select(r => r.Id)]),
            _ => PopulationReport.Render(ledger, days, now),
        });

        return 0;
    }

    /// <summary>
    /// Reads journal history into the ledger and prints what it did.
    /// </summary>
    /// <remarks>
    /// A mode of the binary rather than a script beside it, for one reason that matters: it classifies
    /// through the same <see cref="Classification.EventClassifier"/> the daemon uses. A second copy of
    /// that logic is how two readers of the same journal come to disagree about what a line meant, and
    /// the disagreement would be invisible — both would look like they had read the host correctly.
    /// </remarks>
    private static int Backfill(string[] args)
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
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        ReactorOptions options = ResolveOptions();
        ledgerPath = string.IsNullOrWhiteSpace(ledgerPath) ? options.LedgerPath : ledgerPath;

        IReadOnlyList<string> directories =
            Ingest.JournalBackfill.Discover(options.StateRoot ?? "/var/lib", options.JournalDir);

        if (directories.Count == 0)
        {
            Console.Error.WriteLine("no journal directories found — nothing to read.");
            return 1;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Console.WriteLine($"reading {directories.Count} journal(s) into {ledgerPath}");

        // The ledger is created if it is not there: unlike the report, this is a WRITE mode, and a
        // host that has never run the daemon is exactly where a backfill is most useful.
        using var ledger = new ObservationLedger(ledgerPath);

        Ingest.JournalBackfill.BackfillResult result = Ingest.JournalBackfill.Run(
            ledger,
            directories,
            notBefore: now.AddDays(-days),
            retentionDays: options.RetentionDays,
            now: now);

        Console.WriteLine();
        Console.WriteLine($"   {result.Files,6}  segment(s) read");
        Console.WriteLine($"   {result.Lines,6}  line(s)");
        Console.WriteLine($"   {result.Inserted,6}  new observation(s)");
        Console.WriteLine($"   {result.Skipped,6}  already held (a position is only recorded once)");

        if (result.Unreadable > 0)
            Console.WriteLine($"   {result.Unreadable,6}  line(s) that were not an event envelope");

        if (result.Earliest is { } earliest)
            Console.WriteLine($"   earliest event now on record: {earliest:yyyy-MM-dd HH:mm:ss} UTC");

        if (result.BeyondRetention > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"⚠  {result.BeyondRetention} of these are older than the {options.RetentionDays}-day "
                + "retention window and");
            Console.WriteLine(
                "   the next prune will remove them. Raise Reactor__RetentionDays before reading a");
            Console.WriteLine(
                "   report that depends on them, or the window will close under the reading.");
        }

        Console.WriteLine();
        Console.WriteLine("Observations only — no rule was evaluated and no event was written.");

        return 0;
    }

    /// <summary>
    /// Checks stored positions against the journals and prints what has drifted.
    /// </summary>
    /// <remarks>
    /// Exits non-zero when it finds drift, so a host can run this on a timer and be told rather than
    /// have to read it. ⚠ A rewritten segment is the cause it exists for, and the readings taken from
    /// a drifted ledger are wrong in a way nothing else on the host reports.
    /// </remarks>
    private static int Verify(string[] args)
    {
        string? ledgerPath = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ledger" when i + 1 < args.Length:
                    ledgerPath = args[i + 1];
                    i++;
                    break;
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        ReactorOptions options = ResolveOptions();
        ledgerPath = string.IsNullOrWhiteSpace(ledgerPath) ? options.LedgerPath : ledgerPath;

        if (!File.Exists(ledgerPath))
        {
            Console.Error.WriteLine($"no ledger at '{ledgerPath}'.");
            return 1;
        }

        using var ledger = new ObservationLedger(ledgerPath);

        Ingest.JournalVerify.VerifyResult result = Ingest.JournalVerify.Run(
            ledger, options.StateRoot ?? "/var/lib", options.JournalDir);

        Console.WriteLine($"kgsm-reactor — verifying {result.Checked} stored position(s) in {ledgerPath}");
        Console.WriteLine();
        Console.WriteLine($"   {result.Intact,6}  still name the event they were stored for");
        Console.WriteLine($"   {result.Drifted.Count,6}  do not");

        if (result.SegmentsMissing > 0)
        {
            Console.WriteLine(
                $"   {result.SegmentsMissing,6}  in a segment that is gone (retention — not drift)");
        }

        if (!result.FoundDrift)
        {
            Console.WriteLine();
            Console.WriteLine("Every position resolves. Nothing has rewritten a segment under the ledger.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("── Drifted ─────────────────────────────────────────────────────────");
        Console.WriteLine("A segment has been rewritten. Deleting one line shifts every byte after");
        Console.WriteLine("it, and a stored position then names whatever now sits at that offset.");
        Console.WriteLine();

        foreach (Ingest.JournalVerify.Drift drift in result.Drifted.Take(50))
        {
            Console.WriteLine(
                $"   {drift.Producer}/{drift.Segment}@{drift.Offset}");
            // Only the wrong-event case has two things worth saying — what was expected and what is
            // actually there. The others describe themselves, and repeating the description as a
            // suffix reads as though the tool found two separate problems.
            Console.WriteLine(drift.State == Ingest.JournalVerify.PositionState.WrongEvent
                ? $"      stored as {drift.Expected} — now {drift.Found}  ⚠ a valid line, and the wrong one"
                : $"      stored as {drift.Expected} — {Describe(drift.State)}");
        }

        if (result.Drifted.Count > 50)
            Console.WriteLine($"   … and {result.Drifted.Count - 50} more");

        Console.WriteLine();
        Console.WriteLine("⚠  Readings taken from this ledger are wrong, and a re-run of --backfill");
        Console.WriteLine("   would ADD the shifted lines rather than recognise them — the same event");
        Console.WriteLine("   at a new offset is a new row. The ledger is derived, so the remedy is to");
        Console.WriteLine("   delete it and run --backfill once against the journals as they now are.");

        return 1;
    }

    private static string Describe(Ingest.JournalVerify.PositionState state) => state switch
    {
        Ingest.JournalVerify.PositionState.WrongEvent => "a valid line, and the wrong one",
        Ingest.JournalVerify.PositionState.MidLine => "no longer the start of a line",
        Ingest.JournalVerify.PositionState.PastEnd => "past the end of the segment",
        Ingest.JournalVerify.PositionState.SegmentMissing => "segment gone",
        _ => state.ToString(),
    };

    /// <summary>The daemon's own configuration, resolved the way the daemon resolves it.</summary>
    /// <remarks>
    /// Shared by the read-only modes so running one on the host needs no arguments: the settings file
    /// beside the binary, then the environment, then the state directory.
    /// </remarks>
    private static ReactorOptions ResolveOptions()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "kgsm-reactor.settings.json"),
                optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        ReactorSettings settings =
            configuration.GetSection(ReactorSettings.Section).Get<ReactorSettings>()
            ?? new ReactorSettings();

        return ReactorOptions.FromSettings(settings);
    }

    /// <summary>Which of the two readings the binary was asked for.</summary>
    private enum ReportKind
    {
        /// <summary>What this host does — the input to the rule table.</summary>
        Population,

        /// <summary>What the reactor made of it — the input to the review gate.</summary>
        Decisions,
    }

    private const string Usage =
        """
        usage: kgsm-reactor --report    [--days N] [--ledger PATH]   what this host does
               kgsm-reactor --decisions [--days N] [--ledger PATH]   what the reactor made of it
               kgsm-reactor --backfill  [--days N] [--ledger PATH]   read journal history into the ledger
               kgsm-reactor --verify              [--ledger PATH]   check stored positions still resolve

        --backfill fills OBSERVATIONS only. It evaluates no rule and writes no event: an
        observation restates a line that exists, where a decision is a judgment made against a
        world that answered at the time, and that world is gone.
        """;
}
