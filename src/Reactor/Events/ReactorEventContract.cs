using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Events;

/// <summary>
/// What the reactor writes to its own journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>These describe the decision, not the incident.</b> The incident is already a line in some
/// producer's journal, and every decision carries the position of that line — so a reader can go and
/// check what the reactor judged instead of taking this leaf's word for what happened.
/// </para>
/// <para>
/// <b>Four events, not fewer.</b> A verdict, an offer, an offer's end, and an action this leaf took
/// on nobody's request are four immutable facts. Collapse any pair and a real case loses its
/// spelling: "it decided and the action failed", or "it offered and nobody answered".
/// </para>
/// </remarks>
internal static class ReactorEvents
{
    /// <summary>
    /// <c>reactor.decided</c> — a rule reached a verdict about a subject.
    /// </summary>
    /// <remarks>
    /// <b>Written on a transition, not on an evaluation.</b> A state rule re-evaluates one episode
    /// every sweep and the ledger folds those into a single row that gets better informed; a journal
    /// appends. Emitting per evaluation would write a line every thirty seconds about a condition that
    /// has not changed, and the history would read as how often the reactor looked rather than what it
    /// concluded.
    /// </remarks>
    public const string Decided = "reactor.decided";

    /// <summary>The same name, typed so the writer can take it.</summary>
    /// <remarks>
    /// Derived from the constant above rather than restated: the name this leaf writes and the name
    /// its own rules refuse to wake on cannot become two different strings.
    /// </remarks>
    public static readonly EventName DecidedName = EventName.Parse(Decided);

    /// <summary>
    /// <c>reactor.proposed</c> — a rule staged an action for a person to confirm.
    /// </summary>
    /// <remarks>
    /// <b>An offer, and nothing has been done.</b> The action is held under a handle until somebody
    /// redeems it or its lifetime runs out, and the commonest ending is that the condition resolves
    /// itself and nobody ever answers. A line here is not a line about work.
    /// </remarks>
    public const string Proposed = "reactor.proposed";

    /// <summary>The same name, typed so the writer can take it.</summary>
    public static readonly EventName ProposedName = EventName.Parse(Proposed);

    /// <summary>
    /// <c>reactor.resolved</c> — a staged proposal reached its end, whichever end that was.
    /// </summary>
    /// <remarks>
    /// <b>Exactly one per proposal, including the ones nobody answered.</b> A lapse is a fact and is
    /// written like the rest — an offer that expired unread is the single most useful thing a week's
    /// review can count, and it exists nowhere unless it is said.
    /// </remarks>
    public const string Resolved = "reactor.resolved";

    /// <summary>The same name, typed so the writer can take it.</summary>
    public static readonly EventName ResolvedName = EventName.Parse(Resolved);

    /// <summary>
    /// <c>reactor.acted</c> — this leaf carried an action out itself, however it ended.
    /// </summary>
    /// <remarks>
    /// <b>Autonomous, with nobody behind it.</b> An action a person confirmed is a
    /// <see cref="Resolved"/> carrying their name. This is the one where the rule is the whole
    /// authority, and keeping the two apart is what lets a surface answer "what did this host do on its
    /// own" without subtracting one set from another.
    /// </remarks>
    public const string Acted = "reactor.acted";

    /// <summary>The same name, typed so the writer can take it.</summary>
    public static readonly EventName ActedName = EventName.Parse(Acted);

    /// <summary>The prefix every event this leaf writes shares.</summary>
    /// <remarks>
    /// The reactor tails every producer's journal, its own included, so what it writes comes back to
    /// it. This prefix is how that is recognised: it is absent from the trigger catalog, and a rule
    /// naming one is refused at load rather than left to loop.
    /// </remarks>
    public const string Prefix = "reactor.";
}

/// <summary>The payload field names, spelled once.</summary>
/// <remarks>
/// <b>A field name is a contract the moment something reads it.</b> These are free to change while
/// the reactor is the only thing that has ever written or read them; once kgsm-lib carries the typed
/// classes and a consumer deserializes one, renaming a field silently empties it for every reader
/// built against the old spelling.
/// </remarks>
internal static class ReactorEventFields
{
    /// <summary>The rule that decided. Also the actor an audit row would carry.</summary>
    public const string Rule = "Rule";

    /// <summary>
    /// Who had shaped that rule when it decided, as <c>provider:name</c>, or absent when nobody is
    /// known to have.
    /// </summary>
    /// <remarks>
    /// <b>Beside the actor, never instead of it.</b> The rule performed the act; a person wrote the
    /// rule. A consumer renders <em>"stopped by rule <c>disk_pressure_stop</c>, written by
    /// <c>discord:tanya</c>"</em> — which names the act and its origin without confusing the two.
    /// </remarks>
    /// <remarks>
    /// <b>Absent means unattributed, and unattributed is a real state.</b> A rule this build seeded,
    /// or one hand-written into the file over SSH, carries no identity — and there is no fallback to
    /// the OS user anywhere in this ecosystem. A consumer must render its absence rather than
    /// substituting the host, the daemon, or the person who happens to be reading.
    /// </remarks>
    public const string RuleAuthor = "RuleAuthor";

