using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Actions;
using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// What an offer is, and what redeeming one does.
/// </summary>
/// <remarks>
/// <para>
/// <b>The acceptance test for a lifetime measured in hours.</b> Everything here turns on one property:
/// the condition is re-derived when the handle is redeemed, not trusted from when it was staged. Take
/// that away and the only thing standing between a stale offer and an overwritten world is how quickly
/// somebody happened to read it.
/// </para>
/// <para>
/// The rule used throughout is <c>give_up_backup</c>, because its condition is a single flag the fake
/// supervisor owns — so "the server came back up between the offer and the answer" is one line of a
/// test rather than a fixture.
/// </para>
/// </remarks>
public sealed class ProposalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 3, 0, 0, TimeSpan.Zero);

    private const string Confirmer = "local:claude";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"kgsm-reactor-proposals-{Guid.NewGuid():N}.db");

    private readonly List<string> _ruleDirs = [];

    public void Dispose()
    {
        File.Delete(_path);
        foreach (string dir in _ruleDirs)
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// A staged offer holds the action, and holds it without doing it.
    /// </summary>
    /// <remarks>
    /// The performer throws, which is the assertion that matters: a counter left at zero has to be
    /// remembered and checked, where a performer that cannot be called at all fails the moment
    /// anything reaches it.
    /// </remarks>
    [Fact]
    public async Task Staging_an_offer_performs_nothing()
    {
        Harness harness = Build();

        Proposal? staged = await harness.StageAsync();

        Assert.NotNull(staged);
        Assert.Equal(ProposalState.Open, staged.State);
        Assert.Equal(ActionCatalog.CreateBackup, staged.ActionName);
        Assert.Equal(Now + TimeSpan.FromHours(8), staged.ExpiresAt);
        Assert.Empty(harness.Performer.Performed);

        Proposal announced = Assert.Single(harness.Emitter.Proposed);
        Assert.Equal(staged.Handle, announced.Handle);
    }

    /// <summary>
    /// One episode is offered once, however often the rule re-decides it.
    /// </summary>
    /// <remarks>
    /// A state rule re-reads its condition every sweep and its reason ages with it. Without this, a
    /// condition open for an afternoon would put an offer in somebody's inbox every thirty seconds, and
    /// answering one of them would leave the other four hundred standing.
    /// </remarks>
    [Fact]
    public async Task One_episode_is_offered_once()
    {
        Harness harness = Build();

        Proposal? first = await harness.StageAsync();
        Proposal? second = await harness.StageAsync();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(harness.Service.Open());
    }

    /// <summary>
    /// Confirming re-reads the world, finds the condition standing, and carries the action out.
    /// </summary>
    [Fact]
    public async Task Confirming_a_condition_that_still_holds_performs_the_action()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        Redemption redeemed = await harness.Service.ConfirmAsync(
            staged.Handle, Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Performed, redeemed.Outcome);

        // Attributed to the person, not to the rule. The rule offered; this happened because
        // somebody said yes, and an audit row naming the rule would make an authorised action
        // indistinguishable from one the host took on its own.
        Assert.Equal((ActionCatalog.CreateBackup, Confirmer),
            Assert.Single(harness.Performer.Performed));

        Proposal ended = harness.Service.Find(staged.Handle)!;
        Assert.Equal(ProposalState.Confirmed, ended.State);
        Assert.Equal(Confirmer, ended.AnsweredBy);
        Assert.True(ended.Ok);
        Assert.Equal("backup-7", ended.Artifact);
    }

    /// <summary>
    /// <b>The property the whole design rests on.</b> A server that came back up on its own turns a
    /// confirmation into an explanation instead of an action.
    /// </summary>
    /// <remarks>
    /// This is what makes a lifetime of hours safe, and it is why the window is not the safety control:
    /// however long the offer sat there, what decides is the world at the moment somebody answers.
    /// </remarks>
    [Fact]
    public async Task Confirming_after_the_condition_has_gone_performs_nothing_and_says_why()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        harness.World.Answer =
            Reading<InstanceRunState>.Measured(new InstanceRunState("running", true, 0));

        Redemption redeemed = await harness.Service.ConfirmAsync(
            staged.Handle, Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.NoLongerApplicable, redeemed.Outcome);
        Assert.Empty(harness.Performer.Performed);

        Proposal ended = harness.Service.Find(staged.Handle)!;
        Assert.Equal(ProposalState.NoLongerApplicable, ended.State);
        Assert.Null(ended.Ok);
        Assert.Contains("running", ended.Detail);

        Proposal resolved = Assert.Single(harness.Emitter.Resolved);
        Assert.Equal(ProposalState.NoLongerApplicable, resolved.State);
    }

    /// <summary>
    /// A world that will not answer leaves the offer standing, and performs nothing.
    /// </summary>
    /// <remarks>
    /// Unreadable is not a no and it is not a yes. Ending the proposal here would record a conclusion
    /// nobody reached; performing anyway would act on a reading taken hours ago. So it stays open, the
    /// person is told why, and they can answer again once whatever went quiet is back.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_world_leaves_the_offer_open()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        harness.World.Answer = Reading<InstanceRunState>.Unavailable("the supervisor is not answering");

        Redemption redeemed = await harness.Service.ConfirmAsync(
            staged.Handle, Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Unreadable, redeemed.Outcome);
        Assert.Empty(harness.Performer.Performed);
        Assert.Empty(harness.Emitter.Resolved);
        Assert.Equal(ProposalState.Open, harness.Service.Find(staged.Handle)!.State);
    }

    /// <summary>
    /// Two people confirming at once perform the action once.
    /// </summary>
    /// <remarks>
    /// Both find a redeemable proposal — the read cannot separate them. What separates them is that
    /// the row is claimed before the action runs, so only the call that changed a row goes on to do
    /// anything.
    /// </remarks>
    [Fact]
    public async Task Two_confirmations_of_one_offer_perform_it_once()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        Redemption first = await harness.Service.ConfirmAsync(
            staged.Handle, Confirmer, CancellationToken.None);
        Redemption second = await harness.Service.ConfirmAsync(
            staged.Handle, "discord:tanya", CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Performed, first.Outcome);
        Assert.Equal(RedemptionOutcome.AlreadyAnswered, second.Outcome);
        Assert.Single(harness.Performer.Performed);
        Assert.Equal(Confirmer, harness.Service.Find(staged.Handle)!.AnsweredBy);
    }

    /// <summary>
    /// A confirmation that names nobody is refused, and nothing is performed.
    /// </summary>
    /// <remarks>
    /// This is the one path in the leaf where a person authorises something, so the audit row it
    /// produces has to be able to name them. There is no fallback to the OS user here or anywhere else
    /// in this ecosystem.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("claude")]
    [InlineData(":claude")]
    [InlineData("local:")]
    public async Task A_confirmation_that_does_not_name_a_person_is_refused(string by)
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        Redemption redeemed = await harness.Service.ConfirmAsync(
            staged.Handle, by, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Unattributable, redeemed.Outcome);
        Assert.Empty(harness.Performer.Performed);
        Assert.Equal(ProposalState.Open, harness.Service.Find(staged.Handle)!.State);
    }

    /// <summary>
    /// A dismissal performs nothing, and does not re-read the world to decide that.
    /// </summary>
    /// <remarks>
    /// A person saying no is answering the offer rather than the world, and it stays a no whatever the
    /// world has since done. Asserted by making the world unreadable first: if the dismissal consulted
    /// it, it could not have gone through.
    /// </remarks>
    [Fact]
    public async Task A_dismissal_performs_nothing_and_asks_the_world_nothing()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        harness.World.Answer = Reading<InstanceRunState>.Unavailable("the supervisor is not answering");

        Redemption redeemed = await harness.Service.DismissAsync(
            staged.Handle, Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Dismissed, redeemed.Outcome);
        Assert.Empty(harness.Performer.Performed);

        Proposal ended = harness.Service.Find(staged.Handle)!;
        Assert.Equal(ProposalState.Dismissed, ended.State);
        Assert.Equal(Confirmer, ended.AnsweredBy);
        Assert.Null(ended.Ok);
    }

    /// <summary>
    /// An offer nobody answered lapses, and the lapse is written down.
    /// </summary>
    /// <remarks>
    /// The single most useful thing a week's review can count. A rule whose offers all lapse is one
    /// nobody wants, and that fact exists nowhere unless the expiry is recorded as an event rather than
    /// as a row quietly ageing out.
    /// </remarks>
    [Fact]
    public async Task An_offer_nobody_answers_lapses_and_is_announced()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        harness.Clock.Advance(TimeSpan.FromHours(9));
        int lapsed = await harness.Service.LapseExpiredAsync(CancellationToken.None);

        Assert.Equal(1, lapsed);
        Assert.Empty(harness.Performer.Performed);
        Assert.Empty(harness.Service.Open());

        Proposal ended = harness.Service.Find(staged.Handle)!;
        Assert.Equal(ProposalState.Lapsed, ended.State);
        Assert.Null(ended.AnsweredBy);
        Assert.Null(ended.Ok);

        Assert.Equal(ProposalState.Lapsed, Assert.Single(harness.Emitter.Resolved).State);
    }

    /// <summary>An expired handle cannot be redeemed, even before the sweep has closed it.</summary>
    /// <remarks>
    /// The clock and the sweep disagree for at most one interval, and honouring an expired offer for
    /// those seconds is the one case where the window would not mean what it says.
    /// </remarks>
    [Fact]
    public async Task An_expired_offer_cannot_be_confirmed()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        harness.Clock.Advance(TimeSpan.FromHours(9));

        Redemption redeemed = await harness.Service.ConfirmAsync(
            staged.Handle, Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Expired, redeemed.Outcome);
        Assert.Empty(harness.Performer.Performed);
    }

    /// <summary>A handle nothing was staged under names nothing.</summary>
    [Fact]
    public async Task An_unknown_handle_confirms_nothing()
    {
        Harness harness = Build();

        Redemption redeemed = await harness.Service.ConfirmAsync(
            new string('0', 32), Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Unknown, redeemed.Outcome);
        Assert.Null(redeemed.Proposal);
        Assert.Empty(harness.Performer.Performed);
    }

    /// <summary>
    /// An offer from a rule this host no longer runs cannot be confirmed.
    /// </summary>
    /// <remarks>
    /// Retiring a rule, switching it off, or deleting it are all statements that this host has stopped
    /// wanting what it offered. Honouring the offer anyway would let a deleted rule act.
    /// </remarks>
    [Fact]
    public async Task An_offer_from_a_rule_that_is_gone_is_no_longer_applicable()
    {
        Harness harness = Build();
        Proposal staged = (await harness.StageAsync())!;

        Harness without = Build(rules: []);

        Redemption redeemed = await without.Service.ConfirmAsync(
            staged.Handle, Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.NoLongerApplicable, redeemed.Outcome);
        Assert.Empty(without.Performer.Performed);
        Assert.Contains("give_up_backup", without.Service.Find(staged.Handle)!.Detail);
    }

    /// <summary>
    /// A confirmed offer whose action failed is recorded as confirmed, and as not ok.
    /// </summary>
    /// <remarks>
    /// The resolution says what the person did; <c>Ok</c> says what the action did. Collapsing them
    /// would make this case — somebody authorised it and it did not work — unrepresentable, and it is
    /// exactly the one an investigation is looking for.
    /// </remarks>
    [Fact]
    public async Task A_confirmed_offer_whose_action_failed_records_both_facts()
    {
        Harness harness = Build();
        harness.Performer.Answer = ActionResult.Failed("no room on the device");
        Proposal staged = (await harness.StageAsync())!;

        Redemption redeemed = await harness.Service.ConfirmAsync(
            staged.Handle, Confirmer, CancellationToken.None);

        Assert.Equal(RedemptionOutcome.Failed, redeemed.Outcome);

        Proposal ended = harness.Service.Find(staged.Handle)!;
        Assert.Equal(ProposalState.Confirmed, ended.State);
        Assert.Equal(Confirmer, ended.AnsweredBy);
        Assert.False(ended.Ok);
        Assert.Equal("no room on the device", ended.Detail);
    }

    /// <summary>
    /// Acting writes <c>reactor.acted</c> and never a resolution.
    /// </summary>
    /// <remarks>
    /// An autonomous action has no person behind it and no offer to point at. Writing it as a
    /// resolution too would double-count what this host did on its own, and would leave the resolution
    /// naming nobody in a field whose whole content is who answered.
    /// </remarks>
    [Fact]
    public async Task Acting_is_announced_as_an_action_and_not_as_a_resolution()
    {
        Harness harness = Build();

        ActionResult result = await harness.Service.ActAsync(
            harness.Decision(), new ReactorAction.CreateBackup("Ketchup"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Empty(harness.Emitter.Resolved);
        Assert.Empty(harness.Emitter.Proposed);

        (Decision decision, ActionResult announced) = Assert.Single(harness.Emitter.Acted);
        Assert.Equal("give_up_backup", decision.RuleId);
        Assert.True(announced.Ok);
        Assert.Equal("rule:give_up_backup", Assert.Single(harness.Performer.Performed).Actor);
    }

    /// <summary>
    /// An offer carries the sentence the decision was made with, rather than a pointer to it.
    /// </summary>
    /// <remarks>
    /// A person reading it at seven in the morning has to see what was true when it was staged.
    /// Resolving that through a rule somebody has since edited would show them a sentence no rule ever
    /// produced.
    /// </remarks>
    [Fact]
    public async Task An_offer_carries_the_reason_it_was_staged_with()
    {
        Harness harness = Build();
        Decision decision = harness.Decision(reason: "still given up on after 120s (5 consecutive failures)");

        Proposal staged = (await harness.StageAsync(decision))!;

        Assert.Equal("still given up on after 120s (5 consecutive failures)", staged.Reason);
        Assert.Equal(decision.Id, staged.DecisionId);
        Assert.Equal(EventSeverity.Danger, staged.Severity);
        Assert.Equal("kgsm-watchdog:s.ndjson:10", staged.EpisodeKey);
    }

    /// <summary>
    /// A rule may name its own lifetime, and the host's setting is the fallback.
    /// </summary>
    /// <remarks>
    /// The same arrangement the settle and suppression windows have. Capturing a broken state is worth
    /// answering all day; rolling a server back stops being the right move once somebody has started
    /// working on it by hand.
    /// </remarks>
    [Fact]
    public async Task A_rule_may_name_its_own_lifetime()
    {
        RuleDefinition rule = ShippedRules.Named("give_up_backup")
            with { ProposalLifetime = TimeSpan.FromMinutes(90) };

        Harness harness = Build(rules: [rule]);
        Proposal staged = (await harness.StageAsync(definition: rule))!;

        Assert.Equal(Now + TimeSpan.FromMinutes(90), staged.ExpiresAt);
    }

    /// <summary>
    /// A rule narrowed to one named server declines everywhere else, and says which.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How the first acting rule is contained, and it needs no new mechanism.</b> Scope is an
    /// ordinary guard row over <c>subject.id</c>, so it previews like any other row, reads in the
    /// editor like any other row, and writes its own sentence when it declines.
    /// </para>
    /// <para>
    /// An "applies to" field beside the rows would be a second place a rule can decline from —
    /// invisible to the preview that exists to explain exactly that.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("factorio-test", true)]
    [InlineData("Ketchup", false)]
    public async Task A_rule_scoped_to_one_server_declines_for_every_other(
        string subject, bool holds)
    {
        RuleDefinition seeded = ShippedRules.Named("give_up_backup");

        var scoped = seeded with
        {
            Rows =
            [
                new([Clause.IsNot("subject.id", "factorio-test")],
                    VerdictKind.DoesNotHold,
                    "{subject} is not the one server this rule acts on"),
                .. seeded.Rows,
            ],
        };

        var world = new FakeWorld();
        Verdict verdict = await RuleEvaluator.ToRule(scoped).Holds(
            new RuleContext(subject, Now, world, new NoHistory(), new EmptyFootprints()),
            CancellationToken.None);

        Assert.Equal(holds ? VerdictKind.Holds : VerdictKind.DoesNotHold, verdict.Kind);

        if (!holds)
            Assert.Contains("not the one server", verdict.Reason);
    }

    // ---- the harness ---------------------------------------------------------------------------

    private Harness Build(IReadOnlyList<RuleDefinition>? rules = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"kgsm-reactor-rules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _ruleDirs.Add(dir);

        foreach (RuleDefinition rule in rules ?? [ShippedRules.Named("give_up_backup")])
            File.WriteAllText(RuleStore.PathOf(dir, rule.Id), RuleStore.Write(rule));

        ReactorOptions options = ReactorOptions.FromSettings(new ReactorSettings
        {
            RulesDirectory = dir,
            LedgerPath = _path,
        });

        var ledger = new ObservationLedger(_path);
        new DecisionStore(ledger).Initialize();
        var store = new ProposalStore(ledger);
        store.Initialize();

        var clock = new MovableClock(Now);
        var world = new FakeWorld();
        var performer = new SpyPerformer();
        var emitter = new RecordingEmitter();

        var service = new ProposalService(
            store, performer, emitter,
            new RuleRegistry(dir, NullLogger<RuleRegistry>.Instance), world,
            new LedgerRuleHistory(ledger), new EmptyFootprints(), Options.Create(options), clock,
            NullLogger<ProposalService>.Instance);

        return new Harness(service, store, world, performer, emitter, clock, ledger);
    }

    private sealed record Harness(
        ProposalService Service,
        ProposalStore Store,
        FakeWorld World,
        SpyPerformer Performer,
        RecordingEmitter Emitter,
        MovableClock Clock,
        ObservationLedger Ledger)
    {
        /// <summary>A fired decision of the shape <c>give_up_backup</c> produces.</summary>
        public Decision Decision(string reason = "still given up on after 120s") => new(
            Id: Reactor.Ledger.Decision.IdFor("give_up_backup", "Ketchup", Episode),
            RuleId: "give_up_backup",
            Subject: "Ketchup",
            SubjectKind: SubjectKind.Instance,
            EpisodeKey: Episode,
            Severity: EventSeverity.Danger,
            Mode: RuleMode.Propose,
            Outcome: DecisionOutcome.Fired,
            Reason: reason,
            Withheld: false,
            RuleAuthor: null,
            Action: "take a pinned backup of Ketchup",
            ActionName: ActionCatalog.CreateBackup,
            ActionInstance: "Ketchup",
            ActionState: ActionState.None,
            OpenedAt: Now,
            DecidedAt: Now,
            Source: new EventSource("kgsm-watchdog", "s.ndjson", 10, null));

        public Task<Proposal?> StageAsync(Decision? decision = null, RuleDefinition? definition = null) =>
            Service.StageAsync(
                decision ?? Decision(),
                definition ?? ShippedRules.Named("give_up_backup"),
                CancellationToken.None);

        private const string Episode = "kgsm-watchdog:s.ndjson:10";
    }

    /// <summary>A ledger-free history, for the one test that evaluates a rule without a database.</summary>
    private sealed class NoHistory : IRuleHistory
    {
        public HistoryEvent? LastOccurrence(string eventType, string subject, DateTimeOffset notBefore) =>
            null;

        public IReadOnlyList<OpenEpisode> OpenEpisodes(
            string opensWith, string closesWith, DateTimeOffset notBefore) => [];

        public (TimeSpan P95, int Samples) EpisodeDuration(
            string opensWith, string closesWith, string subject, DateTimeOffset notBefore) =>
            (TimeSpan.Zero, 0);
    }

    /// <summary>A supervisor whose answer the test owns.</summary>
    private sealed class FakeWorld : IWorldView
    {
        public Reading<InstanceRunState> Answer { get; set; } =
            Reading<InstanceRunState>.Measured(new InstanceRunState("failed", true, 5));

        public ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Answer);

        public ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryDeclaration>.Unavailable("not asked here"));

        public ValueTask<Reading<InstanceSupervision>> SupervisionAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<InstanceSupervision>.Measured(new InstanceSupervision(null)));
    }

    private sealed class EmptyFootprints : IFootprintSource
    {
        public ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token) =>
            ValueTask.FromResult(Reading<IReadOnlyList<InstanceFootprint>>.Measured([]));

        public ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryTrend>.Unavailable("no series"));
    }

    /// <summary>A performer that records what it was asked for and answers what the test set.</summary>
    private sealed class SpyPerformer : IActionPerformer
    {
        public List<(string Action, string Actor)> Performed { get; } = [];

        public ActionResult Answer { get; set; } = ActionResult.Succeeded("backup-7", "captured");

        public Task<ActionResult> PerformAsync(
            ReactorAction action, string actor, CancellationToken token)
        {
            Performed.Add((action.Name, actor));
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingEmitter : IDecisionEmitter
    {
        public List<Proposal> Proposed { get; } = [];

        public List<Proposal> Resolved { get; } = [];

        public List<(Decision Decision, ActionResult Result)> Acted { get; } = [];

        public ValueTask<bool> EmitAsync(Decision decision, CancellationToken token = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> EmitProposedAsync(Proposal proposal, CancellationToken token = default)
        {
            Proposed.Add(proposal);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> EmitResolvedAsync(Proposal proposal, CancellationToken token = default)
        {
            Resolved.Add(proposal);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> EmitActedAsync(
            Decision decision, ActionResult result, CancellationToken token = default)
        {
            Acted.Add((decision, result));
            return ValueTask.FromResult(true);
        }
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
