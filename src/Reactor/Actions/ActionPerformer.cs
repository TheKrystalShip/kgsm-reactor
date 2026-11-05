using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.KGSM.Engine.Contracts.Backups;
using TheKrystalShip.KGSM.Engine.Contracts.Invocation;
using TheKrystalShip.KGSM.Engine.Contracts.Operations;
using TheKrystalShip.KGSM.Engine.Reading;

namespace TheKrystalShip.Kgsm.Reactor.Actions;

/// <summary>What came of attempting an action.</summary>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Artifact">What it produced — a backup id — or null when it produced nothing nameable.</param>
/// <param name="Detail">What went wrong, or what else is worth reading. Always present on a failure.</param>
internal readonly record struct ActionResult(bool Ok, string? Artifact, string? Detail)
{
    public static ActionResult Succeeded(string? artifact = null, string? detail = null) =>
        new(true, artifact, detail);

    public static ActionResult Failed(string detail) => new(false, null, detail);
}

/// <summary>Performs what a rule decided on.</summary>
/// <remarks>
/// Behind an interface so the engine's dispatch can be exercised without an engine on the other end —
/// the tests that matter here are about <em>when</em> something is performed, and a suite that needed a
/// live KGSM to ask them would not be run.
/// </remarks>
internal interface IActionPerformer
{
    /// <summary>Carries out <paramref name="action"/>, attributed to <paramref name="actor"/>.</summary>
    Task<ActionResult> PerformAsync(ReactorAction action, string actor, CancellationToken token);
}

