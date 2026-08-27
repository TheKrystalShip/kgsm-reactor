using System.Globalization;

using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>What sort of thing a signal reads, which decides what may be asked of it.</summary>
internal enum SignalKind
{
    /// <summary>A quantity, compared with the ordering operators.</summary>
    Number,

    /// <summary>A string, compared for equality or containment.</summary>
    Text,

    /// <summary>True or false.</summary>
    Flag,

    /// <summary>A length of time, rendered the way an operator reads one.</summary>
    Duration,

    /// <summary>A moment. Usually asked only whether it is there at all.</summary>
    Instant,
}

/// <summary>One value a signal read produced.</summary>
/// <remarks>
/// ⚠ <b><see cref="Present"/> is a value, not a failure.</b> "This instance's blueprint declares no
/// minimum" and "the blueprint could not be read" are different answers, and only the second is
/// <see cref="SignalReading.Readable"/> being false. Collapsing them would make a rule unable to say
/// <em>"there is none"</em> without also saying it could not tell — which is how a coverage gate
/// starts refusing instances it was meant to judge.
/// </remarks>
internal readonly record struct SignalValue
{
    public required SignalKind Kind { get; init; }

    /// <summary>Whether there is a value at all. False is a measurement, not an error.</summary>
    public required bool Present { get; init; }

    public double Number { get; init; }

    public string? Text { get; init; }

    public bool Flag { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTimeOffset Instant { get; init; }

    public static SignalValue OfNumber(double value) =>
        new() { Kind = SignalKind.Number, Present = true, Number = value };

    public static SignalValue OfText(string value) =>
        new() { Kind = SignalKind.Text, Present = true, Text = value };

    public static SignalValue OfFlag(bool value) =>
        new() { Kind = SignalKind.Flag, Present = true, Flag = value };

    public static SignalValue OfDuration(TimeSpan value) =>
        new() { Kind = SignalKind.Duration, Present = true, Duration = value };

    public static SignalValue OfInstant(DateTimeOffset value) =>
        new() { Kind = SignalKind.Instant, Present = true, Instant = value };

    /// <summary>Measured, and there is none.</summary>
    public static SignalValue None(SignalKind kind) => new() { Kind = kind, Present = false };

    /// <summary>
    /// The value as a person reads it, which is what a decision's prose is assembled from.
    /// </summary>
    /// <remarks>
    /// A duration with no format given renders the way <c>threshold_stuck</c> has always rendered one
    /// — minutes below an hour and a half, hours above it — because a p95 of "142m" is a figure
    /// somebody has to divide in their head before it means anything.
    /// </remarks>
    public string Render(string? format)
    {
        if (!Present)
            return "none";

        return Kind switch
        {
            SignalKind.Number => format is null
                ? Number.ToString(CultureInfo.InvariantCulture)
                : Number.ToString(format, CultureInfo.InvariantCulture),
            SignalKind.Text => Text ?? string.Empty,
            SignalKind.Flag => Flag ? "yes" : "no",
            SignalKind.Duration => format is null
                ? RenderDuration(Duration)
                : Duration.TotalMinutes.ToString(format, CultureInfo.InvariantCulture),
            SignalKind.Instant => Instant.ToString("O", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static string RenderDuration(TimeSpan span) =>
        span.TotalMinutes < 90
            ? span.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture) + "m"
            : span.TotalHours.ToString("F1", CultureInfo.InvariantCulture) + "h";
}

/// <summary>A signal read: a value, or the reason there is not one.</summary>
/// <remarks>
/// The same three-valued discipline <see cref="Reading{T}"/> imposes on the sources underneath,
/// carried up to the clause that compares it. An unreadable signal ends the whole rule as
/// <see cref="VerdictKind.Unreadable"/> — made a property of the evaluator rather than a row somebody
/// has to remember to write, which is the only way "cannot tell" stays impossible to forget.
/// </remarks>
internal readonly record struct SignalReading(bool Readable, SignalValue Value, string? Reason)
{
    public static SignalReading Of(SignalValue value) => new(true, value, null);

    public static SignalReading Unreadable(string reason) =>
        new(false, SignalValue.None(SignalKind.Number), reason);
}

/// <summary>What sort of thing an argument to a signal is, so a panel can render a field for it.</summary>
internal enum ArgumentKind
{
    /// <summary>An event type, chosen from the trigger catalog.</summary>
    EventType,

    /// <summary>A plain number.</summary>
    Number,

    /// <summary>Free text.</summary>
    Text,
}

/// <summary>One argument a signal needs before it can read anything.</summary>
/// <param name="Key">The stable wire id it is written under.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Kind">What sort of value it takes.</param>
/// <param name="Default">What is used when nothing supplies one, or null when it is required.</param>
/// <param name="Description">What it changes, in an operator's terms.</param>
internal sealed record SignalArgument(
    string Key,
    string Label,
    ArgumentKind Kind,
    string? Default = null,
    string? Description = null)
{
    public bool Required => Default is null;
}

/// <summary>
/// The arguments one binding supplies, and the parsing every signal would otherwise repeat.
/// </summary>
/// <remarks>
/// A missing or unparseable argument produces a reading that cannot be read rather than an exception:
/// it arrived from a file somebody wrote, and a daemon that died over one would be a worse answer
/// than one that reports which rule could not be evaluated and goes on evaluating the rest.
/// </remarks>
internal sealed class SignalArguments(IReadOnlyDictionary<string, string> values)
{
    public static readonly SignalArguments Empty =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> Values { get; } = values;

    public string? Raw(string key) => Values.TryGetValue(key, out string? value) ? value : null;

    public string Text(string key, string fallback = "") => Raw(key) ?? fallback;

    public bool TryNumber(string key, out double value) =>
        double.TryParse(Raw(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public double Number(string key, double fallback) =>
        TryNumber(key, out double value) ? value : fallback;
}

/// <summary>Everything a signal is given to read with, for one subject at one instant.</summary>
internal sealed record SignalRequest(EvaluationScope Scope, SignalArguments Arguments)
{
    public string Subject => Scope.Subject;

    public DateTimeOffset Now => Scope.Now;
}

/// <summary>
/// One thing a rule can ask about the world, with a unit and a name a person recognises.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compiled, and that is what keeps composition honest.</b> Some of these are derived rather than
/// read — a drift percentage is a footprint and a blueprint compared, and expressing that as data
/// would need an expression language nobody asked for. A person composes from what this build can
/// measure and cannot reach past it.
/// </para>
/// <para>
/// <b>The label, unit and description live here rather than on the rule.</b> A threshold is the
/// comparand in a clause, and everything a panel needs in order to render a field for it — what is
/// being compared, in what unit, and what moving it changes — is a property of the thing being
/// measured, not of the rule doing the measuring.
/// </para>
/// </remarks>
/// <param name="Id">The stable wire id a rule names it by. Immutable once shipped.</param>
/// <param name="Label">Short human name.</param>
/// <param name="Kind">What sort of value it produces.</param>
/// <param name="Read">How it is read. Three-valued, and the reason travels with a failure.</param>
/// <param name="Unit">Display suffix, or null when the number is a count.</param>
/// <param name="Description">What it means, in an operator's terms.</param>
/// <param name="Arguments">What must be supplied before it can read. Empty for most.</param>
internal sealed record Signal(
    string Id,
    string Label,
    SignalKind Kind,
    Func<SignalRequest, CancellationToken, ValueTask<SignalReading>> Read,
    string? Unit = null,
    string? Description = null,
    IReadOnlyList<SignalArgument>? Arguments = null)
{
    public IReadOnlyList<SignalArgument> Arguments { get; init; } = Arguments ?? [];
}

/// <summary>
/// One evaluation's reads, made once each.
/// </summary>
/// <remarks>
/// ⚠ <b>Memoised because the alternative is a rule that costs eight round trips.</b>
/// <c>memory_declaration_drift</c> asks nine things of a footprint that arrives in one response, and a
/// composed rule reading each signal independently would go to the monitor's socket for every one of
/// them. Worse than slow: two reads of a moving measurement inside one decision would let a rule
/// compare figures from different instants and record the result as though it were one observation.
/// </remarks>
internal sealed class EvaluationScope(
    string subject,
    DateTimeOffset now,
    IWorldView world,
    IRuleHistory history,
    IFootprintSource footprint)
{
    private readonly Dictionary<string, SignalReading> _signals = new(StringComparer.Ordinal);

    private Task<Reading<InstanceRunState>>? _instance;
    private Task<Reading<MemoryDeclaration>>? _declaration;
    private Task<Reading<IReadOnlyList<InstanceFootprint>>>? _footprints;
    private Task<Reading<MemoryTrend>>? _trend;

    public string Subject { get; } = subject;

    public DateTimeOffset Now { get; } = now;

    public IRuleHistory History { get; } = history;

    public Task<Reading<InstanceRunState>> InstanceAsync(CancellationToken token) =>
        _instance ??= world.InstanceAsync(Subject, token).AsTask();

    public Task<Reading<MemoryDeclaration>> DeclarationAsync(CancellationToken token) =>
        _declaration ??= world.MemoryDeclarationAsync(Subject, token).AsTask();

    public Task<Reading<IReadOnlyList<InstanceFootprint>>> FootprintsAsync(CancellationToken token) =>
        _footprints ??= footprint.AllAsync(token).AsTask();

    public Task<Reading<MemoryTrend>> TrendAsync(CancellationToken token) =>
        _trend ??= footprint.TrendAsync(Subject, token).AsTask();

    /// <summary>This subject's footprint, or the reason there is not one to read.</summary>
    /// <remarks>
    /// The two failures are separated deliberately. A monitor that is not there and an instance it has
    /// simply never seen call for different responses — install the leaf, or wait — and one message
    /// covering both sends every reader to the wrong one first.
    /// </remarks>
    public async ValueTask<(InstanceFootprint? Value, string? Problem)> FootprintAsync(CancellationToken token)
    {
        Reading<IReadOnlyList<InstanceFootprint>> all = await FootprintsAsync(token).ConfigureAwait(false);

        if (all is not { State: ReadingState.Measured, Value: { } measured })
            return (null, $"the footprint could not be read: {all.Reason ?? "no reason given"}");

        foreach (InstanceFootprint candidate in measured)
        {
            if (string.Equals(candidate.Instance, Subject, StringComparison.Ordinal))
                return (candidate, null);
        }

        return (null, $"no footprint is recorded for {Subject}");
    }

    /// <summary>Read one binding, at most once per evaluation.</summary>
    public async ValueTask<SignalReading> ReadAsync(
        SignalBinding binding, Signal signal, CancellationToken token)
    {
        if (_signals.TryGetValue(binding.Alias, out SignalReading cached))
            return cached;

        SignalReading reading;
        try
        {
            reading = await signal.Read(
                new SignalRequest(this, new SignalArguments(binding.Arguments)), token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A signal that threw has measured nothing. Recorded as unreadable rather than swallowed,
            // so a broken reader is visible in the decision instead of being a silent absence.
            reading = SignalReading.Unreadable($"{signal.Id} failed while reading: {ex.Message}");
        }

        _signals[binding.Alias] = reading;
        return reading;
    }
}
