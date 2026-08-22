using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// What one instance has been measured to hold, over its whole observed life.
/// </summary>
/// <remarks>
/// <b>An observation, and every field is one.</b> Nothing here says what an instance <em>needs</em> —
/// it says what it used given the allowance it had, which are the same only when the allowance never
/// bound. The rule reading this is what draws the distinction; the record does not pretend to.
/// </remarks>
/// <param name="Instance">The instance this is about — the join key every per-server figure uses.</param>
/// <param name="WorkingSetPeakBytes">The largest working set observed, or null when none was measured.</param>
/// <param name="WorkingSetAvgBytes">The mean working set across every observation, or null.</param>
/// <param name="PeakBytes">The highest the kernel's own high-water mark reached in any run. Above the
/// working-set peak whenever a spike fell between two samples, and it includes page cache.</param>
/// <param name="OomKills">Processes the kernel killed in this instance's cgroup for want of memory.
/// The one figure here that bounds what the instance needs rather than describing what it used.</param>
/// <param name="MaxEvents">Times allocation hit the instance's ceiling without anything dying.</param>
/// <param name="StallSeconds">Cumulative time every task in the cgroup spent stalled waiting on memory.</param>
/// <param name="Samples">Observations behind the figures above.</param>
/// <param name="ObservedHours">Cumulative time the instance was observed running.</param>
/// <param name="SpanDays">
/// Calendar days between the first and last observation. Deliberately separate from
/// <paramref name="ObservedHours"/>: a server played two hours an evening for a month has 60 hours of
/// measurement spread over 30 days, and those are different pieces of evidence. Reading either alone
/// as "coverage" throws away the one that mattered.
/// </param>
/// <param name="Runs">Run boundaries observed. Zero for an instance running since before this host
/// started measuring, which is not the same as an instance that has never run.</param>
internal readonly record struct InstanceFootprint(
    string Instance,
    double? WorkingSetPeakBytes,
    double? WorkingSetAvgBytes,
    double? PeakBytes,
    long OomKills,
    long MaxEvents,
    double StallSeconds,
    long Runs,
    double ObservedHours,
    double SpanDays,
    long Samples)
{
    /// <summary>The largest working set observed, in MiB, or null when none was measured.</summary>
    public int? WorkingSetPeakMb =>
        WorkingSetPeakBytes is { } b and > 0 ? (int)Math.Round(b / 1024 / 1024) : null;
}

/// <summary>
/// Which way an instance's working set has been moving across a window.
/// </summary>
/// <remarks>
/// <b>The question a footprint cannot answer.</b> A peak of 2908 MB and a mean of 2163 describe a
/// server that has been flat for a month and one that climbed there from 1800 identically, and those
/// are opposite decisions: the first has found its ceiling, the second has not. Proposing a lower
/// figure against a number still in motion is the way this rule would do harm.
/// </remarks>
/// <param name="Points">Samples behind the comparison. A trend over three points is not a trend.</param>
/// <param name="GrowthPct">
/// How much the later half of the window sits above the earlier half, as a percentage. Negative when
/// the working set has been shrinking.
/// </param>
internal readonly record struct MemoryTrend(int Points, double GrowthPct);

/// <summary>
/// What kgsm-monitor has measured, as a rule needs to see it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The monitor's endpoints, never its database.</b> The monitor owns that store; reading its schema
/// from here would re-create the coupling this ecosystem removed when it made kgsm-api proxy the
/// history endpoint rather than open the file.
/// </para>
/// <para>
/// <b>The monitor being absent is not an error.</b> It is a leaf and may not be installed, which
/// produces <see cref="ReadingState.Unavailable"/> and reaches the rule as "cannot tell" — never as
/// "the condition does not hold", which would be a decision taken on no information. Nothing else
/// about this leaf changes when it is gone: one rule stops being able to speak.
/// </para>
/// </remarks>
internal interface IFootprintSource
{
    /// <summary>Every instance this host holds a footprint for.</summary>
    ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token);

    /// <summary>How one instance's working set has moved over the last thirty days.</summary>
    ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token);
}
