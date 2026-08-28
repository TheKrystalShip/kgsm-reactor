using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

// ---- the file, as it sits on disk ----

/// <summary>One clause, on the wire.</summary>
internal sealed class ClauseDocument
{
    /// <summary>The binding being read.</summary>
    [JsonPropertyName("signal")]
    public string Signal { get; set; } = string.Empty;

    /// <summary>How it is compared: <c>lt</c>, <c>gt</c>, <c>isTrue</c>, <c>absent</c>, and so on.</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    /// <summary>A number to compare against. This is what a threshold now is.</summary>
    [JsonPropertyName("value")]
    public double? Value { get; set; }

    /// <summary>Text to compare against.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Another of this rule's bindings to compare against, instead of a fixed figure.</summary>
    [JsonPropertyName("vsSignal")]
    public string? VsSignal { get; set; }
}

/// <summary>One step of a rule's decision, on the wire.</summary>
internal sealed class GuardRowDocument
{
    /// <summary>Everything that must hold. Absent or empty always holds, which is a default step.</summary>
    [JsonPropertyName("when")]
    public List<ClauseDocument> When { get; set; } = [];

    /// <summary><c>holds</c>, <c>doesNotHold</c> or <c>unreadable</c>.</summary>
    [JsonPropertyName("then")]
    public string Then { get; set; } = string.Empty;

    /// <summary>What it records, with <c>{alias}</c> placeholders.</summary>
    [JsonPropertyName("say")]
    public string Say { get; set; } = string.Empty;

    /// <summary>What it records when something it needs cannot be read. <c>{reason}</c> carries the reader's words.</summary>
    [JsonPropertyName("sayWhenUnreadable")]
    public string? SayWhenUnreadable { get; set; }
}

/// <summary>A signal bound to its arguments, on the wire.</summary>
internal sealed class SignalBindingDocument
{
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("signal")]
    public string Signal { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public Dictionary<string, string> Args { get; set; } = [];
}

/// <summary>Where a rule's subjects come from, on the wire.</summary>
internal sealed class SubjectDocument
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = SubjectSourceCatalog.FromEvent;

    [JsonPropertyName("args")]
    public Dictionary<string, string> Args { get; set; } = [];
}

/// <summary>Who shaped a rule, on the wire.</summary>
internal sealed class AuthorshipDocument
{
    /// <summary><c>provider:name</c>, and the stable username rather than a display name.</summary>
    [JsonPropertyName("actor")]
    public string Actor { get; set; } = string.Empty;

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }
}

/// <summary>One rule, on the wire.</summary>
internal sealed class RuleDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("wakes")]
    public List<string> Wakes { get; set; } = [];

    [JsonPropertyName("subjects")]
    public SubjectDocument? Subjects { get; set; }

    [JsonPropertyName("signals")]
    public List<SignalBindingDocument> Signals { get; set; } = [];

    [JsonPropertyName("rows")]
    public List<GuardRowDocument> Rows { get; set; } = [];

    [JsonPropertyName("default")]
    public GuardRowDocument? Default { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = ActionCatalog.None;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    [JsonPropertyName("settleSeconds")]
    public int SettleSeconds { get; set; }

    /// <summary>Its own quiet window, or absent to follow the host-wide one.</summary>
    [JsonPropertyName("suppressionMinutes")]
    public int? SuppressionMinutes { get; set; }

    /// <summary>
    /// How long an unanswered offer from this rule stays redeemable, or absent to follow the host-wide
    /// setting.
    /// </summary>
    /// <remarks>
    /// ⚠ Not a safety control. The condition is re-derived at redemption, so a stale offer answers "no
    /// longer applicable" instead of executing. What this is for is the difference between offers.
    /// </remarks>
    [JsonPropertyName("proposalLifetimeHours")]
    public int? ProposalLifetimeHours { get; set; }

    /// <summary><c>off</c>, <c>observe</c>, <c>propose</c> or <c>act</c>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "observe";

    /// <summary>Deleted, definition kept so its decisions still resolve to a rule that can be named.</summary>
    [JsonPropertyName("retired")]
    public bool Retired { get; set; }

    [JsonPropertyName("createdBy")]
    public AuthorshipDocument? CreatedBy { get; set; }

    [JsonPropertyName("updatedBy")]
    public AuthorshipDocument? UpdatedBy { get; set; }
}

