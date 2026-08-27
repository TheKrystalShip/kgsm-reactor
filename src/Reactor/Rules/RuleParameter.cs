namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// One threshold a rule compares a measurement against, declared beside the predicate that reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared rather than merely read, so the panel can render a knob it was never told about.</b> A
/// threshold that existed only as a lookup inside a predicate would be configurable in the sense that
/// a file could carry the key, and undiscoverable in every sense that matters: nothing could list it,
/// nothing could state its default, and a misspelling would be indistinguishable from a default.
/// </para>
/// <para>
/// <b><see cref="Default"/> is the figure the rule ships with and the place its rationale lives.</b>
/// The XML docs on the rule say what measurement chose it; this carries the number so a surface can
/// show what an override is departing from.
/// </para>
/// </remarks>
/// <param name="Key">
/// The stable wire id, <c>snake_case</c>. <b>Immutable once shipped</b> — a stored override is keyed
/// by it, so renaming one silently reverts that rule to its default.
/// </param>
/// <param name="Label">Short human name, for the panel. No units here; use <paramref name="Unit"/>.</param>
/// <param name="Default">What the rule uses when nothing overrides it.</param>
/// <param name="Minimum">
/// The floor an override is clamped to. Zero on a gate means zero is a supported setting rather than
/// a degenerate one — an operator who wants a verdict from whatever has been measured so far can turn
/// a gate off, and the rule still reports the figures its decision rests on either way.
/// </param>
/// <param name="Unit">Display suffix: <c>days</c>, <c>h</c>, <c>%</c>. Null when the number is a count.</param>
/// <param name="Description">What moving it changes, in an operator's terms.</param>
internal sealed record RuleParameter(
    string Key,
    string Label,
    double Default,
    double Minimum = 0,
    string? Unit = null,
    string? Description = null);

/// <summary>
/// Every rule's thresholds, resolved from what it declares and what an operator wrote.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration tunes a rule's thresholds; it cannot invent a rule.</b> Not as an access-control
/// posture — anyone who can write this file can already grant a rule the authority to act, and
/// guarding a number more tightly than the authority beside it would be incoherent. It is that a
/// predicate expressed as data needs a language to express it in, and a predicate that parses but
/// means something other than it reads is a worse failure than any this saves. So the predicate, the
/// wake set and the action stay compiled — <see cref="ReactorAction"/> is a closed union, which is
/// what makes the never-list a compiler error rather than a promise.
/// </para>
/// <para>
/// <b>Every declared parameter is present, always.</b> Resolution starts from the declared defaults
/// and overwrites, so a predicate's lookup cannot miss — and a lookup for a key no rule declares
/// throws rather than defaulting, which turns a typo in the code into a failing test instead of a
/// threshold that is silently zero.
/// </para>
/// </remarks>
internal sealed class RuleTuning
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> _byRule;

    private RuleTuning(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> byRule,
        IReadOnlyList<string> problems)
    {
        _byRule = byRule;
        Problems = problems;
    }

    /// <summary>
    /// What was written that could not be honoured, in an operator's words.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Reported rather than thrown, and never silent.</b> A rule id this build does not ship, a
    /// parameter key that was misspelled, a figure below its floor — each leaves the daemon running on
    /// something sane, and each would otherwise present as "I set it and nothing happened", which is
    /// the failure this whole surface exists to prevent. They reach <c>/status</c> and the log.
    /// </remarks>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The thresholds one rule runs on, every declared key present.</summary>
    public IReadOnlyDictionary<string, double> For(string ruleId) =>
        _byRule.TryGetValue(ruleId, out IReadOnlyDictionary<string, double>? values)
            ? values
            : ReadOnlyEmpty;

    private static readonly IReadOnlyDictionary<string, double> ReadOnlyEmpty =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>The catalog's own figures, with nothing overriding them.</summary>
    public static RuleTuning Defaults(IReadOnlyList<Rule> rules) => Resolve(rules, null);

    /// <summary>
    /// Lay what an operator wrote over what each rule declares.
    /// </summary>
    /// <param name="rules">The catalog. Only a rule in it can be tuned.</param>
    /// <param name="overrides">
    /// Rule id to parameter key to value, or null when nothing was written. Rule ids match
    /// case-insensitively, as they do everywhere else a rule is named in configuration.
    /// </param>
    /// <param name="loadProblem">
    /// What stopped the file being read at all, when something did. Carried in
    /// <see cref="Problems"/> beside the per-key ones so a surface has one list to render rather than
    /// two kinds of failure to know about.
    /// </param>
    public static RuleTuning Resolve(
        IReadOnlyList<Rule> rules,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? overrides,
        string? loadProblem = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Dictionary<string, IReadOnlyDictionary<string, double>> resolved = new(StringComparer.OrdinalIgnoreCase);
        List<string> problems = loadProblem is null ? [] : [loadProblem];

        // Re-keyed here rather than trusting the caller's comparer: rule ids match case-insensitively
        // everywhere else configuration names one, and a guarantee that held only when whoever built
        // the dictionary remembered to ask for it is not a guarantee.
        Dictionary<string, IReadOnlyDictionary<string, double>>? written = overrides is null
            ? null
            : new Dictionary<string, IReadOnlyDictionary<string, double>>(
                overrides, StringComparer.OrdinalIgnoreCase);

        foreach (Rule rule in rules)
        {
            Dictionary<string, double> values = rule.Parameters
                .ToDictionary(p => p.Key, p => p.Default, StringComparer.Ordinal);

            if (written is not null
                && written.TryGetValue(rule.Id, out IReadOnlyDictionary<string, double>? forRule))
            {
                foreach ((string key, double value) in forRule)
                {
                    RuleParameter? declared = rule.Parameters
                        .FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

                    if (declared is null)
                    {
                        problems.Add(
                            $"{rule.Id} declares no parameter '{key}' — it was ignored. Its parameters are: "
                            + Names(rule));
                        continue;
                    }

                    if (value < declared.Minimum)
                    {
                        problems.Add(
                            $"{rule.Id}.{key} was set to {value:0.###}, below its floor of "
                            + $"{declared.Minimum:0.###} — the floor is in force");
                        values[key] = declared.Minimum;
                        continue;
                    }

                    values[key] = value;
                }
            }

            resolved[rule.Id] = values;
        }

        if (written is not null)
        {
            foreach (string ruleId in written.Keys.Where(id => !resolved.ContainsKey(id)))
            {
                problems.Add(
                    $"'{ruleId}' is not a rule this build ships — its thresholds were ignored. "
                    + "The rules are: " + string.Join(", ", rules.Select(r => r.Id)));
            }
        }

        return new RuleTuning(resolved, problems);
    }

    private static string Names(Rule rule) =>
        rule.Parameters.Count == 0
            ? "none — it has no thresholds to tune"
            : string.Join(", ", rule.Parameters.Select(p => p.Key));
}
