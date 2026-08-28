using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Actions;

/// <summary>What came of trying to redeem a handle.</summary>
internal enum RedemptionOutcome
{
    /// <summary>Confirmed, the condition still held, and the action was carried out.</summary>
    Performed,

    /// <summary>Confirmed, the condition still held, and the action failed.</summary>
    Failed,

    /// <summary>A person declined it.</summary>
    Dismissed,

    /// <summary>
    /// Confirmed, and the condition had gone by then.
    /// </summary>
    /// <remarks>
    /// <b>An ordinary answer, not an error.</b> It is the reason the lifetime can be hours: a server
    /// that came back up on its own ends the offer instead of having a restore run over it.
    /// </remarks>
    NoLongerApplicable,

    /// <summary>No proposal carries that handle.</summary>
    Unknown,

    /// <summary>It ran out of time before anybody answered.</summary>
    Expired,

    /// <summary>Somebody had already answered it.</summary>
    AlreadyAnswered,

    /// <summary>The caller was not named in the shape an actor has to have.</summary>
    Unattributable,

    /// <summary>
    /// The condition could not be re-read, so nothing is known and nothing was done.
    /// </summary>
    /// <remarks>
    /// <b>The proposal is left open rather than ended.</b> A world that would not answer has said
    /// nothing about whether the offer still stands, and closing it as inapplicable would record a
    /// conclusion nobody reached. The person can try again once whatever went quiet is back.
    /// </remarks>
    Unreadable,
}

/// <summary>One attempt to redeem a handle.</summary>
/// <param name="Outcome">What came of it.</param>
/// <param name="Proposal">The proposal as it now stands, or null when the handle named none.</param>
/// <param name="Detail">Why, in words. Always present when the outcome is not <c>Performed</c>.</param>
internal readonly record struct Redemption(
    RedemptionOutcome Outcome, Proposal? Proposal, string? Detail)
{
    public static Redemption Of(RedemptionOutcome outcome, string detail, Proposal? proposal = null) =>
        new(outcome, proposal, detail);
}

