using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Engine;
using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The gate, and what it records.
/// </summary>
/// <remarks>
/// These are the behaviours that decide whether the observing phase produces data worth tuning
/// against. A gate that silently collapsed "cannot tell" into "settled", or that suppressed a rule
/// against its own open episode, would still look like it was working — the ledger would simply be
/// full of decisions nobody could trust.
/// </remarks>
public class RuleEngineTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-engine-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An event service that replays to whoever registered.</summary>
    private sealed class FakeEvents : IEventService
    {
        private readonly List<Func<EventWrapper, EventPosition, Task>> _raw = [];

        public void Initialize() { }

        public void Initialize(EventStartPosition startPosition) { }

        public void RegisterHandler<T>(Func<T, Task> handler) where T : KgsmEventDataBase { }

        public void RegisterRawHandler(Func<EventWrapper, EventPosition, Task> handler) => _raw.Add(handler);

        public void RegisterGapHandler(Func<EventJournalGap, Task> handler) { }

        public bool HasRawHandler => _raw.Count > 0;

        public async Task EmitAsync(EventWrapper wrapper, EventPosition position)
        {
            foreach (var handler in _raw)
                await handler(wrapper, position);
        }

        public void Dispose() => GC.SuppressFinalize(this);

        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A world that answers whatever the test says, including refusing to answer.</summary>
    private sealed class FakeWorld : IWorldView
    {
        public Reading<InstanceRunState> Answer { get; set; } =
            Reading<InstanceRunState>.Measured(new InstanceRunState("failed", true, 5));

        public Reading<MemoryDeclaration> Declaration { get; set; } =
            Reading<MemoryDeclaration>.Measured(new MemoryDeclaration(2048, 4096, null));

        public ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Answer);

        public ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(
            string instance, CancellationToken token) => ValueTask.FromResult(Declaration);
    }

    /// <summary>A monitor that has measured nothing, which is the ordinary case for these tests.</summary>
    private sealed class EmptyFootprints : IFootprintSource
    {
        public ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token) =>
            ValueTask.FromResult(Reading<IReadOnlyList<InstanceFootprint>>.Measured([]));

        public ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryTrend>.Unavailable("no series"));
    }

    private static EventWrapper Failed(string instance) => new()
    {
        EventType = "server.crash.exhausted",
        Data = JsonDocument.Parse($$"""{"InstanceName":"{{instance}}","ExitCode":"137","Restarts":"5"}""").RootElement,
        Timestamp = Now,
        Actor = "system:watchdog",
        Origin = "system",
    };

    private ObservationLedger OpenLedger()
    {
        var ledger = new ObservationLedger(_path);
        new DecisionStore(ledger).Initialize();
        return ledger;
    }

    /// <summary>
    /// Options pointing at a rules file holding the seeds named, all observing.
    /// </summary>
    /// <remarks>
    /// Written to disk rather than injected, because the file is how a host says which rules it runs
    /// and a test that bypassed it would not be exercising the path the daemon takes.
    /// </remarks>
    private static ReactorOptions Options(
        string observe = "give_up_backup", int suppressionMinutes = 30, int ceiling = 4)
    {
        string rules = Path.Combine(Path.GetTempPath(), $"kgsm-reactor-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(rules, RuleStore.Write(
            observe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => SeededRules.All.Single(r => r.Id == id))));

        return ReactorOptions.FromSettings(new ReactorSettings
        {
            RulesPath = rules,
            SuppressionWindowMinutes = suppressionMinutes,
            MaxActionsPerHour = ceiling,
            LedgerPath = "/unused",
        });
    }

    /// <summary>Options for one rule, edited before it is written to the file.</summary>
    private static ReactorOptions Written(Func<RuleDefinition, RuleDefinition> edit)
    {
        string rules = Path.Combine(Path.GetTempPath(), $"kgsm-reactor-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(rules, RuleStore.Write(
            [edit(SeededRules.All.Single(r => r.Id == "give_up_backup"))]));

        return ReactorOptions.FromSettings(new ReactorSettings
        {
            RulesPath = rules,
            LedgerPath = "/unused",
        });
    }

    /// <summary>Options for one rule somebody signed.</summary>
    private static ReactorOptions Authored(RuleAuthorship created, RuleAuthorship updated) =>
        Written(rule => rule with { Shipped = false, CreatedBy = created, UpdatedBy = updated });

    private static RuleEngine Build(
        FakeEvents events, ObservationLedger ledger, IWorldView world, ReactorOptions options,
        FakeTimeProvider clock, IDecisionEmitter? emitter = null) =>
        new(events, ledger, new DecisionStore(ledger), emitter ?? new RecordingEmitter(), world,
            new LedgerRuleHistory(ledger), new EmptyFootprints(),
            Microsoft.Extensions.Options.Options.Create(options), clock,
            NullLogger<RuleEngine>.Instance);

    /// <summary>An emitter that keeps what it was handed, so a test can ask what was announced.</summary>
    private sealed class RecordingEmitter : IDecisionEmitter
    {
        public List<Decision> Emitted { get; } = [];

        public ValueTask<bool> EmitAsync(Decision decision, CancellationToken token = default)
        {
            Emitted.Add(decision);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static List<Decision> Decisions(ObservationLedger ledger) =>
        [.. new DecisionStore(ledger).Since(DateTimeOffset.MinValue)];

    /// <summary>Wakes a rule and evaluates it, without waiting on a real timer.</summary>
    private static async Task WakeAndSweepAsync(
        RuleEngine engine, FakeEvents events, FakeTimeProvider clock, EventWrapper wrapper,
        EventPosition position, TimeSpan settle)
    {
        await engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => events.HasRawHandler);
        await events.EmitAsync(wrapper, position);
        clock.Advance(settle);
        await engine.SweepAsync(CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // ⚠ BackgroundService.StartAsync returning does not mean ExecuteAsync has begun — a test that
        // emits immediately can reach an engine that has not registered yet.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(5);
        }

        Assert.Fail("the rule engine did not start within 10s");
    }

    [Fact]
    public async Task A_give_up_that_is_still_failed_after_the_settle_window_fires()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(events, ledger, new FakeWorld(), Options(), clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "s.ndjson", 10), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Decision decision = Assert.Single(Decisions(ledger));
        Assert.Equal("give_up_backup", decision.RuleId);
        Assert.Equal("Ketchup", decision.Subject);
        Assert.Equal(DecisionOutcome.Fired, decision.Outcome);
        // Nothing is dispatched in this build, and the record says so rather than leaving it inferred.
        Assert.Equal(ActionState.None, decision.ActionState);
        Assert.Equal(RuleMode.Observe, decision.Mode);
        Assert.Contains("pinned backup", decision.Action);
    }

    /// <summary>
    /// ⚠ A decision names the person who shaped the rule, beside the rule that made it.
    /// </summary>
    /// <remarks>
    /// A rule anybody can create is a rule anybody can get wrong, so a rogue one has to be traceable.
    /// It is <b>copied onto the decision</b> rather than looked up later: resolving it through the
    /// store at read time would mean editing a rule silently rewrites the attribution of everything it
    /// ever decided, and retiring one would erase the trace entirely.
    /// </remarks>
    [Fact]
    public async Task A_decision_carries_who_last_shaped_the_rule_that_made_it()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();

        var engine = Build(
            events, ledger, new FakeWorld(),
            Authored(
                new RuleAuthorship("discord:tanya", Now.AddDays(-9)),
                // The last hand on it, which is the attribution a decision is stamped with.
                new RuleAuthorship("local:claude", Now.AddDays(-1))),
            clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "s.ndjson", 10), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Assert.Equal("local:claude", Assert.Single(Decisions(ledger)).RuleAuthor);
    }

    /// <summary>
    /// ⚠ A rule nobody signed produces a decision nobody signed.
    /// </summary>
    /// <remarks>
    /// There is no fallback to the OS user anywhere in this ecosystem, and a rule this build seeded is
    /// exactly the case that would tempt one. Null is the honest answer, and a surface renders its
    /// absence rather than substituting the host or the person reading.
    /// </remarks>
    [Fact]
    public async Task A_rule_nobody_signed_leaves_the_decision_unattributed()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(events, ledger, new FakeWorld(), Options(), clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "s.ndjson", 10), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Assert.Null(Assert.Single(Decisions(ledger)).RuleAuthor);
    }

    /// <summary>
    /// ⚠ A rule this build cannot honour the mode of runs at the mode it can, and says so.
    /// </summary>
    /// <remarks>
    /// Being silently observed after asking to act is the failure the whole mode ladder exists to make
    /// impossible to miss — the decision records what was actually in force, never what was asked for.
    /// </remarks>
    [Fact]
    public async Task A_rule_asking_to_act_is_recorded_as_having_observed()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();

        var engine = Build(
            events, ledger, new FakeWorld(), Written(r => r with { Mode = RuleMode.Act }), clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "s.ndjson", 10), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Assert.Equal(RuleMode.Observe, Assert.Single(Decisions(ledger)).Mode);
    }

    /// <summary>
    /// ⚠ A rule that is off is never evaluated, which is a different state from being retired.
    /// </summary>
    /// <remarks>
    /// It stays in the store, listed and one field from running again — somebody silenced it while
    /// they work out whether it is right. A retired rule is gone from the live list and kept only so
    /// its old decisions still resolve to a name.
    /// </remarks>
    [Fact]
    public async Task A_rule_that_is_off_is_never_evaluated()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();

        var engine = Build(
            events, ledger, new FakeWorld(), Written(r => r with { Mode = RuleMode.Off }), clock);

        await engine.StartAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        Assert.Empty(engine.Active);
        // Still a rule this host holds: muted is not deleted.
        Assert.Equal("give_up_backup", Assert.Single(engine.Rules.Rules).Id);
        Assert.Empty(Decisions(ledger));
    }

    /// <summary>A retired rule is kept for its record and is not among the rules that run.</summary>
    [Fact]
    public async Task A_retired_rule_is_kept_out_of_the_live_list_and_still_resolvable()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();

        var engine = Build(
            events, ledger, new FakeWorld(), Written(r => r with { Retired = true }), clock);

        await engine.StartAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        Assert.Empty(engine.Active);
        Assert.Empty(engine.Rules.Rules);
        Assert.Equal("give_up_backup", Assert.Single(engine.Rules.Retired).Id);
    }

    [Fact]
    public async Task A_failure_the_operator_already_restarted_settles_instead_of_firing()
    {
        // The whole point of the settle window: somebody saw the alert and acted, so there is nothing
        // to decide — and a backup taken over an instance coming back up would be a hot archive where
        // a cold one was intended.
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var world = new FakeWorld
        {
            Answer = Reading<InstanceRunState>.Measured(new InstanceRunState("running", true, 0)),
        };
        var engine = Build(events, ledger, world, Options(), clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "s.ndjson", 10), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Decision decision = Assert.Single(Decisions(ledger));
        Assert.Equal(DecisionOutcome.Settled, decision.Outcome);
        Assert.Contains("no longer given up on", decision.Reason);
    }

    [Fact]
    public async Task An_unreadable_world_is_recorded_as_unreadable_and_never_as_settled()
    {
        // The distinction invariant 5 exists for. Collapsed into Settled, a supervisor that could not
        // be reached would read back as a condition that resolved itself — silence dressed as a
        // decision, and indistinguishable from it forever after.
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var world = new FakeWorld
        {
            Answer = Reading<InstanceRunState>.Unavailable("the supervisor could not be reached"),
        };
        var engine = Build(events, ledger, world, Options(), clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "s.ndjson", 10), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Decision decision = Assert.Single(Decisions(ledger));
        Assert.Equal(DecisionOutcome.Unreadable, decision.Outcome);
        Assert.Contains("could not be read", decision.Reason);
    }

    [Fact]
    public async Task A_second_failure_inside_the_window_is_suppressed()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(events, ledger, new FakeWorld(), Options(suppressionMinutes: 30), clock);

        await engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => events.HasRawHandler);

        await events.EmitAsync(Failed("Ketchup"), new EventPosition("kgsm-watchdog", "s.ndjson", 10));
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);

        // A different episode — a second give-up ten minutes later, at a different journal position.
        clock.Advance(TimeSpan.FromMinutes(10));
        await events.EmitAsync(Failed("Ketchup"), new EventPosition("kgsm-watchdog", "s.ndjson", 900));
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        List<Decision> decisions = Decisions(ledger);
        Assert.Equal(2, decisions.Count);
        Assert.Equal(1, decisions.Count(d => d.Outcome == DecisionOutcome.Fired));
        Decision suppressed = Assert.Single(decisions, d => d.Outcome == DecisionOutcome.Suppressed);

        // give_up_backup carries its own 15m window, measured from how often a give-up repeats on one
        // subject, so the host-wide 30m configured above is not what stopped this one.
        Assert.Contains("inside the 15m window", suppressed.Reason);
    }

    /// <summary>
    /// A rule's own window overrides the host-wide one, in the direction that lets it speak sooner.
    /// </summary>
    /// <remarks>
    /// The measurement is per rule by three orders of magnitude — 25 seconds between repeat crashes,
    /// four hours between repeat threshold breaches — so a single host-wide figure is necessarily
    /// wrong for something. This is the half that would otherwise go unnoticed: a rule whose window is
    /// SHORTER than the host's stays silent past its own window if the override is ignored, and a
    /// suppressed decision looks like a considered one.
    /// </remarks>
    [Fact]
    public async Task A_rules_own_window_is_what_governs_it()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();

        // Host-wide window far longer than give_up_backup's own 15m.
        var engine = Build(events, ledger, new FakeWorld(), Options(suppressionMinutes: 240), clock);

        await engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => events.HasRawHandler);

        await events.EmitAsync(Failed("Ketchup"), new EventPosition("kgsm-watchdog", "s.ndjson", 10));
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);

        // A second episode past the rule's 15m but well inside the host's 240m.
        clock.Advance(TimeSpan.FromMinutes(20));
        await events.EmitAsync(Failed("Ketchup"), new EventPosition("kgsm-watchdog", "s.ndjson", 900));
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        List<Decision> decisions = Decisions(ledger);
        Assert.Equal(2, decisions.Count);
        Assert.Equal(2, decisions.Count(d => d.Outcome == DecisionOutcome.Fired));
        Assert.DoesNotContain(decisions, d => d.Outcome == DecisionOutcome.Suppressed);
    }

    [Fact]
    public async Task A_rule_does_not_suppress_itself_against_its_own_open_episode()
    {
        // The window exists to stop the NEXT occurrence being announced as news, not to stop this one
        // being refined. Keyed without excluding the episode, the second sweep over one still-open
        // condition would suppress the decision that was already standing.
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(events, ledger, new FakeWorld(), Options(suppressionMinutes: 30), clock);

        await engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => events.HasRawHandler);

        await events.EmitAsync(Failed("Ketchup"), new EventPosition("kgsm-watchdog", "s.ndjson", 10));
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);

        // Same position, so the same episode: re-woken and re-evaluated.
        await events.EmitAsync(Failed("Ketchup"), new EventPosition("kgsm-watchdog", "s.ndjson", 10));
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        // One decision, still fired — refined rather than duplicated or suppressed.
        Decision decision = Assert.Single(Decisions(ledger));
        Assert.Equal(DecisionOutcome.Fired, decision.Outcome);
    }

    [Fact]
    public async Task The_host_ceiling_stops_a_storm()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        // No suppression, so the ceiling is the only thing that can stop the second decision.
        var engine = Build(events, ledger, new FakeWorld(), Options(suppressionMinutes: 0, ceiling: 1), clock);

        await engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => events.HasRawHandler);

        foreach ((string instance, long offset) in new[] { ("alpha", 10L), ("beta", 900L) })
        {
            await events.EmitAsync(Failed(instance), new EventPosition("kgsm-watchdog", "s.ndjson", offset));
            clock.Advance(TimeSpan.FromMinutes(2));
            await engine.SweepAsync(CancellationToken.None);
        }

        await engine.StopAsync(CancellationToken.None);

        List<Decision> decisions = Decisions(ledger);
        Assert.Equal(2, decisions.Count);
        Assert.Equal(1, decisions.Count(d => d.Outcome == DecisionOutcome.Fired));
        Decision ceilinged = Assert.Single(decisions, d => d.Outcome == DecisionOutcome.Ceilinged);
        Assert.Contains("ceiling of 1", ceilinged.Reason);
    }

    [Fact]
    public async Task An_additive_action_is_never_superseded()
    {
        // The carve-out. A regression wants the broken state preserved AND the rollback offered, so a
        // backup must not lose to the proposal beside it — both rules key on the same failure at the
        // same journal position, which is the same episode.
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(
            events, ledger, new FakeWorld(),
            Options(observe: "give_up_backup,update_regression", suppressionMinutes: 0), clock);

        await engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => events.HasRawHandler);

        await events.EmitAsync(Failed("Ketchup"), new EventPosition("kgsm-watchdog", "s.ndjson", 10));
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        List<Decision> decisions = Decisions(ledger);
        Decision backup = Assert.Single(decisions, d => d.RuleId == "give_up_backup");
        Assert.Equal(DecisionOutcome.Fired, backup.Outcome);

        // update_regression does not hold here — no update preceded this failure — so it settles
        // rather than competing. What matters is that the backup fired regardless.
        Decision regression = Assert.Single(decisions, d => d.RuleId == "update_regression");
        Assert.Equal(DecisionOutcome.Settled, regression.Outcome);
    }

    [Fact]
    public async Task A_rule_that_is_enabled_nowhere_never_runs()
    {
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(events, ledger, new FakeWorld(), Options(observe: "threshold_stuck"), clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "s.ndjson", 10), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Assert.Empty(Decisions(ledger));
    }

    [Fact]
    public async Task The_decision_carries_the_journal_line_it_came_from()
    {
        // Invariant 1 as a column: a decision is derived, and anything reading one later must be able
        // to go and read the line it was made from rather than trust this leaf's description of it.
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(events, ledger, new FakeWorld(), Options(), clock);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"),
            new EventPosition("kgsm-watchdog", "2026-08-18.ndjson", 4096), TimeSpan.FromMinutes(2));
        await engine.StopAsync(CancellationToken.None);

        Decision decision = Assert.Single(Decisions(ledger));
        Assert.Equal("kgsm-watchdog", decision.Source.Producer);
        Assert.Equal("2026-08-18.ndjson", decision.Source.Segment);
        Assert.Equal(4096, decision.Source.Offset);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (string file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { /* a temp file the OS still holds */ }
        }
    }

    [Fact]
    public async Task A_decision_is_announced_once_however_often_its_episode_is_re_read()
    {
        // End to end through the engine, because the ledger folding a re-evaluation and the engine
        // deciding not to announce it are two separate pieces and only one of them is exercised by the
        // store's own tests. The same event at the same journal position is one episode: judged again,
        // reaching the same verdict, and worth saying exactly once.
        var events = new FakeEvents();
        var clock = new FakeTimeProvider(Now);
        var emitter = new RecordingEmitter();
        using ObservationLedger ledger = OpenLedger();
        var engine = Build(events, ledger, new FakeWorld(), Options(), clock, emitter);
        var position = new EventPosition("kgsm-watchdog", "s.ndjson", 10);

        await WakeAndSweepAsync(
            engine, events, clock, Failed("Ketchup"), position, TimeSpan.FromMinutes(2));

        await events.EmitAsync(Failed("Ketchup"), position);
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SweepAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);

        Assert.Equal(2, engine.Recorded);
        Assert.Equal(1, engine.Emitted);

        Decision announced = Assert.Single(emitter.Emitted);
        Assert.Equal("give_up_backup", announced.RuleId);
        Assert.Equal(SubjectKind.Instance, announced.SubjectKind);
        Assert.Equal("create_backup", announced.ActionName);
        Assert.Equal("Ketchup", announced.ActionInstance);
    }

}
