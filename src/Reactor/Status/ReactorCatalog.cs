using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Status;

/// <summary>
/// Everything a rule can be assembled from on this build.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a panel needs in order to render an editor without holding a copy of the catalogs.</b> A
/// surface that shipped its own list would go on offering a signal after the build that measures it
/// was replaced, and refusing one after a build that added it was deployed. The leaf is the only thing
/// that knows what it can measure, so the leaf says.
/// </para>
/// <para>
/// ⚠ <b>Read-only, and this is where the write boundary sits.</b> Publishing what a rule may be made of
/// is not the same as accepting one over a socket: composing and storing is the panel's half, which
/// writes the file and restarts the unit through the grant it already holds. Nothing off this host
/// acquires the ability to tell a leaf what to think.
/// </para>
/// <para>
/// ⚠ <b>The trigger list is deliberately absent.</b> What a rule may wake on is what the ledger has
/// actually observed — with each type's producer and how often it fires — and that is a query against
/// this host's own history rather than a constant. A hand-written list would drift from it the first
/// time any producer emitted something new. It is served beside the decision review, where the ledger
/// already is.
/// </para>
/// </remarks>
public sealed record ReactorCatalog
{
    /// <summary>Everything a rule can ask about the world.</summary>
    [JsonPropertyName("signals")]
    public required IReadOnlyList<SignalInfo> Signals { get; init; }

    /// <summary>Every way a rule can work out what it decides about.</summary>
    [JsonPropertyName("subjectSources")]
    public required IReadOnlyList<SubjectSourceInfo> SubjectSources { get; init; }

    /// <summary>Everything a rule that fires can do.</summary>
    [JsonPropertyName("actions")]
    public required IReadOnlyList<ActionInfo> Actions { get; init; }

    /// <summary>
    /// How a clause can compare, in the wire spellings the file uses.
    /// </summary>
    /// <remarks>
    /// Served rather than assumed, so a panel cannot offer an operator the leaf will refuse to read
    /// back. Each says which signal kinds it applies to, because asking whether a duration contains a
    /// piece of text is not a comparison anybody meant to make.
    /// </remarks>
    [JsonPropertyName("operators")]
    public required IReadOnlyList<OperatorInfo> Operators { get; init; }

    /// <summary>
    /// The outcomes a step may conclude, in their wire spellings.
    /// </summary>
    /// <remarks>
    /// ⚠ There are three, and the third is the point. "Cannot tell" must not be able to masquerade as
    /// "no", which would be silence, or as "yes", which would be acting blind.
    /// </remarks>
    [JsonPropertyName("outcomes")]
    public required IReadOnlyList<OutcomeInfo> Outcomes { get; init; }

    /// <summary>
    /// The most authority this build will honour, whatever a rule asks for.
    /// </summary>
    /// <remarks>
    /// Reported so a panel does not have to know which phases exist. A page that hard-coded "this
    /// build only observes" would go on saying it after the build that acts is deployed.
    /// </remarks>
    [JsonPropertyName("honours")]
    public required string Honours { get; init; }

    /// <summary>The placeholders a step's sentence may carry.</summary>
    [JsonPropertyName("placeholders")]
    public required IReadOnlyList<PlaceholderInfo> Placeholders { get; init; }

    /// <summary>
    /// The ways a staged offer ends, in their wire spellings.
    /// </summary>
    /// <remarks>
    /// ⚠ Four, and keeping them apart is the point. A surface folding lapse, dismissal and no-longer-
    /// applicable into "not confirmed" loses the only signal that separates a rule nobody wants from
    /// one whose condition is wrong from one that speaks too early — which is what somebody reviewing
    /// a week of a proposing rule is trying to read.
    /// </remarks>
    [JsonPropertyName("resolutions")]
    public required IReadOnlyList<OutcomeInfo> Resolutions { get; init; }

