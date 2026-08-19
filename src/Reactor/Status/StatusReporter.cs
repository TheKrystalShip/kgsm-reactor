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
            Honours = RuleEngine.Honours.ToString().ToLowerInvariant(),
            Observations = new IngestStatus(ingest.Recorded, ingest.Dropped),
            Decisions = new DecisionStatus(engine.Recorded, engine.Emitted),
            Rules =
            [
                .. engine.Active.Select(rule => Describe(rule)),
            ],
            Pending =
            [
                .. engine.PendingEvaluations.Select(p => new PendingStatus(p.Rule, p.Subject, p.DueAt)),
            ],
            LastSweepAt = engine.LastSweepAt,
        };
    }

    /// <summary>
    /// One rule, as it is actually running.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Everything here is resolved rather than configured</b>, and each of the three has a
    /// different way of going wrong if it is not:
    /// <list type="bullet">
    /// <item><description>The <b>mode</b> is what the engine will honour. A rule named in two lists
    /// gets the safest of them, and one asking for an authority this build has not built observes —
    /// so the configured value travels beside it rather than in place of it.</description></item>
    /// <item><description>The <b>suppression window</b> is the rule's own where it carries one and the
    /// host-wide setting where it does not. The gate block reports the host-wide figure, and a reader
    /// seeing only that would take it for the window in force on every rule, which for two of the
    /// three here it is not.</description></item>
    /// <item><description>What it <b>wakes on</b> and what it <b>would do</b> come from the compiled
    /// rule, because nothing configures either. They are reported so a reader can tell what a rule is
    /// for without having the source open.</description></item>
    /// </list>
    /// </remarks>
    private RuleStatus Describe(Rule rule)
    {
        RuleMode configured = _options.ModeFor(rule.Id) ?? RuleMode.Observe;
        RuleMode effective = RuleEngine.Effective(configured);

        return new RuleStatus(
            rule.Id,
            rule.Shape.ToString().ToLowerInvariant(),
            rule.Severity.ToString().ToLowerInvariant(),
            effective.ToString().ToLowerInvariant(),
            (int)rule.Settle.TotalSeconds,
            (int)(rule.Suppression
                  ?? TimeSpan.FromMinutes(Math.Max(_options.SuppressionWindowMinutes, 0))).TotalMinutes,
            // Null when they agree: a field that always restates the one beside it invites a surface to
            // render "configured observe, running observe", which is noise on every healthy host.
            effective == configured ? null : configured.ToString().ToLowerInvariant(),
            rule.Wakes,
            // The action is declared per subject, but its NAME does not vary by one — it is the stable
            // string other programs compare against, where Describe() is prose that names the instance.
            rule.Action(string.Empty).Name);
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
