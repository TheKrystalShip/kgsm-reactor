using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Status;

/// <summary>
/// What a rule would decide about this host right now, without becoming one of its rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>The thing that makes composing a rule safe.</b> A rule assembled from a catalog reads plausibly
/// and can still fire on nothing — a gate set where no instance clears it, a trigger nothing emits, a
/// step ordered after the one that always matches first. None of that is visible in the editor and all
/// of it is visible here: this evaluates the rule as written against the live world and returns the
/// verdict and the exact sentence it would record.
/// </para>
/// <para>
/// ⚠ <b>A read, expressed as a POST because the rule is the question.</b> Nothing is stored, nothing is
/// dispatched, and no decision reaches the ledger or the journal — the gate is not run, because there
/// is no episode to suppress and no ceiling a hypothetical belongs under. The socket stays a place
/// that answers questions rather than one that takes instructions.
/// </para>
/// <para>
/// ⚠ <b>It reports what the rule says, not what it would be allowed to do.</b> A previewed rule asking
/// for an authority this build does not honour still previews; what it would actually be permitted is
/// <c>honours</c> on the catalog, and the panel says so where the mode is chosen.
/// </para>
/// </remarks>
public sealed record RulePreview
{
    /// <summary>
    /// Everything wrong with the rule as written. When this is non-empty nothing was evaluated.
    /// </summary>
    /// <remarks>
    /// The same validator the daemon runs at load, so a rule that previews clean is a rule that will
    /// load — which is the whole point of previewing rather than saving and watching.
    /// </remarks>
    [JsonPropertyName("problems")]
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>What would wake it — <c>edge</c> or <c>state</c> — as derived from its subject source.</summary>
    [JsonPropertyName("shape")]
    public required string Shape { get; init; }

    /// <summary>
    /// How its subjects were arrived at: <c>enumerated</c> by its own subject source, or <c>named</c> by
    /// the caller.
    /// </summary>
    /// <remarks>
    /// An edge rule takes its subject from the event that wakes it, and there is no event here — so a
    /// preview of one is a preview against a subject somebody chose, and the answer says which it was
    /// rather than letting a reader assume the rule found it.
    /// </remarks>
    [JsonPropertyName("subjectsFrom")]
    public required string SubjectsFrom { get; init; }

    /// <summary>What the rule would do about a subject it decided for.</summary>
    [JsonPropertyName("actionName")]
    public required string ActionName { get; init; }

    /// <summary>
    /// Subjects that were not evaluated because the answer was already long enough.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Reported rather than silently dropped.</b> A truncated preview that looked complete would
    /// tell somebody their rule is quiet on a fleet it was never asked about.
    /// </remarks>
    [JsonPropertyName("notEvaluated")]
    public required int NotEvaluated { get; init; }

    /// <summary>One verdict per subject, in the order the rule's subject source produced them.</summary>
    [JsonPropertyName("verdicts")]
    public required IReadOnlyList<PreviewVerdict> Verdicts { get; init; }

    /// <summary>
    /// The most subjects one preview evaluates.
    /// </summary>
    /// <remarks>
    /// A preview reads the supervisor and the monitor once per subject, and a fleet-wide sweep is the
    /// daemon's job on its own schedule rather than something a page view triggers. Past this the answer
    /// is a sample, and it says so.
    /// </remarks>
    public const int MaxSubjects = 25;

    /// <summary>Evaluate a proposed rule against the live world.</summary>
    internal static async Task<RulePreview> RunAsync(
        RuleDefinition definition,
        string? subject,
        IWorldView world,
        IRuleHistory history,
        IFootprintSource footprint,
        DateTimeOffset now,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(definition);

        IReadOnlyList<string> problems = RuleValidation.Problems(definition);
        string shape = definition.Shape.ToString().ToLowerInvariant();
        string action = ActionCatalog.ById(definition.ActionId)?.Id ?? ActionCatalog.None;

        if (problems.Count > 0)
        {
            return new RulePreview
            {
                Problems = problems,
                Shape = shape,
                SubjectsFrom = "none",
                ActionName = action,
                NotEvaluated = 0,
                Verdicts = [],
            };
        }

        // A named subject wins even for a state rule: somebody asking "what would this say about
        // Ketchup" is asking a narrower question than the rule's own enumeration answers, and refusing
        // it would make previewing a fleet-wide rule an all-or-nothing affair.
        IReadOnlyList<string> subjects;
        string from;

        if (!string.IsNullOrWhiteSpace(subject))
        {
            subjects = [subject.Trim()];
            from = "named";
        }
        else
        {
            subjects = await RuleEvaluator
                .SubjectsAsync(definition, new SubjectContext(now, world, history, footprint), token)
                .ConfigureAwait(false);
            from = "enumerated";
        }

        List<PreviewVerdict> verdicts = [];

        foreach (string one in subjects.Take(MaxSubjects))
        {
            Verdict verdict;
            try
            {
                verdict = await RuleEvaluator.EvaluateAsync(
                    definition,
                    new EvaluationScope(one, now, world, history, footprint),
                    token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A rule that throws has decided nothing, and a preview that returned a 500 would tell
                // somebody their host is broken when their rule is.
                verdict = Verdict.Unreadable($"the rule failed while judging: {ex.Message}");
            }

            // ⚠ The catalog's spelling, not the enum's. A panel matches an outcome against what
            // /catalog offered it, and `doesnothold` would match none of them — a preview whose
            // verdicts no surface can classify.
            verdicts.Add(new PreviewVerdict(one, RuleStore.Wire(verdict.Kind), verdict.Reason));
        }

        return new RulePreview
        {
            Problems = [],
            Shape = shape,
            SubjectsFrom = from,
            ActionName = action,
            NotEvaluated = Math.Max(0, subjects.Count - verdicts.Count),
            Verdicts = verdicts,
        };
    }
}

/// <summary>What a previewed rule would conclude about one subject.</summary>
/// <param name="Subject">What it decided about.</param>
/// <param name="Outcome"><c>holds</c>, <c>doesNotHold</c> or <c>unreadable</c>.</param>
/// <param name="Reason">
/// The sentence it would record, with its placeholders filled from the live world — which is what makes
/// a preview worth reading rather than a yes/no.
/// </param>
public sealed record PreviewVerdict(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>What a preview request carries.</summary>
/// <remarks>
/// The rule is the same shape a rules file entry has, so a panel previews exactly the object it is
/// about to save and there is no second schema to keep in step.
/// </remarks>
internal sealed class RulePreviewRequest
{
    /// <summary>The rule to evaluate, in the rules file's own shape.</summary>
    [JsonPropertyName("rule")]
    public RuleDocument? Rule { get; set; }

    /// <summary>
    /// One subject to evaluate against, or absent to let the rule enumerate its own.
    /// </summary>
    /// <remarks>
    /// Required in practice for an edge rule: its subject arrives with an event, and a preview has no
    /// event.
    /// </remarks>
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }
}

/// <summary>
/// The serializer for the preview endpoint.
/// </summary>
/// <remarks>
/// Source-generated, because this binary is Native AOT: there is no reflection fallback, and a type
/// nobody registered throws at runtime rather than degrading.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RulePreview))]
[JsonSerializable(typeof(RulePreviewRequest))]
internal partial class RulePreviewJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