[JsonSourceGenerationOptions(
    // Hand-editable over SSH as well as panel-written, so it tolerates what a person writes: comments
    // explaining why a rule exists, and a trailing comma after the last one.
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuleDocument))]
internal sealed partial class RulesJsonContext : JsonSerializerContext;

/// <summary>
/// What was loaded, and everything that could not be.
/// </summary>
/// <param name="Rules">The rules that can run, in file order.</param>
/// <param name="Retired">
/// Rules kept only so their decisions still resolve. Never evaluated, never in the live list.
/// </param>
/// <param name="Problems">
/// ⚠ <b>The most important field here for anyone who has just written a rule.</b> A misspelled signal,
/// a step with no sentence, an action this build cannot do — each leaves the daemon running on the
/// rules it could honour, and without this all of them present as "I saved it and nothing happened".
/// </param>
internal sealed record RuleSet(
    IReadOnlyList<RuleDefinition> Rules,
    IReadOnlyList<RuleDefinition> Retired,
    IReadOnlyList<string> Problems);

/// <summary>
/// Reads the rules a host runs, one file per rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>A directory of one-rule files, and nothing else is a source.</b> No rule exists in code: the
/// samples this build ships are ordinary files installed into the state directory at setup, read
/// through this loader like any other. That is deliberate — a rule defined in code would never
/// travel through the parser or the validator, leaving the path every hand-written rule depends on
/// exercised only by hand-written rules.
/// </para>
/// <para>
/// <b>Leaf-owned even when the panel writes it</b>, which is what keeps this leaf standalone. The
/// directory sits inside this daemon's own state directory, so a host with no kgsm-api reads and
/// writes it directly. A panel edits a rule by asking the leaf to, never by reaching into the
/// directory itself — no leaf depends on the API, and this does not become the first one that does.
/// </para>
/// <para>
/// <b>A file that cannot be read costs one rule, not the set.</b> Each is parsed on its own, so a
/// typo in one leaves the rest running and names the file that has to be fixed.
/// </para>
/// <para>
/// <b>An empty directory means no rules</b>, which is a legitimate state and not a fault. A host
/// that deletes every sample gets a reactor that observes nothing and says so.
/// </para>
/// </remarks>
internal static class RuleStore
{
    /// <summary>The extension a rule file carries. Anything else in the directory is ignored.</summary>
    public const string RuleFileExtension = ".json";

    /// <summary>Load every rule in a directory. Missing or empty yields no rules and no problems.</summary>
    public static RuleSet LoadDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Resolve([], []);

        List<string> files;
        try
        {
            // Sorted so a duplicate id is refused against the same file on every host, rather than
            // against whichever one the filesystem happened to hand back first.
            files = [.. Directory.EnumerateFiles(directory, "*" + RuleFileExtension).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Resolve([], [$"{directory} could not be listed: {ex.Message} — no rules are running"]);
        }

        List<string> problems = [];
        List<RuleDefinition> parsed = [];

        foreach (string file in files)
        {
            RuleDefinition? definition = ReadFile(file, problems);
            if (definition is not null)
                parsed.Add(definition);
        }

        return Resolve(parsed, problems);
    }

