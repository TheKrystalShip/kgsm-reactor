using System.Collections.Concurrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Actions;
using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Engine;

/// <summary>
/// Evaluates the rules and records what they decided.
/// </summary>
/// <remarks>
/// <para>
/// <b>This build decides and records. It dispatches nothing.</b> Every rule runs in
/// <see cref="RuleMode.Observe"/>, which is not a placeholder for the real thing — it is how a rule
/// earns the right to act. An observing rule records what it would have done and what would have
/// stopped it, so the review before anything is allowed to act reads real gate outcomes rather than a
/// simulation of them.
/// </para>
/// <para>
/// <b>The gate runs in full even in observe.</b> Short-circuiting at the mode would be cheaper and
/// would throw away exactly the data the exercise is for: a rule that would have been suppressed
/// four times out of five is telling you its window is wrong, and you only learn that by asking.
/// </para>
/// </remarks>
internal sealed class RuleEngine : BackgroundService
{
    private readonly IEventService _events;
    private readonly ObservationLedger _ledger;
    private readonly DecisionStore _decisions;
    private readonly IWorldView _world;
    private readonly IRuleHistory _history;
    private readonly IFootprintSource _footprint;
    private readonly IDecisionEmitter _emitter;
    private readonly ReactorOptions _options;
    private readonly RuleSet _rules;
    private readonly ProposalService _proposals;
    private readonly TimeProvider _clock;
    private readonly ILogger<RuleEngine> _logger;

    /// <summary>
    /// Evaluations waiting for their settle window, keyed so a repeated wake coalesces.
    /// </summary>
    /// <remarks>
    /// Keyed on (rule, subject, episode) rather than queued: a server that crashes four times in the
    /// settle window is one condition being evaluated once, not four evaluations of the same thing.
    /// </remarks>
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private IReadOnlyList<ActiveRule> _active = [];

    /// <summary>The rules this host holds, live and retired, and whatever could not be honoured.</summary>
    public RuleSet Rules => _rules;

    /// <summary>
    /// One rule as it is actually running: what it was written as, what it evaluates through, and the
    /// authority it has after clamping.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The definition travels beside the evaluable rule rather than being looked up from it.</b>
    /// A decision is stamped with the attribution the rule carried when it decided, and resolving that
    /// through the store at write time would mean an edit halfway through a sweep changed who a
    /// decision already in flight appears to name.
    /// </remarks>
    internal sealed record ActiveRule(RuleDefinition Definition, Rule Rule, RuleMode Mode);

    public RuleEngine(
        IEventService events,
        ObservationLedger ledger,
        DecisionStore decisions,
        IDecisionEmitter emitter,
        IWorldView world,
        IRuleHistory history,
        IFootprintSource footprint,
        RuleSet rules,
        ProposalService proposals,
        IOptions<ReactorOptions> options,
        TimeProvider clock,
        ILogger<RuleEngine> logger)
    {
        _events = events;
        _ledger = ledger;
        _decisions = decisions;
        _emitter = emitter;
        _world = world;
        _history = history;
        _footprint = footprint;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _rules = rules;
        _proposals = proposals;
    }

    /// <summary>An evaluation that has been woken and is waiting out its settle window.</summary>
    private sealed record Pending(
        ActiveRule Rule, string Subject, SubjectKind SubjectKind, string EpisodeKey, EventSource Source,
        DateTimeOffset OpenedAt, DateTimeOffset DueAt);

    /// <summary>Decisions recorded since start. Read by tests.</summary>
    internal long Recorded { get; private set; }

    /// <summary>
    /// Decisions announced on the journal since start. Read by tests.
    /// </summary>
    /// <remarks>
    /// Lower than <see cref="Recorded"/> by design, and the gap is the point: it is every sweep that
    /// re-read a condition and found the same answer.
    /// </remarks>
    internal long Emitted { get; private set; }

    /// <summary>
    /// When the last sweep finished, or null before the first one has.
    /// </summary>
    /// <remarks>
    /// Null rather than the start time. A reactor whose first sweep has not landed has not evaluated
    /// anything, and reporting its start time here would read as a sweep that happened.
    /// </remarks>
    internal DateTimeOffset? LastSweepAt { get; private set; }