    /// <summary>
    /// How long an unanswered offer stays redeemable when a rule names no window of its own.
    /// </summary>
    /// <remarks>
    /// Published so an editor can show what a blank field means. ⚠ It is not a safety control: the
    /// condition is re-derived at redemption, so a stale offer answers "no longer applicable" instead
    /// of executing.
    /// </remarks>
    [JsonPropertyName("proposalLifetimeHours")]
    public required int ProposalLifetimeHours { get; init; }

    /// <summary>Assemble it from the catalogs this build compiles in.</summary>
    public static ReactorCatalog Read() => new()
    {
        Signals =
        [
            .. SignalCatalog.All.Select(s => new SignalInfo(
                s.Id, s.Label, s.Kind.ToString().ToLowerInvariant(), s.Unit, s.Description,
                [.. s.Arguments.Select(Describe)])),
        ],
        SubjectSources =
        [
            .. SubjectSourceCatalog.All.Select(s => new SubjectSourceInfo(
                s.Id, s.Label, s.Description, s.FromEvent,
                s.FromEvent ? "edge" : "state",
                [.. s.Arguments.Select(Describe)])),
        ],
        Actions =
        [
            .. ActionCatalog.All.Select(a => new ActionInfo(
                a.Id, a.Label, a.Description, a.Create("an-instance").ChangesServerState)),
        ],
        Operators = Operators_,
        Outcomes =
        [
            new(RuleStore.Wire(VerdictKind.Holds), "Yes",
                "The condition is true right now, and the rule would act on it."),
            new(RuleStore.Wire(VerdictKind.DoesNotHold), "No",
                "The condition is false right now. Usually because it resolved itself."),
            new(RuleStore.Wire(VerdictKind.Unreadable), "Cannot tell",
                "Nothing could be read, or what was read is not enough to decide on. Never treated as "
                + "a no."),
        ],
        Honours = Engine.RuleEngine.Honours.ToString().ToLowerInvariant(),
        Resolutions =
        [
            new(ReactorResolutions.Confirmed, "Confirmed",
                "A person said yes. Whether the action then worked is a separate answer."),
            new(ReactorResolutions.Dismissed, "Dismissed",
                "A person said no. Nothing was attempted, and the world was not re-read to decide it."),
            new(ReactorResolutions.Lapsed, "Lapsed",
                "Nobody answered before it expired. A rule whose offers mostly end here is one nobody "
                + "wants."),
            new(ReactorResolutions.NoLongerApplicable, "No longer applicable",
                "Somebody confirmed it and the condition had gone by then, so nothing was done. The "
                + "safety property working, not a fault."),
        ],
        ProposalLifetimeHours = ReactorOptions.DefaultProposalLifetimeHours,
        Placeholders =
        [
            new("{subject}", "What is being decided about."),
            new("{settleSeconds}", "How long the rule waited before judging."),
            new("{reason}", "The reader's own words. Only in the sentence for an unreadable signal."),
            new("{alias}", "A signal's value, written for a person to read."),
            new("{alias:F1}", "The same, with a .NET numeric format."),
            new("{alias#}", "What this step compares that signal against."),
            new("{alias@key}", "An argument the signal was bound with."),
        ],
    };

    private static ArgumentInfo Describe(SignalArgument argument) => new(
        argument.Key, argument.Label, argument.Kind.ToString().ToLowerInvariant(),
        argument.Required, argument.Default, argument.Description);

    private static readonly IReadOnlyList<OperatorInfo> Operators_ =
    [
        new(RuleStore.Wire(ClauseOperator.LessThan), "is below", ["number", "duration"], true),
        new(RuleStore.Wire(ClauseOperator.AtMost), "is at most", ["number", "duration"], true),
        new(RuleStore.Wire(ClauseOperator.GreaterThan), "is above", ["number", "duration"], true),
        new(RuleStore.Wire(ClauseOperator.AtLeast), "is at least", ["number", "duration"], true),
        new(RuleStore.Wire(ClauseOperator.EqualTo), "is", ["number", "duration", "text"], true),
        new(RuleStore.Wire(ClauseOperator.NotEqualTo), "is not", ["number", "duration", "text"], true),
        new(RuleStore.Wire(ClauseOperator.Contains), "contains", ["text"], true),
        new(RuleStore.Wire(ClauseOperator.IsTrue), "is true", ["flag"], false),
        new(RuleStore.Wire(ClauseOperator.IsFalse), "is false", ["flag"], false),
        // ⚠ These two ask about a measurement that came back empty, which is not the same question as
        // whether it could be read at all. A signal that cannot be read ends the rule as "cannot tell"
        // and never reaches a comparison.
        new(RuleStore.Wire(ClauseOperator.Present), "has a value",
            ["number", "duration", "text", "flag", "instant"], false),
        new(RuleStore.Wire(ClauseOperator.Absent), "has none",
            ["number", "duration", "text", "flag", "instant"], false),
    ];
}

