using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// The live world, read from the supervisor that owns it.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog is the authority on whether a native instance is running, and asking it is the only
/// honest way to answer — inferring run state from whether a metrics row exists is the mistake this
/// ecosystem has a written rule against.
/// </para>
/// <para>
/// <b>The watchdog being absent is not an error here.</b> It is engine/base rather than a leaf, so it
/// is normally present, but a host mid-redeploy has a window where it is not. That window produces
/// <see cref="ReadingState.Unavailable"/>, which reaches the rule as "cannot tell" and stops the
/// evaluation — never as "the condition does not hold", which would be a decision taken on no
/// information.
/// </para>
/// </remarks>
internal sealed class WatchdogWorldView(IWatchdogClient watchdog, ILogger<WatchdogWorldView> logger)
    : IWorldView
{
    public async ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token)
    {
        try
        {
            WatchdogInstanceState? state = await watchdog.GetStatusAsync(instance, token).ConfigureAwait(false);

            if (state is null)
            {
                // The supervisor answered and does not know this instance. That is a measurement, not
                // a failure to measure: a container instance, or one uninstalled since the event.
                return Reading<InstanceRunState>.Unavailable(
                    $"the supervisor does not supervise {instance}");
            }

            return Reading<InstanceRunState>.Measured(new InstanceRunState(
                Phase: state.Phase,
                DesiredRunning: string.Equals(state.Desired, "running", StringComparison.OrdinalIgnoreCase),
                Restarts: state.Restarts));
        }
        catch (Exception ex)
        {
            // Reported rather than thrown: an unreachable supervisor must cost this one evaluation,
            // which the next sweep retries, and nothing else.
            logger.LogWarning(ex, "Could not read {Instance} from the supervisor.", instance);
            return Reading<InstanceRunState>.Unavailable($"the supervisor could not be reached: {ex.Message}");
        }
    }
}
