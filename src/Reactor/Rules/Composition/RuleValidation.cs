using System.Text.RegularExpressions;

using TheKrystalShip.Kgsm.Reactor.Events;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// Checks a composed rule against the catalogs before it is allowed to run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The leaf is the authority, and it re-checks what the panel already checked.</b> A panel
/// validates against the catalog it was served so a person is stopped before they save; this runs at
/// load, on a file that may have been written by hand over SSH, by an older panel, or by a build that
/// offered a signal this one no longer measures.
/// </para>
/// <para>
/// <b>Nothing here throws and nothing here is silent.</b> A rule that cannot be honoured is left out
/// and said out loud on <c>/status</c> — a daemon that refused to start over one bad rule would take
/// every other rule down with it, and one that quietly dropped it would leave somebody watching for a
/// decision that was never going to come.
/// </para>
/// </remarks>
internal static partial class RuleValidation
{
    /// <summary>
    /// The shape of an id.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Immutable once minted, and unique across retired rules too.</b> It is the actor string on
    /// every journal line and ledger row the rule produced. Reusing a retired id would make one name
    /// resolve to two different rules depending on when you asked, which is worse than having no name.
    /// </remarks>
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex IdShape { get; }

    /// <summary>Everything wrong with one rule, in an operator's words. Empty when it can run.</summary>
    public static IReadOnlyList<string> Problems(RuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        List<string> problems = [];
        string id = string.IsNullOrWhiteSpace(definition.Id) ? "(unnamed)" : definition.Id;

        if (!IdShape.IsMatch(definition.Id ?? string.Empty))
            problems.Add($"'{id}' is not a usable rule id — lower case, digits and underscores only");

        if (string.IsNullOrWhiteSpace(definition.Name))
            problems.Add($"{id} has no name");

        // The loop guard, enforced by construction rather than by a test: the reactor tails every
        // producer's journal including its own, so a rule waking on a decision it wrote would decide
        // about its own decision, write that, and be woken by it — at the sweep interval, forever.
        foreach (string wake in definition.Wakes)
        {
            if (wake.StartsWith(ReactorEvents.Prefix, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{id} wakes on '{wake}', which this leaf writes itself — a rule cannot be woken by "
                    + "its own decisions");
            }
        }

        SubjectSource? subjects = SubjectSourceCatalog.ById(definition.SubjectSource ?? string.Empty);
        if (subjects is null)
        {
            problems.Add(
                $"{id} takes its subjects from '{definition.SubjectSource}', which this build does not "
                + "offer. The choices are: " + string.Join(", ", SubjectSourceCatalog.All.Select(s => s.Id)));
        }
        else
        {
            foreach (SignalArgument argument in subjects.Arguments.Where(a => a.Required))
            {
                if (!definition.SubjectArguments.ContainsKey(argument.Key))
                    problems.Add($"{id} does not say what '{argument.Key}' is for its subjects");
            }

            // An edge rule has no other way to be reached, so an empty wake list makes it permanently
            // silent while looking configured.
            if (subjects.FromEvent && definition.Wakes.Count == 0)
                problems.Add($"{id} takes its subject from an event but names no event to wake on");
        }

        if (definition.Settle <= TimeSpan.Zero)
        {
            problems.Add(
                $"{id} is judged the instant its event lands — a condition that ever resolves itself "
                + "will be reported before it has the chance");
        }

        if (ActionCatalog.ById(definition.ActionId ?? string.Empty) is null)
        {
            problems.Add(
                $"{id} would do '{definition.ActionId}', which this build cannot do. The choices are: "
                + string.Join(", ", ActionCatalog.All.Select(a => a.Id)));
        }

        foreach (SignalBinding binding in definition.Signals)
        {
            // ⚠ Refused rather than resolved by precedence. The evaluator answers its own tokens
            // before it looks at a binding, so a rule naming one would read as saved-and-working
            // while every sentence mentioning it silently said something else.
            if (MessageTemplate.IsIntrinsic(binding.Alias))
            {
                problems.Add(
                    $"{id} binds a measurement as '{binding.Alias}', which is a name every rule's "
                    + "sentences already use for something else. Bind it under another name.");
            }

            Signal? signal = SignalCatalog.ById(binding.SignalId);
            if (signal is null)
            {
                problems.Add(
                    $"{id} reads '{binding.SignalId}' as {binding.Alias}, which this build does not measure");
                continue;
            }

            foreach (SignalArgument argument in signal.Arguments.Where(a => a.Required))
            {
                if (!binding.Arguments.ContainsKey(argument.Key))
                    problems.Add($"{id} does not say what '{argument.Key}' is for {binding.Alias}");
            }
        }

        int aliases = definition.Signals.Select(b => b.Alias).Distinct(StringComparer.Ordinal).Count();
        if (aliases != definition.Signals.Count)
            problems.Add($"{id} binds the same name twice, so which measurement it means is undefined");

        foreach (GuardRow row in definition.Rows.Append(definition.Default))
            CheckRow(definition, row, id, problems);

        return problems;
    }

    private static void CheckRow(
        RuleDefinition definition, GuardRow row, string id, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(row.Message))
        {
            problems.Add(
                $"{id} has a step that concludes {row.Outcome} without saying why — a decision whose "
                + "reason is blank is a record nobody can act on");
        }

        foreach (Clause clause in row.Clauses)
        {
            Known(definition, clause.Alias, id, "compares", problems);

            if (clause.Against is Comparand.OfSignal other)
                Known(definition, other.Alias, id, "compares against", problems);

            bool needsComparand = clause.Operator
                is not (ClauseOperator.IsTrue or ClauseOperator.IsFalse
                    or ClauseOperator.Present or ClauseOperator.Absent);

            if (needsComparand && clause.Against is null)
                problems.Add($"{id} asks whether {clause.Alias} is {clause.Operator} without saying what");
        }

        foreach (string? template in new string?[] { row.Message, row.UnreadableMessage })
        {
            if (template is null)
                continue;

            foreach (string alias in MessageTemplate.Aliases(template))
                Known(definition, alias, id, "says", problems);
        }
    }

    /// <summary>An alias resolves when the rule binds it, or when it is a signal that needs no binding.</summary>
    private static void Known(
        RuleDefinition definition, string alias, string id, string verb, List<string> problems)
    {
        if (definition.Binding(alias) is not null || SignalCatalog.ById(alias) is not null)
            return;

        problems.Add(
            $"{id} {verb} '{alias}', which it does not bind and this build does not measure");
    }
}
