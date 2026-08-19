using System.Collections.Concurrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
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
    private readonly IDecisionEmitter _emitter;
    private readonly ReactorOptions _options;
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

    private IReadOnlyList<Rule> _active = [];

    public RuleEngine(
        IEventService events,
        ObservationLedger ledger,
        DecisionStore decisions,
        IDecisionEmitter emitter,
        IWorldView world,
        IRuleHistory history,
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
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>An evaluation that has been woken and is waiting out its settle window.</summary>
    private sealed record Pending(
        Rule Rule, string Subject, SubjectKind SubjectKind, string EpisodeKey, EventSource Source,
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

    /// <summary>Every rule that is live, in catalog order.</summary>
    internal IReadOnlyList<Rule> Active => _active;

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
            .Select(p => (p.Rule.Id, p.Subject, p.DueAt))
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
            "Evaluating {Count} rule(s) every {Sweep}s, all in observe: {Rules}",
            _active.Count, _options.SweepIntervalSeconds, string.Join(", ", _active.Select(r => r.Id)));

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
    private IReadOnlyList<Rule> ResolveActiveRules()
    {
        List<Rule> active = [];

        foreach (Rule rule in RuleCatalog.All)
        {
            RuleMode? configured = _options.ModeFor(rule.Id);
            if (configured is null)
            {
                _logger.LogInformation("Rule {Rule} is not enabled on this host.", rule.Id);
                continue;
            }

            if (configured != RuleMode.Observe)
            {
                _logger.LogWarning(
                    "Rule {Rule} is configured to {Mode}, but this build only observes — treating it as "
                    + "observe. Nothing will be staged or performed.", rule.Id, configured);
            }

            if (rule.Shape == RuleShape.State && rule.Subjects is null)
            {
                // A state rule with nothing to enumerate would never evaluate, and would look enabled.
                _logger.LogError(
                    "Rule {Rule} is state-shaped but names no subjects — skipping it rather than "
                    + "leaving it enabled and inert.", rule.Id);
                continue;
            }

            active.Add(rule);
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

        foreach (Rule rule in _active)
        {
            if (rule.Shape != RuleShape.Edge)
                continue;
            if (!rule.Wakes.Contains(wrapper.EventType, StringComparer.Ordinal))
                continue;

            string key = $"{rule.Id}|{facts.Subject}|{source.Key}";
            _pending[key] = new Pending(
                rule, facts.Subject, facts.SubjectKind, source.Key, source,
                occurredAt, _clock.GetUtcNow() + rule.Settle);
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

        foreach (Rule rule in _active)
        {
            if (token.IsCancellationRequested)
                return;
            if (rule.Shape != RuleShape.State || rule.Subjects is null)
                continue;

            // A state rule rediscovers its own subjects, which is what makes it immune to a missed
            // event: nothing had to be seen for the condition to be found.
            IReadOnlyList<string> subjects = await rule.Subjects(_history, token).ConfigureAwait(false);

            foreach (string subject in subjects)
            {
                OpenEpisode? episode = OpeningOf(rule, subject, now);
                if (episode is null)
                    continue;

                await EvaluateAsync(
                    new Pending(rule, subject, episode.Value.SubjectKind, episode.Value.Source.Key,
                        episode.Value.Source, episode.Value.OpenedAt, now),
                    now, token).ConfigureAwait(false);
            }
        }

        // Stamped at the end rather than the start, so it answers "when did a sweep last complete"
        // rather than "when was one last attempted" — a sweep that hung partway through would
        // otherwise keep reporting itself as recent.
        LastSweepAt = _clock.GetUtcNow();
    }

    /// <summary>
    /// The open episode a state rule's subject belongs to, for its identity and its start.
    /// </summary>
    /// <remarks>
    /// Read from the ledger rather than held in memory, which is what lets an episode that began
    /// before this daemon started still be judged from when it actually opened.
    /// </remarks>
    private OpenEpisode? OpeningOf(Rule rule, string subject, DateTimeOffset now)
    {
        if (rule.Wakes.Count == 0)
            return null;

        // The closing type is the opening type's counterpart by convention: `_breached` closes with
        // `_cleared`. Derived rather than declared because a rule that named both would be declaring
        // the same pairing its Holds predicate already knows.
        string opens = rule.Wakes[0];
        string closes = opens.Replace("_breached", "_cleared", StringComparison.Ordinal);

        IReadOnlyList<OpenEpisode> open = _history.OpenEpisodes(opens, closes, now - TimeSpan.FromDays(30));
        foreach (OpenEpisode episode in open)
        {
            if (string.Equals(episode.Subject, subject, StringComparison.Ordinal))
                return episode;
        }

        return null;
    }

    /// <summary>Evaluate one rule against one subject, run the gate, and record what came of it.</summary>
    private async Task EvaluateAsync(Pending pending, DateTimeOffset now, CancellationToken token)
    {
        Rule rule = pending.Rule;
        var context = new RuleContext(pending.Subject, now, _world, _history);

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

        var decision = new Decision(
            Id: Decision.IdFor(rule.Id, pending.Subject, pending.EpisodeKey),
            RuleId: rule.Id,
            Subject: pending.Subject,
            SubjectKind: pending.SubjectKind,
            EpisodeKey: pending.EpisodeKey,
            Severity: rule.Severity,
            Mode: RuleMode.Observe,
            Outcome: outcome,
            Reason: reason,
            Action: action.Describe(),
            ActionName: action.Name,
            ActionInstance: action.TargetInstance,
            // Nothing is dispatched in this build, and the record says so rather than leaving a
            // reader to infer it from the mode.
            ActionState: ActionState.None,
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

        // Only a transition. The ledger folds a re-evaluated episode into one row that gets better
        // informed; the journal appends, and a condition that has held all afternoon is one judgment,
        // not one every thirty seconds.
        if (change == DecisionChange.Unchanged)
            return;

        if (await _emitter.EmitAsync(decision, token).ConfigureAwait(false))
            Emitted++;
    }

    /// <summary>
    /// Everything between a condition holding and an action being warranted.
    /// </summary>
    /// <remarks>
    /// ⚠ Every window and ceiling read here is a <b>placeholder</b> until the population report has a
    /// week behind it. They are wired now so the gate's outcomes are recorded from the start — which
    /// is what turns "is 30 minutes the right window" from an opinion into a query.
    /// </remarks>
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
                $"{holds}; but this rule already fired for {pending.Subject} "
                + $"{(int)(now - fired).TotalMinutes}m ago, inside the {(int)window.TotalMinutes}m window");
        }

        int firedThisHour = _decisions.FiredSince(now - TimeSpan.FromHours(1));
        if (_options.MaxActionsPerHour > 0 && firedThisHour >= _options.MaxActionsPerHour)
        {
            return (DecisionOutcome.Ceilinged,
                $"{holds}; but {firedThisHour} decision(s) already fired this hour, at the ceiling of "
                + $"{_options.MaxActionsPerHour}");
        }

        // ⚠ The carve-out: an additive action competes with nothing. A regression wants the broken
        // state preserved AND the rollback offered, and making those supersede one another would
        // silently lose whichever rule declared the lower severity.
        if (action.ChangesServerState)
        {
            foreach ((string otherRule, string severity) in
                     _decisions.FiredOnEpisode(pending.EpisodeKey, rule.Id))
            {
                if (Enum.TryParse(severity, out Severity other) && other > rule.Severity)
                {
                    return (DecisionOutcome.Superseded,
                        $"{holds}; but {otherRule} already spoke for this episode at {other}");
                }
            }
        }

        return (DecisionOutcome.Fired, holds);
    }
}