/// <summary>One thing a rule can ask about the world.</summary>
/// <param name="Id">The stable wire id a rule names it by.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Kind">What sort of value it produces, which decides what may be asked of it.</param>
/// <param name="Unit">Display suffix, or null when the number is a count.</param>
/// <param name="Description">What it means, in an operator's terms.</param>
/// <param name="Arguments">What must be supplied before it can read. Empty for most.</param>
public sealed record SignalInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("args")] IReadOnlyList<ArgumentInfo> Arguments);

/// <summary>One argument a signal or subject source needs.</summary>
/// <param name="Key">The stable wire id it is written under.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Kind">What sort of value it takes.</param>
/// <param name="Required">Whether a rule must supply it.</param>
/// <param name="Default">What is used when nothing supplies one, or null when it is required.</param>
/// <param name="Description">What it changes, in an operator's terms.</param>
public sealed record ArgumentInfo(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("default")] string? Default,
    [property: JsonPropertyName("description")] string? Description);

/// <summary>One way of working out what a rule decides about.</summary>
/// <param name="Id">The stable wire id a rule names it by.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Description">What it enumerates, in an operator's terms.</param>
/// <param name="FromEvent">Whether the subject arrives with the event rather than being enumerated.</param>
/// <param name="Shape">
/// The shape a rule built on it has — <c>edge</c> or <c>state</c>. Served because it is a consequence
/// of this choice rather than a separate one, and a panel offering both would be offering a
/// contradiction.
/// </param>
/// <param name="Arguments">What it needs supplied.</param>
public sealed record SubjectSourceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("fromEvent")] bool FromEvent,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("args")] IReadOnlyList<ArgumentInfo> Arguments);

/// <summary>One thing a rule that fires can do.</summary>
/// <param name="Id">The stable wire id, matching what a decision records.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Description">What it does, in an operator's terms.</param>
/// <param name="ChangesServerState">
/// Whether performing it changes the server rather than only adding something beside it. Served
/// because it is what a person is really being asked to weigh, and because the composition gate
/// exempts the additive ones.
/// </param>
public sealed record ActionInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("changesServerState")] bool ChangesServerState);

/// <summary>One way a clause can compare.</summary>
/// <param name="Id">The wire spelling the file uses.</param>
/// <param name="Label">How it reads in a sentence.</param>
/// <param name="Kinds">The signal kinds it applies to.</param>
/// <param name="NeedsComparand">Whether it must be given something to compare against.</param>
public sealed record OperatorInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kinds")] IReadOnlyList<string> Kinds,
    [property: JsonPropertyName("needsComparand")] bool NeedsComparand);

/// <summary>One outcome a step may conclude.</summary>
/// <param name="Id">The wire spelling the file uses.</param>
/// <param name="Label">How it reads to a person.</param>
/// <param name="Description">What concluding it means.</param>
public sealed record OutcomeInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string Description);

/// <summary>One placeholder a step's sentence may carry.</summary>
/// <param name="Token">How it is written.</param>
/// <param name="Description">What it fills in with.</param>
public sealed record PlaceholderInfo(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("description")] string Description);

/// <summary>
/// The serializer for the catalog endpoint.
/// </summary>
/// <remarks>
/// Source-generated, because this binary is Native AOT: there is no reflection fallback, and a type
/// nobody registered throws at runtime rather than degrading.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ReactorCatalog))]
public partial class ReactorCatalogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
