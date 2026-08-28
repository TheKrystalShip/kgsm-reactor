namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// The sentence a guard row records, and the placeholders it may carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written rather than generated, and that is deliberate.</b> A decision reads <em>"Ketchup holds
/// 5433MB against 8192MB declared, -34% below it"</em> because whoever composed the rule wrote that
/// sentence and the evaluator filled in the figures. A printer rendering the clauses themselves would
/// produce <c>peak(5433) &lt; declared(8192) → true</c>, which records the reasoning and destroys the
/// thing recording it is for.
/// </para>
/// <para>
/// The placeholders, all resolved against the same reads the clauses used:
/// <list type="bullet">
/// <item><description><c>{subject}</c> — what is being decided about.</description></item>
/// <item><description><c>{openedAt}</c> — when the condition began.</description></item>
/// <item><description><c>{openFor}</c> — how long it has been true, as an operator reads a
/// duration.</description></item>
/// <item><description><c>{settleSeconds}</c> — how long the rule waited before judging.</description></item>
/// <item><description><c>{reason}</c> — the reader's own words. Only in an unreadable message.</description></item>
/// <item><description><c>{alias}</c> — a signal's value, rendered for a person.</description></item>
/// <item><description><c>{alias:F1}</c> — the same, with a .NET numeric format.</description></item>
/// <item><description><c>{alias#}</c> — what this row compares that signal against, so a gate can
/// name the figure in force and a reader can tell a rule waiting for evidence from one somebody
/// tuned.</description></item>
/// <item><description><c>{alias@key}</c> — an argument the signal was bound with, for a sentence
/// that has to name the window it asked about.</description></item>
/// </list>
/// </para>
/// <para>
/// <c>{{</c> and <c>}}</c> are literal braces. An unclosed brace is left as written rather than
/// throwing — the file is hand-editable, and a stray brace should cost a strange-looking sentence
/// rather than a rule that cannot be evaluated.
/// </para>
/// </remarks>
internal static class MessageTemplate
{
    public const string SubjectToken = "subject";
    public const string SettleToken = "settleSeconds";
    public const string ReasonToken = "reason";

    /// <summary>When the condition began.</summary>
    /// <remarks>
    /// ⚠ <b>Not every evaluation has one, and the two that do not are different.</b> A rule that wakes
    /// on nothing judges a standing fact with no opening to name; a preview is asked about a rule that
    /// has never run. Both leave this unresolved, which ends the sentence as unreadable rather than
    /// dating a condition from whenever it happened to be asked about.
    /// </remarks>
    public const string OpenedAtToken = "openedAt";

    /// <summary>How long the condition has been true.</summary>
    public const string OpenForToken = "openFor";

    /// <summary>The tokens the evaluator resolves itself, which therefore name no signal.</summary>
    private static readonly string[] Intrinsic =
        [SubjectToken, SettleToken, ReasonToken, OpenedAtToken, OpenForToken];

    /// <summary>Literal text, or one placeholder.</summary>
    /// <param name="Literal">The text, when this is text. Null for a placeholder.</param>
    /// <param name="Head">The alias or token being resolved.</param>
    /// <param name="Format">A .NET format string, or null.</param>
    /// <param name="Comparand">Whether it asks for what the row compares against rather than the value.</param>
    /// <param name="Argument">The argument key asked for, or null.</param>
    internal readonly record struct Part(
        string? Literal, string? Head, string? Format, bool Comparand, string? Argument);

    public static IReadOnlyList<Part> Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        List<Part> parts = [];
        int index = 0;
        int literalFrom = 0;

        void FlushTo(int end)
        {
            if (end > literalFrom)
                parts.Add(new Part(template[literalFrom..end], null, null, false, null));
        }

        while (index < template.Length)
        {
            char current = template[index];

            if (current == '{' && index + 1 < template.Length && template[index + 1] == '{')
            {
                FlushTo(index);
                parts.Add(new Part("{", null, null, false, null));
                index += 2;
                literalFrom = index;
                continue;
            }

            if (current == '}' && index + 1 < template.Length && template[index + 1] == '}')
            {
                FlushTo(index);
                parts.Add(new Part("}", null, null, false, null));
                index += 2;
                literalFrom = index;
                continue;
            }

            if (current != '{')
            {
                index++;
                continue;
            }

            int close = template.IndexOf('}', index + 1);
            if (close < 0)
            {
                // Left as written. A stray brace costs a strange sentence, not an unevaluable rule.
                index++;
                continue;
            }

            FlushTo(index);
            parts.Add(Placeholder(template[(index + 1)..close]));
            index = close + 1;
            literalFrom = index;
        }

        FlushTo(template.Length);
        return parts;
    }

    private static Part Placeholder(string content)
    {
        string head = content;
        string? format = null;

        int colon = content.IndexOf(':');
        if (colon >= 0)
        {
            head = content[..colon];
            format = content[(colon + 1)..];
        }

        string? argument = null;
        int at = head.IndexOf('@');
        if (at >= 0)
        {
            argument = head[(at + 1)..];
            head = head[..at];
        }

        bool comparand = head.EndsWith('#');
        if (comparand)
            head = head[..^1];

        return new Part(null, head, format, comparand, argument);
    }

    /// <summary>Whether a name is one the evaluator answers itself, and therefore not bindable.</summary>
    public static bool IsIntrinsic(string name) => Intrinsic.Contains(name, StringComparer.Ordinal);

    /// <summary>Every alias a template reads, so a rule can be checked before it runs.</summary>
    public static IEnumerable<string> Aliases(string template) =>
        Parse(template)
            .Where(p => p.Literal is null && p.Head is not null
                        && !Intrinsic.Contains(p.Head, StringComparer.Ordinal))
            .Select(p => p.Head!);
}
