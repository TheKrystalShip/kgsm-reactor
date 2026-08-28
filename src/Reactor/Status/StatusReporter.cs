using System.Reflection;

using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Reactor.Engine;
using TheKrystalShip.Kgsm.Reactor.Ingest;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

using TheKrystalShip.KGSM.Events;

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
            RulesDirectory = _options.RulesDirectory,
            RuleFiles = engine.Rules.Rules.Count + engine.Rules.Retired.Count,
            Problems = engine.Rules.Problems,
            Observations = new IngestStatus(ingest.Recorded, ingest.Dropped),
            Decisions = new DecisionStatus(engine.Recorded, engine.Emitted),
            Rules =
            [
                .. engine.Active.Select(active => Describe(active.Definition, active.Mode)),
            ],
            // Reported from the store rather than from what is running, because nothing runs them.
            Retired =
            [
                .. engine.Rules.Retired.Select(definition => Describe(definition, RuleMode.Off)),
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
    /// <b>The whole definition, because a rule is now something a person wrote.</b> Reporting only
    /// its id, its windows and what it would do described a rule when the predicate was compiled and
    /// the same on every host; now the predicate is the part that differs, and a surface that could
    /// not show it could not explain a decision either.
    /// <list type="bullet">
    /// <item><description>The <b>mode</b> is what the engine will honour, not what the rule asked for.
    /// One asking for an authority this build has not built observes, so the asked-for value travels
    /// beside it rather than in place of it.</description></item>
    /// <item><description>The <b>suppression window</b> is the rule's own where it carries one and the
    /// host-wide setting where it does not. The gate block reports the host-wide figure, and a reader
    /// seeing only that would take it for the window in force on every rule, which for most of these
    /// it is not.</description></item>
    /// <item><description>The <b>rows</b> are in evaluation order, and that order is the semantics —
    /// the first whose clauses all hold decides. A surface re-sorting them would show a rule that
    /// behaves differently from the one running.</description></item>
    /// <item><description>The <b>author</b> is null when nobody is known to have shaped it, which is a
    /// real state rather than a missing lookup.</description></item>
    /// </list>
    /// </remarks>
    private RuleStatus Describe(RuleDefinition definition, RuleMode effective) =>
        new(definition.Id,
            definition.Name,
            definition.Shape.ToString().ToLowerInvariant(),
            definition.Severity.ToString().ToLowerInvariant(),
            effective.ToString().ToLowerInvariant(),
            (int)definition.Settle.TotalSeconds,
            (int)(definition.Suppression
                  ?? TimeSpan.FromMinutes(Math.Max(_options.SuppressionWindowMinutes, 0))).TotalMinutes,
            // Null when they agree: a field that always restates the one beside it invites a surface to
            // render "asked for observe, running observe", which is noise on every healthy host.
            effective == definition.Mode ? null : definition.Mode.ToString().ToLowerInvariant(),
            definition.Wakes,
            definition.SubjectSource,
            definition.SubjectArguments,
            definition.ActionId,
            [
                .. definition.Signals.Select(b =>
                    new SignalBindingStatus(b.Alias, b.SignalId, b.Arguments)),
            ],
            [.. definition.Rows.Select(Describe)],
            Describe(definition.Default),
            definition.Author,
            definition.Retired);

    private static GuardRowStatus Describe(GuardRow row) => new(
        [
            .. row.Clauses.Select(c => new ClauseStatus(
                c.Alias,
                RuleStore.Wire(c.Operator),
                c.Against is Comparand.Literal { Value.Kind: SignalKind.Number } number
                    ? number.Value.Number
                    : null,
                c.Against is Comparand.Literal { Value.Kind: SignalKind.Text } text
                    ? text.Value.Text
                    : null,
                c.Against is Comparand.OfSignal other ? other.Alias : null)),
        ],
        RuleStore.Wire(row.Outcome),
        row.Message,
        row.UnreadableMessage);

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