    /// <summary>Every rule that is live, in file order.</summary>
    internal IReadOnlyList<ActiveRule> Active => _active;

    /// <summary>
    /// The most authority this build can honour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single place that fact is expressed, so every surface reads it from here rather than
    /// assuming — <c>/status</c> and <c>/catalog</c> both report it, and the panel's editor offers a
    /// mode against it.
    /// </para>
    /// <para>
    /// ⚠ <b>This is a ceiling, not a setting.</b> Every rule still starts at
    /// <see cref="RuleMode.Observe"/> and a host acts only where somebody has said so on a named rule.
    /// Raising this authorises nothing on its own.
    /// </para>
    /// </remarks>
    internal static RuleMode Honours => RuleMode.Act;

    /// <summary>
    /// What a rule may actually do, as opposed to what it was configured to do.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>This is the mode a surface must report.</b> Reporting the configured one shows an
    /// authority the rule does not have: an operator who set a rule to act, saw "act" on a status
    /// page and was silently observed would believe the host is acting when it is not — which is the
    /// failure <see cref="ResolveActiveRules"/> refuses to allow quietly, and a page contradicting a
    /// warning nobody reads in the journal is how it happens anyway.
    /// <para>
    /// The enum is ordered safest-first, so the smaller of the two is the answer.
    /// </para>
    /// </remarks>
    internal static RuleMode Effective(RuleMode configured) =>
        (RuleMode)Math.Min((int)configured, (int)Honours);

