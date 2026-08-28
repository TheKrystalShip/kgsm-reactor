using System.Text;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// Runs a composed rule: reads what it asks for, decides in row order, and writes the sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two things are the evaluator's rather than the rule author's, and both were repeated in all
/// four compiled rules before they were.</b> An unreadable signal ends the rule as
/// <see cref="VerdictKind.Unreadable"/> carrying the reader's own reason — nobody has to remember to
/// write that row. And rows are read in order with the first match deciding, so a rule reads the way
/// somebody explains a decision out loud.
/// </para>
/// <para>
/// <b>Every read happens at most once per evaluation.</b> Not only for cost: a rule that read a moving
/// measurement twice inside one decision could compare figures from different instants and record the
/// result as a single observation.
/// </para>
/// </remarks>
internal static class RuleEvaluator
{
    /// <summary>What a sentence is told when it asks to date a condition nobody saw begin.</summary>
    private const string WhenUnknown =
        "when this condition began is not on record, so it cannot be dated";

    /// <summary>Decide one rule about one subject.</summary>
    public static async ValueTask<Verdict> EvaluateAsync(
        RuleDefinition definition, EvaluationScope scope, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(scope);

        foreach (GuardRow row in definition.Rows)
        {
            (bool? holds, string? unreadable) = await MatchAsync(definition, row, scope, token)
                .ConfigureAwait(false);

            if (unreadable is not null)
                return await UnreadableAsync(definition, row, scope, unreadable, token).ConfigureAwait(false);

            if (holds is true)
                return await ConcludeAsync(definition, row, scope, token).ConfigureAwait(false);
        }

        return await ConcludeAsync(definition, definition.Default, scope, token).ConfigureAwait(false);
    }

