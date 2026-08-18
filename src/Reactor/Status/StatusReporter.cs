using System.Reflection;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Engine;
using TheKrystalShip.Kgsm.Reactor.Ingest;
using TheKrystalShip.Kgsm.Reactor.Rules;

namespace TheKrystalShip.Kgsm.Reactor.Status;

/// <summary>
/// Assembles the answer the status endpoint gives.
/// </summary>
/// <remarks>
/// It reads the two hosted services directly rather than having them push into a shared object.
/// Everything it reports is a fact one of them already holds, and a copy kept in a third place is a
/// copy that can be stale — which on a status surface is worse than no surface, because the reading
/// is wrong rather than absent.
/// </remarks>
internal sealed class StatusReporter(
    EventIngestService ingest,
    RuleEngine engine,
    IOptions<ReactorOptions> options,
    TimeProvider clock,
    DateTimeOffset startedAt)
{
    private readonly ReactorOptions _options = options.Value;

    /// <summary>The producer id this leaf's journal is written under.</summary>
    private const string Leaf = "kgsm-reactor";

    public ReactorStatus Read()
    {
        DateTimeOffset now = clock.GetUtcNow();

        return new ReactorStatus
        {
            Leaf = Leaf,
            Version = Build,
            StartedAt = startedAt,
            UptimeSeconds = (long)(now - startedAt).TotalSeconds,
            Enabled = _options.Enabled,
            LedgerPath = _options.LedgerPath,
            Gate = new GateStatus(
                _options.SweepIntervalSeconds,
                _options.SuppressionWindowMinutes,
                _options.MaxActionsPerHour),
            Observations = new IngestStatus(ingest.Recorded, ingest.Dropped),
            Decisions = new DecisionStatus(engine.Recorded, engine.Emitted),
            Rules =
            [
                .. engine.Active.Select(rule => new RuleStatus(
                    rule.Id,
                    rule.Shape.ToString().ToLowerInvariant(),
                    rule.Severity.ToString().ToLowerInvariant(),
                    // The mode as resolved, not as configured: a rule named in two lists gets the
                    // safest of them, and reporting what was written in the file would show an
                    // authority the rule does not actually have.
                    (_options.ModeFor(rule.Id) ?? RuleMode.Observe).ToString().ToLowerInvariant())),
            ],
            Pending =
            [
                .. engine.PendingEvaluations.Select(p => new PendingStatus(p.Rule, p.Subject, p.DueAt)),
            ],
            LastSweepAt = engine.LastSweepAt,
        };
    }

    /// <summary>
    /// The running build, version and commit, read from the assembly.
    /// </summary>
    /// <remarks>
    /// The same string the journal stamps on every line this leaf writes, so an operator comparing a
    /// decision against the reactor that is running now is comparing like with like.
    /// </remarks>
    private static string Build { get; } =
        typeof(StatusReporter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