    /// <summary>
    /// One rule file, or null with the reason recorded.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The filename is not the id.</b> The id inside the file is what everything keys on, and it
    /// is checked against the filename rather than derived from it — a file somebody copied and
    /// renamed without editing would otherwise install a second rule under the first one's identity,
    /// silently folding two rules' decisions together.
    /// </remarks>
    private static RuleDefinition? ReadFile(string path, List<string> problems)
    {
        string stem = Path.GetFileNameWithoutExtension(path);

        try
        {
            using FileStream stream = File.OpenRead(path);
            RuleDocument? document = JsonSerializer.Deserialize(stream, RulesJsonContext.Default.RuleDocument);

            if (document is null)
            {
                problems.Add($"{stem} holds no object, so it defines no rule");
                return null;
            }

            if (!string.Equals(document.Id, stem, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{stem}{RuleFileExtension} declares the id '{document.Id}' — a rule file is named "
                    + "for the id inside it, and an id is the actor on every decision the rule makes");
                return null;
            }

            return Read(document, problems);
        }
        catch (JsonException ex)
        {
            // The position is the whole value of this message: "line 7, position 22" is the difference
            // between a fixable typo and a file somebody rewrites from scratch.
            problems.Add(
                $"{stem}{RuleFileExtension} could not be parsed at line {ex.LineNumber}, position "
                + $"{ex.BytePositionInLine}: {ex.Message} — that rule is not running");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{stem}{RuleFileExtension} could not be read: {ex.Message} — that rule is not running");
            return null;
        }
    }

    /// <summary>Validate a set, separate the retired, and refuse what cannot be honoured.</summary>
    /// <remarks>
    /// ⚠ <b>A duplicate id is refused rather than resolved.</b> The id is the actor on every line a
    /// rule produced and the key its decisions are folded on, so two rules sharing one would silently
    /// merge their records — and picking a winner would make which of them ran depend on file order.
    /// </remarks>
    public static RuleSet Resolve(IReadOnlyList<RuleDefinition> definitions, IReadOnlyList<string> loadProblems)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        List<string> problems = [.. loadProblems];
        List<RuleDefinition> live = [];
        List<RuleDefinition> retired = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (RuleDefinition definition in definitions)
        {
            IReadOnlyList<string> found = RuleValidation.Problems(definition);
            if (found.Count > 0)
            {
                problems.AddRange(found);
                continue;
            }

            // Uniqueness spans retired rules: an id that resolved to one rule last year and another
            // one now is worse than no name at all.
            if (!seen.Add(definition.Id))
            {
                problems.Add(
                    $"'{definition.Id}' names more than one rule — the second was ignored. An id is the "
                    + "actor on every decision a rule made, including a retired one's");
                continue;
            }

            if (definition.Retired)
                retired.Add(definition);
            else
                live.Add(definition);
        }

