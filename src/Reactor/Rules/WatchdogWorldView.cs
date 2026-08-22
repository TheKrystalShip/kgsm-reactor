using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// The live world, read from whichever component owns each part of it.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog is the authority on whether a native instance is running, and asking it is the only
/// honest way to answer — inferring run state from whether a metrics row exists is the mistake this
/// ecosystem has a written rule against.
/// </para>
/// <para>
/// Run state comes from the supervisor and the declared requirement from the engine, because those are
/// the two authorities and neither can answer for the other. They are behind one interface because a
/// rule wants "the world", not a map of which daemon holds which half of it.
/// </para>
/// <para>
/// <b>The watchdog being absent is not an error here.</b> It is engine/base rather than a leaf, so it
/// is normally present, but a host mid-redeploy has a window where it is not. That window produces
/// <see cref="ReadingState.Unavailable"/>, which reaches the rule as "cannot tell" and stops the
/// evaluation — never as "the condition does not hold", which would be a decision taken on no
/// information.
/// </para>
/// </remarks>
internal sealed class WatchdogWorldView(
    IWatchdogClient watchdog,
    IInstanceService instances,
    IBlueprintService blueprints,
    ILogger<WatchdogWorldView> logger) : IWorldView
{
    /// <summary>
    /// Maximum-heap arguments, in the spellings a launch line actually uses.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively on the argument name and returned verbatim, so a reader is told
    /// which flag was found rather than a normalised figure this never computed.
    /// </remarks>
    private static readonly string[] HeapFlagPrefixes = ["-Xmx", "-XX:MaxRAMPercentage="];

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

    /// <inheritdoc/>
    public ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(
        string instance, CancellationToken token)
    {
        try
        {
            Instance? spec = instances.GetInstanceInfo(instance);
            if (spec is null)
            {
                return ValueTask.FromResult(Reading<MemoryDeclaration>.Unavailable(
                    $"the engine does not know an instance called {instance}"));
            }

            // An unreadable blueprint is an unanswerable comparison rather than a missing declaration:
            // reporting "nothing is declared" would let a rule conclude something about a figure it
            // never read.
            Blueprint? blueprint = string.IsNullOrWhiteSpace(spec.Blueprint)
                ? null
                : blueprints.GetInfo(spec.Blueprint);

            if (blueprint is null)
            {
                return ValueTask.FromResult(Reading<MemoryDeclaration>.Unavailable(
                    $"the blueprint {spec.Blueprint} of {instance} could not be read"));
            }

            return ValueTask.FromResult(Reading<MemoryDeclaration>.Measured(new MemoryDeclaration(
                MinRamMb: Positive(blueprint.Metadata?.MinRamMb),
                RecommendedRamMb: Positive(blueprint.Metadata?.RecommendedRamMb),
                HeapFlag: FindHeapFlag(spec.ExecutableArguments))));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read what {Instance} is declared to need.", instance);
            return ValueTask.FromResult(Reading<MemoryDeclaration>.Unavailable(
                $"the engine could not be read: {ex.Message}"));
        }
    }

    /// <summary>A declared figure of zero is KGSM's spelling of "not declared", not a requirement of none.</summary>
    private static int? Positive(int? value) => value is { } v and > 0 ? v : null;

    /// <summary>The heap argument on a launch line, verbatim, or null when it carries none.</summary>
    internal static string? FindHeapFlag(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string prefix in HeapFlagPrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && token.Length > prefix.Length)
                    return token;
            }
        }

        return null;
    }
}
