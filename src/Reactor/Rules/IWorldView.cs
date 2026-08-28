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
/// What an instance is declared to need, and what would make measuring it meaningless.
/// </summary>
/// <remarks>
/// <b>The declaration describes a game; a measurement describes one world.</b> A blueprint's figures
/// are curated from vendor documentation and are true of the software; an instance's footprint is true
/// of its own map, mods and players. A rule comparing them is not correcting one with the other — it
/// is reporting that two true statements about different things have drifted apart.
/// </remarks>
/// <param name="MinRamMb">The blueprint's advisory minimum, or null when it declares none.</param>
/// <param name="RecommendedRamMb">The blueprint's advisory recommendation, or null.</param>
/// <param name="HeapFlag">
/// The maximum-heap argument this instance launches with, when it carries one.
/// <para>
/// <b>Its presence makes the footprint unusable as a requirement.</b> A JVM with <c>-Xmx4096M</c>
/// will hold four gigabytes whether or not the world needs them, so what was measured is the value of
/// a flag rather than a property of the server.
/// </para>
/// <para>
/// <b>Null does not mean there is none.</b> This reads the arguments KGSM launches with, and a game
/// whose own start script sets the heap — Project Zomboid's <c>ProjectZomboid64.json</c> carries
/// <c>-Xmx8g</c> — is invisible here. Absence is "none found", never "none exists".
/// </para>
/// </param>
internal readonly record struct MemoryDeclaration(int? MinRamMb, int? RecommendedRamMb, string? HeapFlag);

/// <summary>
/// What the supervisor is configured to do about an instance failing.
/// </summary>
/// <remarks>
/// <b>The denominator behind a failure count.</b> "Failed twice" and "failed twice out of two" are
/// different statements, and only the second says the supervisor has run out of attempts rather than
/// being partway through them.
/// </remarks>
/// <param name="MaxRestarts">
/// How many times in a row the supervisor will restart it before giving up, or null when the instance
/// declares no figure of its own.
/// <para>
/// <b>Null is "this instance names none", never "there is no limit".</b> The watchdog falls back to
/// its own setting, which is not readable from here — so a rule that wants the denominator asks
/// whether there is one and says what it found, and nothing anywhere substitutes a plausible number
/// for the one that actually applies.
/// </para>
/// </param>
internal readonly record struct InstanceSupervision(int? MaxRestarts);

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

    /// <summary>What this instance is declared to need, and whether it can be measured at all.</summary>
    ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(string instance, CancellationToken token);

    /// <summary>How many failures the supervisor will absorb before it gives up on this instance.</summary>
    /// <remarks>
    /// Its own read rather than a field on <see cref="InstanceRunState"/>: run state is asked for by
    /// every rule that mentions the supervisor at all, and this comes from the engine's instance
    /// record instead of the supervisor's socket. Folding them would put an engine read behind every
    /// <c>world.*</c> signal on the host.
    /// </remarks>
    ValueTask<Reading<InstanceSupervision>> SupervisionAsync(string instance, CancellationToken token);
}
