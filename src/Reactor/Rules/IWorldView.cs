using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>How an instance stands right now, as far as the supervisor is concerned.</summary>
/// <param name="Phase">
/// The supervisor's own phase — <c>running</c>, <c>stopped</c>, <c>failed</c>, <c>unknown</c>.
/// </param>
/// <param name="DesiredRunning">Whether the supervisor still wants it running.</param>
/// <param name="Restarts">Consecutive failures at the moment of reading.</param>
internal readonly record struct InstanceRunState(string Phase, bool DesiredRunning, int Restarts)
{
    /// <summary>The supervisor exhausted its retries and stopped trying.</summary>
    /// <remarks>
    /// <b>A latch, not a moment.</b> The watchdog persists it deliberately — its own words, so that
    /// "an OOM doesn't fake recovery" — and nothing on a timer ever leaves it. The only exit is an
    /// operator start, which is documented there as an override that clears the give-up latch and the
    /// failure streak together.
    /// </remarks>
    public bool GaveUp => string.Equals(Phase, "failed", StringComparison.OrdinalIgnoreCase);

    public bool Running => string.Equals(Phase, "running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The live world, read fresh at every evaluation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule re-derives from this rather than trusting the event that woke it.</b> An event says
/// what happened; only a read says what is true now, and the gap between the two is where a settle
/// window lives — a crash the supervisor has already recovered from must not produce a decision about
/// a server that is running again.
/// </para>
/// <para>
/// Returns a <see cref="Reading{T}"/> rather than a value or a null: not being able to read the world
/// is a third answer, and it must reach the rule as one instead of collapsing into "the condition
/// does not hold", which would be silence dressed as a decision.
/// </para>
/// </remarks>
internal interface IWorldView
{
    /// <summary>How the supervisor sees one instance.</summary>
    ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token);
}