    /// <summary>What a state rule should evaluate on this sweep.</summary>
    public static async ValueTask<IReadOnlyList<string>> SubjectsAsync(
        RuleDefinition definition, SubjectContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(definition);

        SubjectSource? source = SubjectSourceCatalog.ById(definition.SubjectSource);

        return source?.Enumerate is null
            ? []
            : await source.Enumerate(context, new SignalArguments(definition.SubjectArguments), token)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether every clause in a row holds, or the reason one of them could not be read.
    /// </summary>
    /// <remarks>
    /// Clauses are read in order and stop at the first that does not hold, which is what keeps a rule
    /// from reading a source it did not need. <c>memory_declaration_drift</c> depends on it: an
    /// instance holding more than it was declared to need is reported without the working-set trend
    /// ever being asked for, so a monitor that cannot serve one does not block the verdict.
    /// </remarks>
    private static async ValueTask<(bool? Holds, string? Unreadable)> MatchAsync(
        RuleDefinition definition, GuardRow row, EvaluationScope scope, CancellationToken token)
    {
        foreach (Clause clause in row.Clauses)
        {
            (SignalReading reading, string? missing) =
                await ReadAsync(definition, clause.Alias, scope, token).ConfigureAwait(false);

            if (missing is not null)
                return (null, missing);

            if (!reading.Readable)
                return (null, reading.Reason ?? $"{clause.Alias} could not be read");

            SignalValue? against = null;
            if (clause.Against is Comparand.Literal literal)
            {
                against = literal.Value;
            }
            else if (clause.Against is Comparand.OfSignal other)
            {
                (SignalReading comparand, string? absent) =
                    await ReadAsync(definition, other.Alias, scope, token).ConfigureAwait(false);

                if (absent is not null)
                    return (null, absent);
                if (!comparand.Readable)
                    return (null, comparand.Reason ?? $"{other.Alias} could not be read");

                against = comparand.Value;
            }

            if (!Compare(reading.Value, clause.Operator, against))
                return (false, null);
        }

        return (true, null);
    }

    /// <summary>Whether one comparison holds.</summary>
    /// <remarks>
    /// ⚠ <b>An absent value fails every comparison except the two that ask about absence.</b> There is
    /// nothing to compare, and answering "not equal" for a figure that does not exist would let a rule
    /// draw a conclusion from a measurement nobody made.
    /// </remarks>
    private static bool Compare(SignalValue value, ClauseOperator op, SignalValue? against)
    {
        if (op == ClauseOperator.Present)
            return value.Present;
        if (op == ClauseOperator.Absent)
            return !value.Present;

        if (!value.Present)
            return false;

        switch (op)
        {
            case ClauseOperator.IsTrue:
                return value.Flag;
            case ClauseOperator.IsFalse:
                return !value.Flag;
        }

        if (against is not { Present: true } comparand)
            return false;

        if (value.Kind == SignalKind.Text || comparand.Kind == SignalKind.Text)
        {
            string left = value.Text ?? string.Empty;
            string right = comparand.Text ?? string.Empty;

            return op switch
            {
                ClauseOperator.EqualTo => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
                ClauseOperator.NotEqualTo => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
                ClauseOperator.Contains => left.Contains(right, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        double a = Quantity(value);
        double b = Quantity(comparand);

        return op switch
        {
            ClauseOperator.LessThan => a < b,
            ClauseOperator.AtMost => a <= b,
            ClauseOperator.GreaterThan => a > b,
            ClauseOperator.AtLeast => a >= b,
            ClauseOperator.EqualTo => a.Equals(b),
            ClauseOperator.NotEqualTo => !a.Equals(b),
            _ => false,
        };
    }

    private static double Quantity(SignalValue value) => value.Kind switch
    {
        SignalKind.Duration => value.Duration.TotalSeconds,
        SignalKind.Instant => value.Instant.ToUnixTimeMilliseconds(),
        SignalKind.Flag => value.Flag ? 1 : 0,
        _ => value.Number,
    };

    /// <summary>The binding an alias names, read once.</summary>
    /// <remarks>
    /// An alias equal to a signal's own id needs no binding written, which is the common case: only a
    /// signal that takes arguments has anything to bind.
    /// </remarks>
    private static async ValueTask<(SignalReading Reading, string? Missing)> ReadAsync(
        RuleDefinition definition, string alias, EvaluationScope scope, CancellationToken token)
    {
        SignalBinding? binding = definition.Binding(alias)
            ?? (SignalCatalog.ById(alias) is null ? null : SignalBinding.Bare(alias));

        if (binding is null)
            return (default, $"this rule reads '{alias}', which this build does not measure");

        Signal? signal = SignalCatalog.ById(binding.SignalId);
        if (signal is null)
            return (default, $"'{binding.SignalId}' is not a signal this build measures");

        return (await scope.ReadAsync(binding, signal, token).ConfigureAwait(false), null);
    }

    private static async ValueTask<Verdict> ConcludeAsync(
        RuleDefinition definition, GuardRow row, EvaluationScope scope, CancellationToken token)
    {
        (string text, string? unreadable) =
            await RenderAsync(definition, row, scope, row.Message, null, token).ConfigureAwait(false);

        // A sentence that cannot state its own figures is not a decision worth recording. The verdict
        // becomes "cannot tell" rather than a conclusion with a hole where the measurement was.
        if (unreadable is not null)
            return await UnreadableAsync(definition, row, scope, unreadable, token).ConfigureAwait(false);

        // ⚠ A row that concludes `unreadable` is the rule declining on evidence it successfully read,
        // which is a different fact from a source refusing to answer — every coverage gate in every
        // rule is written this way. Marked here because this is the only place that can tell them
        // apart: one step further out they are both just "cannot tell".
        return row.Outcome == VerdictKind.Unreadable
            ? Verdict.Withhold(text)
            : new Verdict(row.Outcome, text);
    }

    private static async ValueTask<Verdict> UnreadableAsync(
        RuleDefinition definition, GuardRow row, EvaluationScope scope, string reason, CancellationToken token)
    {
        if (row.UnreadableMessage is null)
            return Verdict.Unreadable(reason);

        (string text, string? failed) = await RenderAsync(
            definition, row, scope, row.UnreadableMessage, reason, token).ConfigureAwait(false);

        // If the row's own sentence needs something that also cannot be read, the reader's words are
        // what is left — less informative and still true, which is the right way round to fail.
        return Verdict.Unreadable(failed is null ? text : reason);
    }

    /// <summary>
    /// Fill a row's sentence from the same reads its clauses used.
    /// </summary>
    /// <remarks>
    /// See <see cref="MessageTemplate"/> for the placeholders. Everything resolves against this
    /// evaluation's cache, so the figures in the prose are the figures the decision was taken on
    /// rather than a second look at a source that has moved.
    /// </remarks>
    private static async ValueTask<(string Text, string? Unreadable)> RenderAsync(
        RuleDefinition definition,
        GuardRow row,
        EvaluationScope scope,
        string template,
        string? reason,
        CancellationToken token)
    {
        var output = new StringBuilder(template.Length + 32);

        foreach (MessageTemplate.Part part in MessageTemplate.Parse(template))
        {
            if (part.Literal is { } text)
            {
                output.Append(text);
                continue;
            }

            switch (part.Head)
            {
                case MessageTemplate.SubjectToken:
                    output.Append(scope.Subject);
                    continue;
                case MessageTemplate.SettleToken:
                    output.Append((int)definition.Settle.TotalSeconds);
                    continue;
                case MessageTemplate.ReasonToken:
                    output.Append(reason ?? string.Empty);
                    continue;

                // Unreadable rather than filled from the evaluation instant. A sentence saying a
                // server has been down "0m" because nobody knew when it went down is worse than one
                // that admits it cannot date the condition.
                case MessageTemplate.OpenedAtToken:
                    if (scope.OpenedAt is not { } at)
                        return (string.Empty, WhenUnknown);
                    output.Append(SignalValue.OfInstant(at).Render(part.Format));
                    continue;
                case MessageTemplate.OpenForToken:
                    if (scope.OpenedAt is not { } began)
                        return (string.Empty, WhenUnknown);
                    output.Append(SignalValue.OfDuration(scope.Now - began).Render(part.Format));
                    continue;
            }

            if (part.Argument is { } argument)
            {
                output.Append(definition.Binding(part.Head!)?.Arguments is { } arguments
                              && arguments.TryGetValue(argument, out string? supplied)
                    ? supplied
                    : SignalCatalog.ById(part.Head!)
                        ?.Arguments.FirstOrDefault(a => a.Key == argument)?.Default
                      ?? string.Empty);
                continue;
            }

            if (part.Comparand)
            {
                Clause? clause = row.Clauses.FirstOrDefault(c =>
                    string.Equals(c.Alias, part.Head, StringComparison.Ordinal));

                if (clause?.Against is Comparand.Literal literal)
                {
                    output.Append(literal.Value.Render(part.Format));
                    continue;
                }

                if (clause?.Against is Comparand.OfSignal other)
                {
                    (SignalReading reading, string? missing) =
                        await ReadAsync(definition, other.Alias, scope, token).ConfigureAwait(false);

                    if (missing is not null)
                        return (string.Empty, missing);
                    if (!reading.Readable)
                        return (string.Empty, reading.Reason);

                    output.Append(reading.Value.Render(part.Format));
                    continue;
                }

                return (string.Empty,
                    $"this rule's message compares against '{part.Head}', which nothing in the row does");
            }

            {
                (SignalReading reading, string? missing) =
                    await ReadAsync(definition, part.Head!, scope, token).ConfigureAwait(false);

                if (missing is not null)
                    return (string.Empty, missing);
                if (!reading.Readable)
                    return (string.Empty, reading.Reason);

                output.Append(reading.Value.Render(part.Format));
            }
        }

        return (output.ToString(), null);
    }

    /// <summary>
    /// Run a composed rule through the shape the engine already evaluates.
    /// </summary>
    /// <remarks>
    /// The adapter is what lets a stored rule and a compiled one be judged by the same machinery, and
    /// therefore what lets one be checked against the other verdict-for-verdict.
    /// </remarks>
    public static Rule ToRule(RuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        ActionEntry action = ActionCatalog.ById(definition.ActionId)
            ?? ActionCatalog.All.First(a => a.Id == ActionCatalog.None);

        return new Rule(
            Id: definition.Id,
            Shape: definition.Shape,
            Wakes: definition.Wakes,
            Severity: definition.Severity,
            Settle: definition.Settle,
            Holds: (ctx, token) => EvaluateAsync(
                definition,
                new EvaluationScope(
                    ctx.Subject, ctx.Now, ctx.World, ctx.History, ctx.Footprint, ctx.OpenedAt),
                token),
            Action: action.Create,
            Subjects: definition.Shape == RuleShape.State
                ? (ctx, token) => SubjectsAsync(definition, ctx, token)
                : null,
            Suppression: definition.Suppression);
    }
}