/// <summary>
/// Stages actions for a person to confirm, and carries out the ones they do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safety here is re-derivation, not a short window.</b> The assistant's confirmations expire in
/// five minutes because somebody just asked and is standing in front of the answer. A reactor proposal
/// is addressed to whoever notices, possibly in the morning, so its lifetime is measured in hours —
/// and what makes that safe is that <see cref="ConfirmAsync"/> re-evaluates the rule against the world
/// as it is <em>now</em>. A person confirming a restore on a server that came back up gets
/// <see cref="RedemptionOutcome.NoLongerApplicable"/> and an explanation, not an overwritten world.
/// </para>
/// <para>
/// <b>Redemption re-derives the condition and deliberately does not re-run the gate.</b> Suppression
/// and the hourly ceiling govern how often the <em>reactor</em> speaks; at redemption the person is
/// speaking, and refusing what somebody just authorised because the rule had been noisy an hour ago
/// would be the leaf overruling them on a question that was never about them.
/// </para>
/// <para>
/// <b>Every proposal ends exactly once, and the <c>UPDATE</c> is what decides which call ended it.</b>
/// Two people pressing confirm in the same second both find a redeemable proposal; only one changes a
/// row, and only that one performs the action.
/// </para>
/// </remarks>
internal sealed class ProposalService(
    ProposalStore proposals,
    IActionPerformer performer,
    IDecisionEmitter emitter,
    RuleRegistry registry,
    IWorldView world,
    IRuleHistory history,
    IFootprintSource footprint,
    IOptions<ReactorOptions> options,
    TimeProvider clock,
    ILogger<ProposalService> logger)
{
    private readonly ReactorOptions _options = options.Value;

    /// <summary>
    /// Stages a decision's action for a person, unless one is already offered for the same episode.
    /// </summary>
    /// <remarks>
    /// The decision id is stable per rule, subject and episode, so a state rule re-deciding on every
    /// sweep re-offers nothing: the store's index refuses the duplicate and this answers null. The
    /// caller reads that as "already offered", which is what it is.
    /// </remarks>
    /// <returns>The staged proposal, or null when one was already open or nothing could be written.</returns>
    public async Task<Proposal?> StageAsync(
        Decision decision, RuleDefinition definition, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(definition);

        DateTimeOffset now = clock.GetUtcNow();

        var proposal = new Proposal(
            Handle: Proposal.NewHandle(),
            DecisionId: decision.Id,
            RuleId: decision.RuleId,
            RuleAuthor: decision.RuleAuthor,
            Subject: decision.Subject,
            SubjectKind: decision.SubjectKind,
            EpisodeKey: decision.EpisodeKey,
            Severity: decision.Severity,
            ActionName: decision.ActionName,
            Action: decision.Action,
            ActionInstance: decision.ActionInstance,
            Reason: decision.Reason,
            // The decision's own opening, not the staging instant. They differ by the settle window
            // at least, and by however long the condition had been true before anything woke.
            OpenedAt: definition.Wakes.Count == 0 ? null : decision.OpenedAt,
            StagedAt: now,
            ExpiresAt: now + LifetimeOf(definition));

        try
        {
            if (!proposals.Stage(proposal))
                return null;
        }
        catch (Exception ex)
        {
            // A proposal that could not be stored must not be announced: a journal line offering a
            // handle nothing will honour is worse than no offer at all.
            logger.LogError(ex, "Could not stage {Rule}'s offer about {Subject}.",
                decision.RuleId, decision.Subject);
            return null;
        }

        logger.LogInformation(
            "{Rule} offers to {Action} — {Reason}. Expires {Expires:u}.",
            proposal.RuleId, proposal.Action, proposal.Reason, proposal.ExpiresAt);

        await emitter.EmitProposedAsync(proposal, token).ConfigureAwait(false);
        return proposal;
    }

    /// <summary>Carries out an action a rule decided on, with nobody behind it.</summary>
    /// <remarks>
    /// <b>The one path with no person in it.</b> The actor is the rule and there is no confirmation
    /// to point at, which is why it is written as <c>reactor.acted</c> rather than as a resolution: a
    /// surface answering "what did this host do on its own" must not have to subtract the confirmed
    /// ones out of a combined list.
    /// </remarks>
    public async Task<ActionResult> ActAsync(Decision decision, ReactorAction action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(action);

        ActionResult result;
        try
        {
            result = await performer
                .PerformAsync(action, ActorFor(decision.RuleId), token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = ActionResult.Failed(ex.Message);
        }

        if (result.Ok)
            logger.LogInformation("{Rule} {Action}.", decision.RuleId, decision.Action);
        else
            logger.LogError("{Rule} tried to {Action} and could not: {Detail}",
                decision.RuleId, decision.Action, result.Detail);

        await emitter.EmitActedAsync(decision, result, token).ConfigureAwait(false);
        return result;
    }

    /// <summary>Every proposal still open, soonest to expire first.</summary>
    public IReadOnlyList<Proposal> Open() => proposals.Open();

    /// <summary>The proposals staged since <paramref name="notBefore"/>, whatever became of them.</summary>
    public IReadOnlyList<Proposal> Recent(DateTimeOffset notBefore, int limit) =>
        proposals.Recent(notBefore, limit);

    /// <summary>The proposal a handle names, or null.</summary>
    public Proposal? Find(string handle) => proposals.Find(handle);

    /// <summary>
    /// Redeems a handle: re-derives the condition, and carries the action out if it still holds.
    /// </summary>
    /// <param name="handle">The token the proposal was offered under.</param>
    /// <param name="by">
    /// Who is confirming, as <c>provider:name</c>. Required, and refused when it is not in that
    /// shape — an action a person authorised that cannot name the person is exactly the audit row this
    /// ecosystem does not write.
    /// </param>
    /// <param name="token">Cancellation.</param>
    public async Task<Redemption> ConfirmAsync(string handle, string by, CancellationToken token)
    {
        if (!IsActor(by))
        {
            return Redemption.Of(RedemptionOutcome.Unattributable,
                "a confirmation has to name who is confirming, as provider:name");
        }

        DateTimeOffset now = clock.GetUtcNow();

        if (Redeemable(handle, now) is { } refusal)
            return refusal;

        Proposal proposal = proposals.Find(handle)!;

        // Re-derived here rather than trusted from staging time. This is the whole safety argument for
        // a lifetime measured in hours, and it runs BEFORE the row is claimed so a stale offer is never
        // spent — the person can be told why and the proposal ends as inapplicable rather than as a
        // failure they have to interpret.
        Verdict verdict = await ReevaluateAsync(proposal, now, token).ConfigureAwait(false);

        // Unreadable is not a no. Nothing could be established, so the offer stays open and the
        // person is told why — ending it here would record a conclusion nobody reached, and doing it
        // the other way and performing anyway would act on a reading taken hours ago.
        if (verdict.Kind == VerdictKind.Unreadable)
        {
            return Redemption.Of(RedemptionOutcome.Unreadable,
                $"the condition could not be re-read, so nothing was done — {verdict.Reason}",
                proposal);
        }

        if (verdict.Kind != VerdictKind.Holds)
        {
            return await EndAsync(
                proposal, ProposalState.NoLongerApplicable, now, by,
                ok: null, artifact: null,
                detail: verdict.Reason,
                outcome: RedemptionOutcome.NoLongerApplicable,
                token).ConfigureAwait(false);
        }

        // Claimed before the action runs. Whichever call changes the row is the one that performs, so
        // two people confirming at the same moment cannot both restore the same server.
        if (!proposals.End(handle, ProposalState.Confirmed, now, by))
        {
            return Redemption.Of(RedemptionOutcome.AlreadyAnswered,
                "somebody answered this a moment ago", proposals.Find(handle));
        }

        ActionResult result;
        try
        {
            // Attributed to the PERSON, not to the rule. The rule found the condition and offered;
            // this backup exists because somebody said yes, and an audit row naming the rule would make
            // an authorised action indistinguishable from one the host took on its own — which is the
            // entire distinction propose and act exist to draw. The rule is still recoverable: the
            // origin says `reactor`, and the resolution line names it beside the person.
            result = await performer
                .PerformAsync(ActionOf(proposal), by, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = ActionResult.Failed(ex.Message);
        }

        // The row is already claimed; this fills in how it went. Written whatever happened — a
        // confirmed proposal whose action failed is a complete fact and the case somebody investigating
        // most needs to find.
        proposals.Fill(handle, result.Ok, result.Artifact, result.Detail);
        Proposal ended = proposals.Find(handle)!;

        if (result.Ok)
            logger.LogInformation("{By} confirmed {Rule}'s offer: {Action}.", by, ended.RuleId, ended.Action);
        else
            logger.LogError("{By} confirmed {Rule}'s offer and it failed: {Detail}",
                by, ended.RuleId, result.Detail);

        await emitter.EmitResolvedAsync(ended, token).ConfigureAwait(false);

        return new Redemption(
            result.Ok ? RedemptionOutcome.Performed : RedemptionOutcome.Failed, ended, result.Detail);
    }

    /// <summary>Declines a proposal. Nothing is attempted and nothing is re-derived.</summary>
    /// <remarks>
    /// The condition is not re-read, deliberately: a person saying no is answering the offer, not the
    /// world, and it stays a no whatever the world has since done.
    /// </remarks>
    public async Task<Redemption> DismissAsync(string handle, string by, CancellationToken token)
    {
        if (!IsActor(by))
        {
            return Redemption.Of(RedemptionOutcome.Unattributable,
                "a dismissal has to name who is dismissing, as provider:name");
        }

        DateTimeOffset now = clock.GetUtcNow();

        if (Redeemable(handle, now) is { } refusal)
            return refusal;

        if (!proposals.End(handle, ProposalState.Dismissed, now, by))
        {
            return Redemption.Of(RedemptionOutcome.AlreadyAnswered,
                "somebody answered this a moment ago", proposals.Find(handle));
        }

        Proposal ended = proposals.Find(handle)!;
        logger.LogInformation("{By} dismissed {Rule}'s offer to {Action}.", by, ended.RuleId, ended.Action);
        await emitter.EmitResolvedAsync(ended, token).ConfigureAwait(false);

        return new Redemption(RedemptionOutcome.Dismissed, ended, "dismissed");
    }

    /// <summary>
    /// Ends every proposal whose lifetime has run out.
    /// </summary>
    /// <remarks>
    /// One at a time, each with its own journal line. An offer nobody answered is the single most
    /// useful thing a week's review can count, and a bulk update would close a dozen of them with
    /// nothing able to say which.
    /// </remarks>
    /// <returns>How many lapsed.</returns>
    public async Task<int> LapseExpiredAsync(CancellationToken token)
    {
        DateTimeOffset now = clock.GetUtcNow();
        int lapsed = 0;

        foreach (Proposal expired in proposals.Expired(now))
        {
            if (token.IsCancellationRequested)
                break;

            if (!proposals.End(expired.Handle, ProposalState.Lapsed, now, by: null))
                continue;

            lapsed++;
            Proposal ended = proposals.Find(expired.Handle)!;

            logger.LogInformation(
                "Nobody answered {Rule}'s offer to {Action} — it expired after {Hours:F1}h.",
                ended.RuleId, ended.Action, (ended.ExpiresAt - ended.StagedAt).TotalHours);

            await emitter.EmitResolvedAsync(ended, token).ConfigureAwait(false);
        }

        return lapsed;
    }

    /// <summary>
    /// How long an unanswered offer from this rule stays redeemable.
    /// </summary>
    /// <remarks>
    /// The rule's own window when it declares one, the host's setting when it does not — the same
    /// arrangement the settle and suppression windows have, and for the same reason: a proposal to
    /// capture a broken state is worth answering all day, where one to roll a server back stops being
    /// the right move once somebody has started working on it by hand.
    /// </remarks>
    private TimeSpan LifetimeOf(RuleDefinition definition) =>
        definition.ProposalLifetime
        ?? TimeSpan.FromHours(Math.Max(_options.ProposalLifetimeHours, 1));

    /// <summary>Why a handle cannot be redeemed, or null when it can.</summary>
    private Redemption? Redeemable(string handle, DateTimeOffset now)
    {
        Proposal? proposal = proposals.Find(handle);

        if (proposal is null)
        {
            return Redemption.Of(RedemptionOutcome.Unknown, "no proposal carries that handle");
        }

        if (proposal.State != ProposalState.Open)
        {
            return Redemption.Of(RedemptionOutcome.AlreadyAnswered,
                $"this was already {ProposalStore.Wire(proposal.State)}"
                + (proposal.AnsweredBy is { } who ? $" by {who}" : string.Empty),
                proposal);
        }

        // Refused on the clock even though the sweep has not closed it yet. The two disagree for at
        // most one sweep interval, and honouring an expired offer for those seconds is the one case
        // where the window would not mean what it says.
        if (now >= proposal.ExpiresAt)
        {
            return Redemption.Of(RedemptionOutcome.Expired,
                $"this expired at {proposal.ExpiresAt:u}", proposal);
        }

        return null;
    }

    /// <summary>
    /// Asks the rule that staged a proposal whether its condition still holds.
    /// </summary>
    /// <remarks>
    /// <b>A rule that is no longer live answers no.</b> Retiring a rule, switching it off, or
    /// deleting it are all statements that this host has stopped wanting what it offered — and honouring
    /// an offer from a rule that no longer exists would let a deleted rule act.
    /// </remarks>
    private async Task<Verdict> ReevaluateAsync(
        Proposal proposal, DateTimeOffset now, CancellationToken token)
    {
        // Read now rather than held: a rule edited while its offer stood is re-derived against what it
        // says today, which is what the re-derivation is for. A rule retired or deleted since resolves
        // to nothing here and the offer answers "no longer applicable" instead of acting.
        RuleDefinition? definition = registry.Current.Rules
            .FirstOrDefault(r => string.Equals(r.Id, proposal.RuleId, StringComparison.Ordinal));

        if (definition is null)
        {
            return Verdict.DoesNotHold(
                $"{proposal.RuleId} is not a rule this host runs any more, so its offer stands on "
                + "nothing");
        }

        try
        {
            Rule rule = RuleEvaluator.ToRule(definition);

            // The opening carried on the offer, not one looked up now. The condition is re-derived
            // against the live world — that is the safety property — but WHEN it began is a fact about
            // the episode this offer was staged for, and a fresh lookup could land on a later one.
            var context = new RuleContext(
                proposal.Subject, now, world, history, footprint, proposal.OpenedAt);
            return await rule.Holds(context, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable, and therefore not confirmed. A world that will not answer is not a world
            // anything should be changed in on the strength of a reading taken hours ago.
            logger.LogError(ex, "Could not re-read {Rule}'s condition about {Subject}.",
                proposal.RuleId, proposal.Subject);
            return Verdict.Unreadable($"the condition could not be re-read: {ex.Message}");
        }
    }

    /// <summary>Ends a proposal without performing anything, and says so on the journal.</summary>
    private async Task<Redemption> EndAsync(
        Proposal proposal, ProposalState state, DateTimeOffset now, string? by, bool? ok,
        string? artifact, string detail, RedemptionOutcome outcome, CancellationToken token)
    {
        if (!proposals.End(proposal.Handle, state, now, by, ok, artifact, detail))
        {
            return Redemption.Of(RedemptionOutcome.AlreadyAnswered,
                "somebody answered this a moment ago", proposals.Find(proposal.Handle));
        }

        Proposal ended = proposals.Find(proposal.Handle)!;

        logger.LogInformation(
            "{Rule}'s offer about {Subject} ended as {State} — {Detail}",
            ended.RuleId, ended.Subject, ProposalStore.Wire(state), detail);

        await emitter.EmitResolvedAsync(ended, token).ConfigureAwait(false);
        return new Redemption(outcome, ended, detail);
    }

    /// <summary>The action a proposal offered, rebuilt from the catalog it was composed from.</summary>
    /// <remarks>
    /// Rebuilt rather than serialized into the row: the catalog is what this build can perform, and an
    /// action name stored months ago that this build no longer has must come back as
    /// <see cref="ReactorAction.Nothing"/> rather than as something reconstructed by hand.
    /// </remarks>
    private static ReactorAction ActionOf(Proposal proposal) =>
        ActionCatalog.Build(proposal.ActionName, proposal.ActionInstance ?? proposal.Subject);

    /// <summary>Invariant 2's actor shape, so an audit row names the rule and never a person.</summary>
    private static string ActorFor(string ruleId) => $"rule:{ruleId}";

    /// <summary>
    /// Whether a caller named itself the way an actor has to be named.
    /// </summary>
    /// <remarks>
    /// <b>No fallback to the OS user, here least of all.</b> This is the one path where a person
    /// authorises something, and an unattributable confirmation would put an action on the host that
    /// nobody can be shown to have asked for.
    /// </remarks>
    private static bool IsActor(string? by)
    {
        if (string.IsNullOrWhiteSpace(by))
            return false;

        int colon = by.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < by.Length - 1;
    }
}