/// <summary>
/// Performs a reactor action through the engine: writes run the binary, reads come from the engine core.
/// </summary>
/// <remarks>
/// <para>
/// <b>The origin is always <c>reactor</c>; who the actor is depends on who decided.</b> That pair is
/// what the engine echoes into its own journal, which is what kgsm-api turns into an audit row. A rule
/// acting on its own passes <c>rule:&lt;id&gt;</c>; a proposal somebody confirmed passes <em>them</em>,
/// because the action exists on their say-so and an audit row naming the rule would make an authorised
/// action indistinguishable from one the host took by itself. Neither is ever the OS user the daemon
/// happens to run as.
/// </para>
/// <para>
/// <b>It performs and reports; it decides nothing.</b> Whether an action was permitted, whether the
/// condition still holds, whether a person authorised it — all settled before anything gets here. What
/// this owns is the one thing the rule cannot describe: which archive a restore actually names.
/// </para>
/// </remarks>
internal sealed class KgsmActionPerformer(EngineCli engine, EngineReader reader, ILogger<KgsmActionPerformer> logger)
    : IActionPerformer
{
    /// <summary>The origin every action this leaf performs carries.</summary>
    public const string Origin = "reactor";

    public Task<ActionResult> PerformAsync(ReactorAction action, string actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);

        // The closed union is what makes this exhaustive: a new action cannot be added to the catalog
        // without the compiler asking what performing it means.
        return action switch
        {
            ReactorAction.Nothing => Task.FromResult(ActionResult.Succeeded(detail: "nothing to do")),
            ReactorAction.CreateBackup backup => CaptureAsync(backup, actor, token),
            ReactorAction.ProposeRestore restore => RestoreAsync(restore, actor, token),
            _ => Task.FromResult(ActionResult.Failed($"{action.Name} is not something this build performs")),
        };
    }

    /// <summary>
    /// Takes the archive that preserves a broken state.
    /// </summary>
    /// <remarks>
    /// <b>Incident, and pinned.</b> The reason is what tells this archive apart from a routine one
    /// afterwards, and it is the difference between "restore the latest" being safe and being the
    /// thing that puts the broken world back. Pinned because rotation deleting the evidence three days
    /// into a debugging session is exactly the failure this rule exists to prevent — and a pinned
    /// archive does not consume a slot in the keep window, so preserving one erodes nothing.
    /// </remarks>
    private async Task<ActionResult> CaptureAsync(ReactorAction.CreateBackup action, string actor, CancellationToken token)
    {
        EngineResult<BackupCreateReport> result = await engine.CreateBackupAsync(
            action.Instance, BackupReason.Incident, BackupRetention.Pinned, new EngineCaller(actor, Origin), token)
            .ConfigureAwait(false);

        if (result.IsFailure || result.Value is null)
            return ActionResult.Failed(result.Message);

        // The engine names the archive it wrote in its report, so the id is a fact the engine stated and
        // not a string scraped from output or a guess from a listing.
        Backup written = result.Value.Backup;
        return ActionResult.Succeeded(
            written.Id,
            $"archived the state {action.Instance} was left in as {written.Id}{Size(written)}, "
            + "pinned so nothing rotates it away");
    }

    /// <summary>
    /// Rolls an instance back to the archive taken before the update that preceded its failure.
    /// </summary>
    /// <remarks>
    /// <b>Resolved by the manifest's reason, never by recency.</b> Once a rule that captures broken
    /// states is running, the newest archive at this moment is the broken post-update state that rule
    /// has just taken — restoring it would put back exactly what somebody is trying to escape. An
    /// archive written before the manifest carried a reason reads back unknown, and an unknown one is
    /// not a rollback candidate: refusing is the answer, because "probably the right one" is how a
    /// world gets overwritten with the wrong week.
    /// </remarks>
    private async Task<ActionResult> RestoreAsync(ReactorAction.ProposeRestore action, string actor, CancellationToken token)
    {
        Backup? candidate = NewestBackup(action.Instance, BackupReason.PreUpdate);
        if (candidate is null)
        {
            return ActionResult.Failed(
                $"no archive of {action.Instance} records itself as taken before an update, and an "
                + "archive that does not say why it exists is not a rollback candidate");
        }

        EngineResult<BackupRestoreReport> result = await engine.RestoreBackupAsync(
            action.Instance, candidate.Id, new EngineCaller(actor, Origin), token).ConfigureAwait(false);

        return result.IsFailure
            ? ActionResult.Failed(result.Message)
            : ActionResult.Succeeded(candidate.Id, $"restored {action.Instance} from {Name(candidate)}");
    }

    /// <summary>
    /// One archive as a person needs to see it named: which one, from when, and of what.
    /// </summary>
    /// <remarks>
    /// <b>Each part appears only when the manifest carries it.</b> An archive written before the
    /// manifest recorded a version says nothing about one, and a date invented for it would be the
    /// most convincing fabrication on the page — this is the sentence somebody checks before deciding
    /// a week of a world is worth overwriting.
    /// </remarks>
    internal static string Name(Backup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        string said = backup.Id;

        // Converted, not just formatted. A manifest may carry any offset, and printing its local
        // wall clock under the word UTC would misdate the archive by hours in the one sentence
        // somebody reads before overwriting a world.
        said += $", taken {backup.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC";

        if (!string.IsNullOrWhiteSpace(backup.Version))
            said += $" at version {backup.Version}";

        return said + Size(backup);
    }

    /// <summary>How big an archive is, or nothing at all when the manifest does not say.</summary>
    /// <remarks>
    /// A zero here is a manifest that recorded no size, not an empty archive — and " (0 B)" beside a
    /// backup id reads as one.
    /// </remarks>
    private static string Size(Backup backup) => backup.SizeBytes switch
    {
        >= 1_073_741_824 => $" ({backup.SizeBytes / 1_073_741_824.0:F1} GB)",
        >= 1_048_576 => $" ({backup.SizeBytes / 1_048_576.0:F0} MB)",
        > 0 => $" ({backup.SizeBytes / 1024.0:F0} KB)",
        _ => string.Empty,
    };

    /// <summary>
    /// The most recent archive of an instance taken for <paramref name="reason"/>, or null.
    /// </summary>
    /// <remarks>
    /// <b>A null reason is not a match.</b> The manifest recording none means nobody knows why that
    /// archive exists, and treating unknown as the reason being looked for is the fabrication this leaf
    /// refuses everywhere else — with a restore on the other end of it.
    /// </remarks>
    private Backup? NewestBackup(string instance, BackupReason reason)
    {
        try
        {
            return reader.Backups(instance)?.Backups
                .Where(b => b.Reason == reason)
                .Where(b => !string.IsNullOrWhiteSpace(b.Id))
                .OrderByDescending(b => b.CreatedAt)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read {Instance}'s backups.", instance);
            return null;
        }
    }
}
