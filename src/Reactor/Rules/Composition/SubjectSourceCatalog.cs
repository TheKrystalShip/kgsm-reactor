namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// One way of working out what a rule is deciding about.
/// </summary>
/// <remarks>
/// <b>This is where a rule's shape comes from.</b> Taking the subject from the event that woke you is
/// edge-shaped and misses whatever happened while the process was down; enumerating subjects yourself
/// is state-shaped and cannot miss anything, because nothing had to be seen for the condition to be
/// found. The choice is the same choice, so it is made once, here.
/// </remarks>
/// <param name="Id">The stable wire id a rule names it by.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Description">What it enumerates, in an operator's terms.</param>
/// <param name="FromEvent">Whether the subject arrives with the event rather than being enumerated.</param>
/// <param name="Arguments">What it needs supplied.</param>
/// <param name="Enumerate">
/// How it finds its subjects on a sweep. Null exactly when <paramref name="FromEvent"/> is true.
/// </param>
internal sealed record SubjectSource(
    string Id,
    string Label,
    string Description,
    bool FromEvent,
    IReadOnlyList<SignalArgument> Arguments,
    Func<SubjectContext, SignalArguments, CancellationToken, ValueTask<IReadOnlyList<string>>>? Enumerate);

/// <summary>Every way this build can work out what a rule decides about.</summary>
internal static class SubjectSourceCatalog
{
    /// <summary>The subject of the event that woke the rule.</summary>
    public const string FromEvent = "from_event";

    /// <summary>Every instance kgsm-monitor holds a footprint for.</summary>
    public const string InstancesWithFootprint = "instances_with_footprint";

    /// <summary>Subjects with an episode of a given kind currently open.</summary>
    public const string OpenEpisodes = "open_episodes";

    public static IReadOnlyList<SubjectSource> All { get; } =
    [
        new(FromEvent,
            "The subject of the event",
            "Whatever the event that woke the rule was about. A rule built this way is only reached "
            + "when the event arrives, so it misses what happened while this daemon was down.",
            FromEvent: true,
            Arguments: [],
            Enumerate: null),

        new(InstancesWithFootprint,
            "Instances the monitor has measured",
            "Every instance kgsm-monitor holds a footprint for. No monitor means no subjects rather "
            + "than every instance, because an evaluation per instance that can only answer "
            + "\"cannot tell\" is a ledger full of this leaf reporting that another one is missing.",
            FromEvent: false,
            Arguments: [],
            Enumerate: async (ctx, _, token) =>
            {
                var footprints = await ctx.Footprint.AllAsync(token).ConfigureAwait(false);

                return footprints is { State: KGSM.Core.Models.ReadingState.Measured, Value: { } measured }
                    ? [.. measured.Select(f => f.Instance)]
                    : [];
            }),

        new(OpenEpisodes,
            "Subjects with an open episode",
            "Anything an opening event was seen for with no closing event after it. Read from the "
            + "ledger on every sweep, so an episode that began before this daemon started is still "
            + "found, and judged from when it actually opened.",
            FromEvent: false,
            Arguments:
            [
                new("opensWith", "Opens with", ArgumentKind.EventType,
                    Description: "The event that starts an episode."),
                new("closesWith", "Closes with", ArgumentKind.EventType,
                    Description: "The event that ends one."),
                new("withinDays", "Look back", ArgumentKind.Number, Default: "30",
                    Description: "How far back to look. Bounded by the ledger's own retention regardless."),
            ],
            Enumerate: (ctx, args, _) =>
            {
                IReadOnlyList<OpenEpisode> open = ctx.History.OpenEpisodes(
                    args.Text("opensWith"),
                    args.Text("closesWith"),
                    ctx.Now - TimeSpan.FromDays(args.Number("withinDays", 30)));

                return ValueTask.FromResult<IReadOnlyList<string>>([.. open.Select(e => e.Subject)]);
            }),
    ];

    public static SubjectSource? ById(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}

/// <summary>One thing a rule may declare it would do.</summary>
/// <remarks>
/// ⚠ <b>The catalog is a rendering of <see cref="ReactorAction"/>, never a widening of it.</b> What a
/// composed rule may do stays a compiler question: the union's constructor is private, so nothing
/// outside its own declaration can add a case, and a rule naming an action this list does not hold is
/// refused at load. That is the never-list — never uninstall, never delete a backup, never rewrite
/// instance config, never moderate a player — expressed one level up from where it was already
/// enforced.
/// </remarks>
/// <param name="Id">The stable wire id, matching <see cref="ReactorAction.Name"/>.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Description">What it does, in an operator's terms.</param>
/// <param name="Create">Builds it for one subject.</param>
internal sealed record ActionEntry(
    string Id,
    string Label,
    string Description,
    Func<string, ReactorAction> Create)
{
    /// <summary>
    /// What performing it costs, and whether it can be taken back.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Read off the action itself rather than restated here, and it must not name the
    /// instance.</b> What an action does to a server is a property of the action on every host that
    /// has it — a second copy in this catalog would be the one somebody forgets to change, and it is
    /// read by an editor that has no instance to build one for. <c>ActionCatalogTests</c> holds the
    /// contract by asking two different instances for the same sentence.
    /// </remarks>
    public string Consequence => Create(string.Empty).Consequence;
}

/// <summary>Everything this build can do about a rule that fires.</summary>
internal static class ActionCatalog
{
    public const string None = "none";
    public const string CreateBackup = "create_backup";
    public const string ProposeRestore = "propose_restore";

    public static IReadOnlyList<ActionEntry> All { get; } =
    [
        new(None, "Report only",
            "Record the decision and do nothing else. The decision record is the whole output.",
            _ => new ReactorAction.Nothing()),

        new(CreateBackup, "Archive it as it stands",
            "Capture the instance exactly as it is, as a pinned archive — the state somebody debugging "
            + "it will want and the running server will not keep. Only ever creates, so a false "
            + "positive costs disk rather than a running server.",
            instance => new ReactorAction.CreateBackup(instance)),

        new(ProposeRestore, "Roll it back",
            "Restore the archive taken before the update that preceded the failure. Overwrites live "
            + "state irreversibly, so it is offered for a person to authorise and never performed on "
            + "the host's own initiative.",
            instance => new ReactorAction.ProposeRestore(instance)),
    ];

    public static ActionEntry? ById(string id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The action <paramref name="id"/> names, for <paramref name="instance"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A name this build does not hold builds <see cref="ReactorAction.Nothing"/>.</b> An offer
    /// staged months ago names its action as a string, and the catalog is what this build can actually
    /// perform — reconstructing a missing one by hand would let a redemption carry out something the
    /// never-list has since been narrowed to exclude.
    /// </remarks>
    public static ReactorAction Build(string id, string instance) =>
        ById(id)?.Create(instance) ?? new ReactorAction.Nothing();
}