        return new RuleSet(live, retired, problems);
    }

    /// <summary>Load a directory, and log whatever could not be honoured.</summary>
    public static RuleSet LoadDirectory(string directory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        RuleSet set = LoadDirectory(directory);

        foreach (string problem in set.Problems)
            logger.LogWarning("rules: {Problem}", problem);

        return set;
    }

    /// <summary>One rule as its file, which is the only shape a rule is ever stored in.</summary>
    public static string Write(RuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return JsonSerializer.Serialize(ToDocument(definition), RulesJsonContext.Default.RuleDocument);
    }

    /// <summary>The path a rule occupies in a directory. The id names the file.</summary>
    public static string PathOf(string directory, string ruleId) =>
        Path.Combine(directory, ruleId + RuleFileExtension);

    /// <summary>
    /// One rule as it is written, turned into one this build can evaluate.
    /// </summary>
    /// <remarks>
    /// Shared with the preview endpoint, so a rule is read the same way whether it is being loaded or
    /// being tried out — a preview that parsed differently from the loader would be a rehearsal of a
    /// different rule.
    /// </remarks>
    public static RuleDefinition FromDocument(RuleDocument document, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(problems);

        return Read(document, problems);
    }

    private static RuleDefinition Read(RuleDocument document, List<string> problems)
    {
        SubjectDocument subjects = document.Subjects ?? new SubjectDocument();

        return new RuleDefinition(
            Id: document.Id,
            Name: document.Name,
            Wakes: document.Wakes,
            SubjectSource: subjects.Source,
            SubjectArguments: Insensitive(subjects.Args),
            Signals: [.. document.Signals.Select(s =>
                new SignalBinding(s.Alias, s.Signal, Insensitive(s.Args)))],
            Rows: [.. document.Rows.Select(r => Row(document.Id, r, problems))],
            Default: Row(document.Id, document.Default ?? Fallback, problems),
            ActionId: document.Action,
            Severity: Enum.TryParse(document.Severity, ignoreCase: true, out EventSeverity severity)
                ? severity
                : EventSeverity.Info,
            Settle: TimeSpan.FromSeconds(document.SettleSeconds),
            Suppression: document.SuppressionMinutes is { } minutes
                ? TimeSpan.FromMinutes(minutes)
                : null,
            ProposalLifetime: document.ProposalLifetimeHours is { } hours
                ? TimeSpan.FromHours(hours)
                : null,
            Mode: Enum.TryParse(document.Mode, ignoreCase: true, out RuleMode mode)
                ? mode
                // A mode nobody can read is not a licence to guess upward. Observing is what a rule
                // has to earn its way out of, and it is where an unreadable one starts.
                : RuleMode.Observe,
            Retired: document.Retired,
            Shipped: false,
            CreatedBy: Authorship(document.CreatedBy),
            UpdatedBy: Authorship(document.UpdatedBy));
    }

    /// <summary>
    /// What a rule concludes when it names no final step.
    /// </summary>
    /// <remarks>
    /// Unreadable rather than "no", because a rule that fell off the end of its own steps has not
    /// decided that the condition is false — it has failed to say anything, and recording that as a
    /// negative would be silence wearing a decision's clothes.
    /// </remarks>
    private static readonly GuardRowDocument Fallback = new()
    {
        Then = "unreadable",
        Say = "this rule names no final step, so it could not conclude anything",
    };

    private static GuardRow Row(string id, GuardRowDocument document, List<string> problems)
    {
        if (!TryOutcome(document.Then, out VerdictKind outcome))
        {
            problems.Add(
                $"{id} has a step concluding '{document.Then}', which is not one of holds, doesNotHold "
                + "or unreadable — it was read as unreadable");
        }

        return new GuardRow(
            [.. document.When.Select(c => Clause(id, c, problems))],
            outcome,
            document.Say,
            document.SayWhenUnreadable);
    }

    private static Clause Clause(string id, ClauseDocument document, List<string> problems)
    {
        if (!TryOperator(document.Op, out ClauseOperator op))
        {
            problems.Add(
                $"{id} compares {document.Signal} with '{document.Op}', which is not an operator this "
                + "build knows");
        }

        Comparand? against = document.VsSignal is { Length: > 0 } alias
            ? new Comparand.OfSignal(alias)
            : document.Value is { } number
                ? Comparand.Literal.Number(number)
                : document.Text is { } text
                    ? Comparand.Literal.Text(text)
                    : null;

        return new Clause(document.Signal, op, against);
    }

    private static bool TryOutcome(string? written, out VerdictKind outcome)
    {
        outcome = VerdictKind.Unreadable;

        switch ((written ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant())
        {
            case "holds":
            case "yes":
                outcome = VerdictKind.Holds;
                return true;
            case "doesnothold":
            case "no":
                outcome = VerdictKind.DoesNotHold;
                return true;
            case "unreadable":
            case "cannottell":
                outcome = VerdictKind.Unreadable;
                return true;
            default:
                return false;
        }
    }

    private static bool TryOperator(string? written, out ClauseOperator op)
    {
        op = ClauseOperator.EqualTo;

        switch ((written ?? string.Empty).ToLowerInvariant())
        {
            case "lt": op = ClauseOperator.LessThan; return true;
            case "lte": op = ClauseOperator.AtMost; return true;
            case "gt": op = ClauseOperator.GreaterThan; return true;
            case "gte": op = ClauseOperator.AtLeast; return true;
            case "eq": op = ClauseOperator.EqualTo; return true;
            case "neq": op = ClauseOperator.NotEqualTo; return true;
            case "istrue": op = ClauseOperator.IsTrue; return true;
            case "isfalse": op = ClauseOperator.IsFalse; return true;
            case "present": op = ClauseOperator.Present; return true;
            case "absent": op = ClauseOperator.Absent; return true;
            case "contains": op = ClauseOperator.Contains; return true;
            default: return false;
        }
    }

    /// <summary>The spelling an operator has in the file, which is also what a surface renders.</summary>
    /// <remarks>
    /// One spelling, in one place. A status surface inventing its own would let a panel offer an
    /// operator the file cannot express, or spell one it can in a way the leaf will not read back.
    /// </remarks>
    public static string Wire(ClauseOperator op) => op switch
    {
        ClauseOperator.LessThan => "lt",
        ClauseOperator.AtMost => "lte",
        ClauseOperator.GreaterThan => "gt",
        ClauseOperator.AtLeast => "gte",
        ClauseOperator.EqualTo => "eq",
        ClauseOperator.NotEqualTo => "neq",
        ClauseOperator.IsTrue => "isTrue",
        ClauseOperator.IsFalse => "isFalse",
        ClauseOperator.Present => "present",
        ClauseOperator.Absent => "absent",
        _ => "contains",
    };

    /// <inheritdoc cref="Wire(ClauseOperator)"/>
    public static string Wire(VerdictKind kind) => kind switch
    {
        VerdictKind.Holds => "holds",
        VerdictKind.DoesNotHold => "doesNotHold",
        _ => "unreadable",
    };

    private static RuleDocument ToDocument(RuleDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        Wakes = [.. definition.Wakes],
        Subjects = new SubjectDocument
        {
            Source = definition.SubjectSource,
            Args = definition.SubjectArguments.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
        },
        Signals =
        [
            .. definition.Signals.Select(b => new SignalBindingDocument
            {
                Alias = b.Alias,
                Signal = b.SignalId,
                Args = b.Arguments.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
            }),
        ],
        Rows = [.. definition.Rows.Select(ToDocument)],
        Default = ToDocument(definition.Default),
        Action = definition.ActionId,
        Severity = definition.Severity.ToString().ToLowerInvariant(),
        SettleSeconds = (int)definition.Settle.TotalSeconds,
        SuppressionMinutes = definition.Suppression is { } window ? (int)window.TotalMinutes : null,
        ProposalLifetimeHours =
            definition.ProposalLifetime is { } lifetime ? (int)lifetime.TotalHours : null,
        Mode = definition.Mode.ToString().ToLowerInvariant(),
        Retired = definition.Retired,
        CreatedBy = ToDocument(definition.CreatedBy),
        UpdatedBy = ToDocument(definition.UpdatedBy),
    };

    private static GuardRowDocument ToDocument(GuardRow row) => new()
    {
        When =
        [
            .. row.Clauses.Select(c => new ClauseDocument
            {
                Signal = c.Alias,
                Op = Wire(c.Operator),
                Value = c.Against is Comparand.Literal { Value.Kind: SignalKind.Number } number
                    ? number.Value.Number
                    : null,
                Text = c.Against is Comparand.Literal { Value.Kind: SignalKind.Text } text
                    ? text.Value.Text
                    : null,
                VsSignal = c.Against is Comparand.OfSignal other ? other.Alias : null,
            }),
        ],
        Then = Wire(row.Outcome),
        Say = row.Message,
        SayWhenUnreadable = row.UnreadableMessage,
    };

    private static AuthorshipDocument? ToDocument(RuleAuthorship? authorship) =>
        authorship is null ? null : new AuthorshipDocument { Actor = authorship.Actor, At = authorship.At };

    /// <summary>
    /// Authorship as written, or none.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>No fallback to the OS user.</b> A definition hand-written into the file over SSH carries no
    /// identity and must not be given one — the same enforcement made everywhere else in this ecosystem
    /// that an actor is stamped. An unattributed rule says so.
    /// </remarks>
    private static RuleAuthorship? Authorship(AuthorshipDocument? document) =>
        document is null || string.IsNullOrWhiteSpace(document.Actor)
            ? null
            : new RuleAuthorship(document.Actor.Trim(), document.At);

    private static IReadOnlyDictionary<string, string> Insensitive(Dictionary<string, string> values) =>
        new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
}