    /// <summary>
    /// The evaluations woken and waiting out their settle windows, soonest first.
    /// </summary>
    /// <remarks>
    /// A snapshot, not the live dictionary: the sweep mutates that from another thread, and handing a
    /// reader something being written underneath it is how a status endpoint comes to report an
    /// evaluation that had already run.
    /// </remarks>
    internal IReadOnlyList<(string Rule, string Subject, DateTimeOffset DueAt)> PendingEvaluations =>
        [.. _pending.Values
            .Select(p => (p.Rule.Definition.Id, p.Subject, p.DueAt))
            .OrderBy(p => p.DueAt)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _active = ResolveActiveRules();

        if (_active.Count == 0)
        {
            _logger.LogInformation("No rules are enabled — the reactor observes and judges nothing.");
            return;
        }

        _logger.LogInformation(
            "Evaluating {Count} rule(s) every {Sweep}s: {Rules}",
            _active.Count, _options.SweepIntervalSeconds,
            string.Join(", ", _active.Select(r => $"{r.Definition.Id} ({r.Mode.ToString().ToLowerInvariant()})")));

        _events.RegisterRawHandler(OnEventAsync);

        var period = TimeSpan.FromSeconds(Math.Max(_options.SweepIntervalSeconds, 1));
        using var timer = new PeriodicTimer(period, _clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;

                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not end the loop. A reactor that stopped evaluating would look
                // exactly like a host with nothing wrong, which is the worst way for this to fail.
                _logger.LogError(ex, "A rule sweep failed; retrying on the next tick.");
            }
        }
    }

    /// <summary>
    /// Which rules are enabled, and in what mode.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A mode this build cannot honour is clamped and said out loud.</b> Propose and act are
    /// later phases; an operator who configures one and is silently observed would believe the host is
    /// acting when it is not, which is a worse failure than refusing.
    /// </remarks>
    private IReadOnlyList<ActiveRule> ResolveActiveRules()
    {
        List<ActiveRule> active = [];

        foreach (RuleDefinition definition in _rules.Rules)
        {
            RuleMode effective = Effective(definition.Mode);

            if (effective != definition.Mode)
            {
                _logger.LogWarning(
                    "Rule {Rule} asks for {Mode}, but this build honours at most {Honours}, "
                    + "which is what it will do. Nothing will be staged or performed.",
                    definition.Id, definition.Mode, Honours);
            }

            if (effective == RuleMode.Off)
            {
                _logger.LogInformation("Rule {Rule} is off on this host.", definition.Id);
                continue;
            }

            active.Add(new ActiveRule(definition, RuleEvaluator.ToRule(definition), effective));
        }

        return active;
    }

    /// <summary>Wakes any edge rule that keys on this event.</summary>
    /// <remarks>
    /// Does no I/O, for the same reason the observation handler does not: this runs on the journal
    /// read loop, and putting an evaluation in front of it would put the whole rule table's latency
    /// between the journal and every other consumer of that loop.
    /// </remarks>
    private Task OnEventAsync(EventWrapper wrapper, EventPosition position)
    {
        if (wrapper is null || string.IsNullOrWhiteSpace(wrapper.EventType))
            return Task.CompletedTask;

        string producer = string.IsNullOrWhiteSpace(position.Producer) ? "unknown" : position.Producer!;
        EventFacts facts = EventClassifier.Classify(wrapper.EventType, wrapper.Data, producer);

        if (facts.Subject.Length == 0)
            return Task.CompletedTask;

        var source = new EventSource(
            producer, position.Segment ?? string.Empty, position.Offset, position.EventId);
        DateTimeOffset occurredAt = wrapper.Timestamp ?? _clock.GetUtcNow();

        foreach (ActiveRule active in _active)
        {
            if (active.Rule.Shape != RuleShape.Edge)
                continue;
            // Matched on the current name: a segment written before a producer renamed one of its
            // events is still read, and a rule keyed on the name it is called now has to wake on it.
            if (!active.Rule.Wakes.Contains(
                    LegacyEventNames.Canonical(wrapper.EventType), StringComparer.Ordinal))
                continue;

            string key = $"{active.Definition.Id}|{facts.Subject}|{source.Key}";
            _pending[key] = new Pending(
                active, facts.Subject, facts.SubjectKind, source.Key, source,
                occurredAt, _clock.GetUtcNow() + active.Rule.Settle);
        }

        return Task.CompletedTask;
    }

    /// <summary>One pass: everything whose settle window has elapsed, plus every state rule.</summary>
    internal async Task SweepAsync(CancellationToken token)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        foreach ((string key, Pending pending) in _pending.ToArray())
        {
            if (token.IsCancellationRequested)
                return;
            if (now < pending.DueAt)
                continue;

            _pending.TryRemove(key, out _);
            await EvaluateAsync(pending, now, token).ConfigureAwait(false);
        }

        foreach (ActiveRule active in _active)
        {
            if (token.IsCancellationRequested)
                return;
            if (active.Rule.Shape != RuleShape.State || active.Rule.Subjects is null)
                continue;

            // A state rule rediscovers its own subjects, which is what makes it immune to a missed
            // event: nothing had to be seen for the condition to be found.
            var subjectContext = new SubjectContext(_clock.GetUtcNow(), _world, _history, _footprint);
            IReadOnlyList<string> subjects =
                await active.Rule.Subjects(subjectContext, token).ConfigureAwait(false);

            foreach (string subject in subjects)
            {
                OpenEpisode? episode = OpeningOf(active.Rule, subject, now);
                if (episode is null)
                    continue;

                await EvaluateAsync(
                    new Pending(active, subject, episode.Value.SubjectKind, episode.Value.Source.Key,
                        episode.Value.Source, episode.Value.OpenedAt, now),
                    now, token).ConfigureAwait(false);
            }
        }

        // An offer nobody answered has to end, and the sweep is the only clock this leaf has. It runs
        // after the evaluations rather than before: a rule that has just re-offered something should
        // not have that offer expired in the same pass by a window computed a moment earlier.
        await _proposals.LapseExpiredAsync(token).ConfigureAwait(false);

        // Stamped at the end rather than the start, so it answers "when did a sweep last complete"
        // rather than "when was one last attempted" — a sweep that hung partway through would
        // otherwise keep reporting itself as recent.
        LastSweepAt = _clock.GetUtcNow();
    }

    /// <summary>
    /// The open episode a state rule's subject belongs to, for its identity and its start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the ledger rather than held in memory, which is what lets an episode that began
    /// before this daemon started still be judged from when it actually opened.
    /// </para>
    /// <para>
    /// <b>A rule that wakes on nothing identifies its own episodes.</b> Some conditions are not
    /// episodes at all: a footprint drifting away from a declaration is a standing fact about
    /// accumulated measurement, and no producer writes a line when it becomes true. Such a rule gets a
    /// synthetic opening keyed on itself and its subject — stable across sweeps, so re-evaluating one
    /// refines its decision rather than opening a second, and <c>opened_at</c> keeps the instant it was
    /// first seen because the upsert deliberately does not overwrite it.
    /// </para>
    /// <para>
    /// ⚠ <b>The source it carries names a measurement rather than a journal line, and that is a real
    /// weakening of invariant 1.</b> A reader can go to the endpoint named and see what is true now,
    /// which is not the same as reading the line the decision was made from. The reason string is
    /// therefore load-bearing here in a way it is not for the other rules: it carries the figures, so
    /// the decision describes itself rather than pointing at something that has since moved on.
    /// </para>
    /// </remarks>
    private OpenEpisode? OpeningOf(Rule rule, string subject, DateTimeOffset now)
    {
        if (rule.Wakes.Count == 0)
        {
            return new OpenEpisode(
                subject,
                SubjectKind.Instance,
                now,
                new EventSource(rule.Id, subject, 0, null));
        }

        string opens = rule.Wakes[0];

        IReadOnlyList<OpenEpisode> open = _history.OpenEpisodes(
            opens, EpisodeShape.Closes(opens), now - EpisodeShape.LookBack);
        foreach (OpenEpisode episode in open)
        {
            if (string.Equals(episode.Subject, subject, StringComparison.Ordinal))
                return episode;
        }

        return null;
    }

    /// <summary>
    /// Whether a decision is worth putting on the journal every component on this host shares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything is recorded; this decides only what is announced.</b> The ledger holds every
    /// evaluation with its reason, and <c>--decisions</c> reads it. The journal is a different
    /// audience: an audit log somebody skims, and a line there costs their attention whether or not
    /// it was worth having.
    /// </para>
    /// <para>
    /// <b>Only a transition</b>, because a condition that has held all afternoon is one judgment, not
    /// one every thirty seconds.
    /// </para>
    /// <para>
    /// ⚠ <b>And never a verdict the rule withheld</b> — a coverage gate reports what this leaf cannot
    /// yet say about an instance, which is unactionable by construction and the permanent steady state
    /// for anything recently installed. <b>Except when it replaces a rule that was firing:</b> a
    /// condition that stops being judged is news exactly when something was being judged, and
    /// swallowing that would let a rule go quiet without anybody being told it had.
    /// </para>
    /// </remarks>
    private bool Announceable(
        DecisionChange change, Decision decision, DecisionOutcome? previously)
    {
        if (change == DecisionChange.Unchanged)
            return false;

        return !decision.Withheld || previously == DecisionOutcome.Fired;
    }

    /// <summary>
    /// When the condition began, for a sentence that wants to date it, or null when nothing observed
    /// it beginning.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A rule that wakes on nothing has no opening, and its synthetic episode's stamp is not
    /// one.</b> That stamp records when this daemon first looked, which is a fact about the reactor;
    /// handing it to a sentence would date a fortnight-old drift from the moment somebody deployed.
    /// </remarks>
    private static DateTimeOffset? OpeningInstant(Pending pending) =>
        pending.Rule.Rule.Wakes.Count == 0 ? null : pending.OpenedAt;

    /// <summary>Evaluate one rule against one subject, run the gate, and record what came of it.</summary>
    private async Task EvaluateAsync(Pending pending, DateTimeOffset now, CancellationToken token)
    {
        RuleDefinition definition = pending.Rule.Definition;
        Rule rule = pending.Rule.Rule;
        var context = new RuleContext(
            pending.Subject, now, _world, _history, _footprint, OpeningInstant(pending));

        Verdict verdict;
        try
        {
            verdict = await rule.Holds(context, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A predicate that throws has not decided anything. Recorded as unreadable rather than
            // swallowed, so a broken rule is visible in the record instead of being a silent absence.
            _logger.LogError(ex, "Rule {Rule} failed while judging {Subject}.", rule.Id, pending.Subject);
            verdict = Verdict.Unreadable($"the rule failed while judging: {ex.Message}");
        }

        ReactorAction action = rule.Action(pending.Subject);

        (DecisionOutcome outcome, string reason) = verdict.Kind switch
        {
            VerdictKind.Unreadable => (DecisionOutcome.Unreadable, verdict.Reason),
            VerdictKind.DoesNotHold => (DecisionOutcome.Settled, verdict.Reason),
            _ => Gate(rule, pending, action, now, verdict.Reason),
        };

        string decisionId = Decision.IdFor(rule.Id, pending.Subject, pending.EpisodeKey);

        // Read before the upsert overwrites it. A state rule re-decides its episode every sweep and its
        // reason ages with the condition, so "the decision changed" is not "act again" — what makes
        // dispatch happen once is this row already saying something was handed somewhere, which is also
        // the only answer that survives a restart.
        ActionState dispatched = _decisions.ActionStateOf(decisionId) ?? ActionState.None;

        // Read beside it, and for the same reason: whether a change is worth announcing depends on
        // what it changed from, and the upsert below is about to overwrite it.
        DecisionOutcome? previously = _decisions.OutcomeOf(decisionId);

        var decision = new Decision(
            Id: decisionId,
            RuleId: rule.Id,
            Subject: pending.Subject,
            SubjectKind: pending.SubjectKind,
            EpisodeKey: pending.EpisodeKey,
            Severity: rule.Severity,
            Mode: pending.Rule.Mode,
            Outcome: outcome,
            Reason: reason,
            // Carried from the verdict, and only meaningful while the outcome is unreadable: a rule
            // that went on to fire or settle read enough to say so.
            Withheld: verdict.Withheld && outcome == DecisionOutcome.Unreadable,
            // Copied onto the decision rather than joined at read time. A decision six months old
            // must still name who had shaped the rule when it fired, and resolving it through the
            // store later means editing a rule silently rewrites the attribution of everything it
            // ever decided — while retiring one, or closing an account, erases the trace entirely.
            RuleAuthor: definition.Author,
            Action: action.Describe(),
            ActionName: action.Name,
            ActionInstance: action.TargetInstance,
            // Carried forward rather than reset, so re-deciding an open episode does not erase the
            // fact that its action was already offered or already performed.
            ActionState: dispatched,
            OpenedAt: pending.OpenedAt,
            DecidedAt: now,
            Source: pending.Source);

        DecisionChange change;
        try
        {
            change = _decisions.Record(decision);
            Recorded++;

            if (outcome == DecisionOutcome.Fired)
            {
                _logger.LogInformation(
                    "{Rule} would {Action} — {Reason}", rule.Id, action.Describe(), reason);
            }
            else
            {
                _logger.LogDebug(
                    "{Rule} on {Subject}: {Outcome} — {Reason}",
                    rule.Id, pending.Subject, outcome, reason);
            }
        }
        catch (Exception ex)
        {
            // The ledger is where the gate reads its own history from, so a decision that could not be
            // recorded must not be announced either — a journal line for a decision the suppression
            // window has never heard of would let the same thing fire again on the next sweep.
            _logger.LogError(ex, "Could not record {Rule}'s decision about {Subject}.",
                rule.Id, pending.Subject);
            return;
        }

        if (Announceable(change, decision, previously)
            && await _emitter.EmitAsync(decision, token).ConfigureAwait(false))
        {
            Emitted++;
        }

        // Dispatch is judged on the row and not on the transition, deliberately: the announcement is
        // about what changed, and this is about what has already been done. A decision whose reason
        // aged from "open four minutes" to "open forty" is one judgment being refined, and acting on
        // it twice would be the reactor doing the same thing to a server every time it looked.
        if (outcome == DecisionOutcome.Fired && dispatched == ActionState.None)
            await DispatchAsync(pending, decision, action, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands a fired decision's action to whatever its mode permits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mode is read from what was resolved, never from what the rule asked for.</b> A rule
    /// configured to act on a build that honours less observes, and observing means this does nothing
    /// at all — which is what makes the review before anything acts a review of real gate outcomes.
    /// </para>
    /// <para>
    /// ⚠ <b>An action of <c>none</c> is not a mode failure and is not reported as one.</b> Most rules
    /// report and propose nothing; the decision record is their whole output, and staging an offer to
    /// do nothing would fill somebody's inbox with questions that have no answer.
    /// </para>
    /// </remarks>
    private async Task DispatchAsync(
        Pending pending, Decision decision, ReactorAction action, CancellationToken token)
    {
        if (action is ReactorAction.Nothing)
            return;

        switch (pending.Rule.Mode)
        {
            case RuleMode.Propose:
                Proposal? staged = await _proposals
                    .StageAsync(decision, pending.Rule.Definition, token)
                    .ConfigureAwait(false);

                if (staged is not null)
                    _decisions.SetActionState(decision.Id, ActionState.Proposed);

                break;

            case RuleMode.Act:
                // Stamped before the attempt, so a daemon that dies mid-backup leaves a row saying
                // something was handed to the engine rather than a row saying nothing was — and the
                // next sweep does not hand it over again.
                _decisions.SetActionState(decision.Id, ActionState.Dispatched);

                ActionResult result = await _proposals
                    .ActAsync(decision, action, token)
                    .ConfigureAwait(false);

                _decisions.SetActionState(
                    decision.Id, result.Ok ? ActionState.Succeeded : ActionState.Failed);
                break;

            default:
                break;
        }
    }

    /// <summary>How long <paramref name="rule"/> stays quiet about one subject after firing.</summary>
    /// <remarks>
    /// The rule's own measured window when it has one, the host-wide setting when it does not. A rule
    /// carrying a figure derived from its own repeat spacing should not have it overridden by a number
    /// chosen for a different rule's event — and one that has never been measured should follow the
    /// host rather than a default invented for it.
    /// </remarks>
    private TimeSpan SuppressionFor(Rule rule) =>
        rule.Suppression ?? TimeSpan.FromMinutes(Math.Max(_options.SuppressionWindowMinutes, 0));

    private (DecisionOutcome, string) Gate(
        Rule rule, Pending pending, ReactorAction action, DateTimeOffset now, string holds)
    {
        DateTimeOffset? lastFired = _decisions.LastFired(rule.Id, pending.Subject, pending.EpisodeKey);
        TimeSpan window = SuppressionFor(rule);
        if (lastFired is { } fired && now - fired < window)
        {
            return (DecisionOutcome.Suppressed,
                $"{holds}. This rule already said so about {pending.Subject} "
                + $"{(int)(now - fired).TotalMinutes} minutes ago and stays quiet for "
                + $"{(int)window.TotalMinutes} after it does");
        }

        int firedThisHour = _decisions.FiredSince(now - TimeSpan.FromHours(1));
        if (_options.MaxActionsPerHour > 0 && firedThisHour >= _options.MaxActionsPerHour)
        {
            return (DecisionOutcome.Ceilinged,
                $"{holds}. This host has already had {firedThisHour} rule(s) fire in the last hour, "
                + $"which is all it allows ({_options.MaxActionsPerHour})");
        }

        // ⚠ The carve-out: an additive action competes with nothing. A regression wants the broken
        // state preserved AND the rollback offered, and making those supersede one another would
        // silently lose whichever rule declared the lower severity.
        if (action.ChangesServerState)
        {
            foreach ((string otherRule, string severity) in
                     _decisions.FiredOnEpisode(pending.EpisodeKey, rule.Id))
            {
                if (Enum.TryParse(severity, out EventSeverity other) && other > rule.Severity)
                {
                    return (DecisionOutcome.Superseded,
                        $"{holds}. {otherRule} already spoke about the same episode and speaks more "
                        + $"loudly ({other.ToWire()}), so it decides what happens to this server");
                }
            }
        }

        return (DecisionOutcome.Fired, holds);
    }
}
