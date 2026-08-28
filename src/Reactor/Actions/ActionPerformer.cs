using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

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
/// Performs a reactor action through kgsm-lib.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The origin is always <c>reactor</c>; who the actor is depends on who decided.</b> That pair is
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
internal sealed class KgsmActionPerformer(IInstanceService instances, ILogger<KgsmActionPerformer> logger)
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
            ReactorAction.CreateBackup backup => Task.Run(() => Capture(backup, actor), token),
            ReactorAction.ProposeRestore restore => Task.Run(() => Restore(restore, actor), token),
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
    private ActionResult Capture(ReactorAction.CreateBackup action, string actor)
    {
        KgsmResult result = instances.CreateBackup(
            action.Instance, actor, Origin, BackupReason.Incident, BackupRetention.Pinned);

        if (result.IsFailure)
            return ActionResult.Failed(Explain(result));

        // The engine prints the id it minted. Read back rather than parsed out of the output: what
        // was written is a fact the engine holds, and a string scraped from a log is a guess that
        // would name a backup nobody can find the day the output changes.
        string? id = NewestBackup(action.Instance, BackupReason.Incident)?.Id;
        if (id is null)
            logger.LogWarning("Backed up {Instance}, and no archive came back to name.", action.Instance);

        return ActionResult.Succeeded(id, $"captured the state {action.Instance} was left in");
    }

    /// <summary>
    /// Rolls an instance back to the archive taken before the update that preceded its failure.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Resolved by the manifest's reason, never by recency.</b> Once a rule that captures broken
    /// states is running, the newest archive at this moment is the broken post-update state that rule
    /// has just taken — restoring it would put back exactly what somebody is trying to escape. An
    /// archive written before the manifest carried a reason reads back unknown, and an unknown one is
    /// not a rollback candidate: refusing is the answer, because "probably the right one" is how a
    /// world gets overwritten with the wrong week.
    /// </remarks>
    private ActionResult Restore(ReactorAction.ProposeRestore action, string actor)
    {
        InstanceBackup? candidate = NewestBackup(action.Instance, BackupReason.PreUpdate);
        if (candidate is null)
        {
            return ActionResult.Failed(
                $"no archive of {action.Instance} records itself as taken before an update, and an "
                + "archive that does not say why it exists is not a rollback candidate");
        }

        KgsmResult result = instances.RestoreBackup(action.Instance, candidate.Id, actor, Origin);

        return result.IsFailure
            ? ActionResult.Failed(Explain(result))
            : ActionResult.Succeeded(
                candidate.Id,
                $"restored {action.Instance} from the archive taken before its update"
                + (candidate.CreatedAt is { } at ? $" on {at:u}" : string.Empty));
    }

    /// <summary>
    /// The most recent archive of an instance taken for <paramref name="reason"/>, or null.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A null reason is not a match.</b> The manifest recording none means nobody knows why that
    /// archive exists, and treating unknown as the reason being looked for is the fabrication this leaf
    /// refuses everywhere else — with a restore on the other end of it.
    /// </remarks>
    private InstanceBackup? NewestBackup(string instance, string reason)
    {
        try
        {
            return instances.GetBackupsDetailed(instance)
                .Where(b => string.Equals(b.Reason, reason, StringComparison.Ordinal))
                .Where(b => !string.IsNullOrWhiteSpace(b.Id))
                .OrderByDescending(b => b.CreatedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read {Instance}'s backups.", instance);
            return null;
        }
    }

    /// <summary>The engine's own words, which are more use than anything this leaf could paraphrase.</summary>
    private static string Explain(KgsmResult result)
    {
        string said = result.Stderr.Trim();
        if (said.Length == 0)
            said = result.Stdout.Trim();

        return said.Length == 0 ? $"the engine exited {result.ExitCode}" : said;
    }
}