    /// <summary>What was judged: a server name, a sensor reference, a component.</summary>
    public const string Subject = "Subject";

    /// <summary>
    /// What sort of thing the subject is — <c>instance</c>, <c>host</c>, <c>leaf</c>, <c>unknown</c>.
    /// </summary>
    /// <remarks>
    /// Carried rather than left to be derived, because a consumer that derives it does so by looking
    /// the name up and seeing what it finds. kgsm-bot routes on the instance name; a host-scoped
    /// subject has no channel to follow, and it needs to know that rather than discover it by failing
    /// to find one.
    /// </remarks>
    public const string SubjectKind = "SubjectKind";

    /// <summary>How loudly the rule speaks — one of the ecosystem's severity spellings.</summary>
    public const string Severity = "Severity";

    /// <summary>The authority it ran under: <c>observe</c>, <c>propose</c>, <c>act</c>.</summary>
    public const string Mode = "Mode";

    /// <summary>
    /// What was decided: <c>fired</c>, <c>settled</c>, <c>suppressed</c>, <c>ceilinged</c>,
    /// <c>superseded</c>, <c>unreadable</c>.
    /// </summary>
    public const string Outcome = "Outcome";

    /// <summary>Why, in one line. Always present.</summary>
    public const string Reason = "Reason";

    /// <summary>What the rule would do: <c>none</c>, <c>create_backup</c>, <c>propose_restore</c>.</summary>
    public const string Action = "Action";

    /// <summary>
    /// The server the action would operate on, or null when it operates on none.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Subject"/>, which is what was judged. They are the same string for
    /// today's three rules and will not stay that way: a rule judging a host sensor can still propose
    /// something about a server.
    /// </remarks>
    public const string ActionInstance = "ActionInstance";

    /// <summary>The decision's own identity, which <c>reactor.acted</c> refers back to.</summary>
    public const string DecisionId = "DecisionId";

    /// <summary>When the condition opened. The envelope's timestamp is when it was decided.</summary>
    public const string OpenedAt = "OpenedAt";

    /// <summary>Whose journal the originating line is in.</summary>
    public const string SourceProducer = "SourceProducer";

    /// <summary>Which segment file of it.</summary>
    public const string SourceSegment = "SourceSegment";

    /// <summary>The byte offset in that segment.</summary>
    public const string SourceOffset = "SourceOffset";

    /// <summary>
    /// The id the originating line's producer minted for it, or null when that line carries none.
    /// </summary>
    /// <remarks>
    /// Beside the position rather than instead of it, and both are needed. The position <em>finds</em>
    /// the line, cheaply, without reading a segment end to end; the id <em>proves</em> it is the right
    /// one. A consumer that follows the pointer and finds the two disagree has caught a rewritten
    /// segment, where following the position alone would hand it a real, parseable event of the wrong
    /// kind with nothing to notice.
    /// </remarks>
    public const string SourceEventId = "SourceEventId";

    /// <summary>
    /// The token a staged proposal is redeemed with.
    /// </summary>
    /// <remarks>
    /// <b>Spelled in full because a bare <c>Handle</c> already means a person.</b> An account event
    /// carries one and it is somebody's name; this is a capability that names nobody, and one field
    /// name standing for both would leave every consumer classifying whichever it met first.
    /// </remarks>
    public const string ProposalHandle = "ProposalHandle";

    /// <summary>When an unanswered proposal stops being redeemable.</summary>
    public const string ExpiresAt = "ExpiresAt";

    /// <summary>How a proposal ended — see <see cref="ReactorResolutions"/>.</summary>
    public const string Resolution = "Resolution";

    /// <summary>Who answered, as <c>provider:name</c>, or null when nobody did.</summary>
    public const string AnsweredBy = "AnsweredBy";

    /// <summary>Whether the action succeeded, or null when none was attempted.</summary>
    public const string Ok = "Ok";

    /// <summary>What the action produced — a backup id — or null.</summary>
    public const string Artifact = "Artifact";

    /// <summary>What went wrong, or what else is worth reading.</summary>
    public const string Detail = "Detail";
}

/// <summary>
/// The four ways a staged proposal ends.
/// </summary>
/// <remarks>
/// Kept apart rather than folded into confirmed-or-not, because each says something different about
/// the rule that staged it: mostly confirmed is a candidate for acting on its own, mostly dismissed
/// has a wrong condition, mostly lapsed is unwanted, and mostly stale speaks too early. A consumer
/// counting "not confirmed" loses the only signal that separates them.
/// </remarks>
internal static class ReactorResolutions
{
    /// <summary>A person said yes, and the action was attempted.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>A person said no. Nothing was attempted.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>Nobody answered before the lifetime ran out.</summary>
    public const string Lapsed = "lapsed";

    /// <summary>
    /// Somebody tried to confirm it and the condition had gone by then.
    /// </summary>
    /// <remarks>
    /// <b>The safety property observed working.</b> The rule is re-evaluated at redemption rather
    /// than trusted from staging, so a server that came back up on its own resolves the proposal
    /// instead of having a restore run over it.
    /// </remarks>
    public const string NoLongerApplicable = "no_longer_applicable";
}
