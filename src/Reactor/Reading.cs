namespace TheKrystalShip.Kgsm.Reactor;

/// <summary>Whether a value was measured, and if not, why not. Never a stand-in for the value.</summary>
internal enum ReadingState
{
    /// <summary>The value was read from its authority.</summary>
    Measured,

    /// <summary>The source does not provide this value for this subject.</summary>
    Unsupported,

    /// <summary>The source could not be reached or did not answer.</summary>
    Unavailable,

    /// <summary>Nobody asked: the value was not read on this pass.</summary>
    Skipped,
}

/// <summary>
/// One measurement, or the reason there is none. A reading that was not measured carries no value, so a
/// caller cannot mistake "unknown" for zero.
/// </summary>
internal sealed record Reading<T>
{
    public ReadingState State { get; init; }

    public T? Value { get; init; }

    /// <summary>Why the value is absent; null for a measured reading.</summary>
    public string? Reason { get; init; }

    public bool IsMeasured => State == ReadingState.Measured;

    public static Reading<T> Measured(T value) => new() { State = ReadingState.Measured, Value = value };

    public static Reading<T> Unavailable(string? reason = null) =>
        new() { State = ReadingState.Unavailable, Reason = reason };

    public static Reading<T> Unsupported(string? reason = null) =>
        new() { State = ReadingState.Unsupported, Reason = reason };

    public static Reading<T> Skipped(string? reason = null) =>
        new() { State = ReadingState.Skipped, Reason = reason };
}
