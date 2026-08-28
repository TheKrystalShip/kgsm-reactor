using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The rules about what this leaf's sentences have to say, enforced instead of remembered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every sentence here is read cold, by somebody who was not watching.</b> A message that only
/// makes sense to whoever already knows which rule wrote it, what the settle window is for, or which
/// server the row was about has failed at the one job it has — and none of those failures break a
/// test that checks verdicts.
/// </para>
/// <para>
/// What is checked is the part that can be: a reason names its own subject, an action says what it
/// costs, and no message reaches a person carrying vocabulary from inside this daemon. The prose
/// itself is still somebody's to write well.
/// </para>
/// </remarks>
public class MessageQualityTests
{
    /// <summary>
    /// ⚠ A reason has to stand alone, because it is read where nothing else is.
    /// </summary>
    /// <remarks>
    /// A push notification, a Discord line and an audit row each carry the sentence and not the row
    /// around it. <em>"still given up on"</em> is true and useless: given up on <em>what</em>? The
    /// subject is the one thing a reader cannot supply for themselves.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryRow))]
    public void Every_sentence_a_rule_can_record_names_what_it_is_about(
        string ruleId, string outcome, string message)
    {
        Assert.True(
            message.Contains($"{{{MessageTemplate.SubjectToken}}}", StringComparison.Ordinal),
            $"{ruleId}'s '{outcome}' sentence never names its subject: \"{message}\"");
    }

    /// <summary>
    /// ⚠ Words that mean something only inside this daemon do not reach a person.
    /// </summary>
    /// <remarks>
    /// The gate outcomes are the trap: <em>ceilinged</em> and <em>superseded</em> are precise names for
    /// real things and describe machinery nobody outside this process has any reason to know about. The
    /// wire outcome in the payload is where a program reads them.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryRow))]
    public void No_sentence_a_person_reads_carries_this_daemon_s_own_vocabulary(
        string ruleId, string outcome, string message)
    {
        foreach (string word in Jargon)
        {
            Assert.False(
                message.Contains(word, StringComparison.OrdinalIgnoreCase),
                $"{ruleId}'s '{outcome}' sentence says \"{word}\", which means nothing outside this "
                + $"daemon: \"{message}\"");
        }
    }

    /// <summary>Vocabulary that is precise here and empty anywhere a person reads it.</summary>
    private static readonly string[] Jargon =
        ["ceilinged", "superseded", "settle window", "guard row", "unreadable signal", "episode key"];

    /// <summary>
    /// ⚠ Every action says what it costs, and says it about the action rather than the server.
    /// </summary>
    /// <remarks>
    /// The catalog serves this to an editor, which has no instance to build an action for — so a
    /// consequence that named one would render with a hole in it there, and read as being about one
    /// particular server everywhere else. <c>ActionEntry.Consequence</c> builds against an empty name
    /// on exactly that understanding, and this is the check that holds it.
    /// </remarks>
    [Fact]
    public void What_an_action_costs_is_said_once_and_does_not_name_a_server()
    {
        Assert.All(ActionCatalog.All, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Consequence));

            Assert.Equal(
                entry.Create("alpha").Consequence,
                entry.Create("omega").Consequence);
        });
    }

    /// <summary>
    /// ⚠ What an action would do reads as an offer; what it did is the performer's to say.
    /// </summary>
    /// <remarks>
    /// <see cref="ReactorAction.Describe"/> is written in the infinitive because every sentence that
    /// carries it is about something not yet done — <em>"would archive it"</em>, <em>"offers to archive
    /// it"</em>. A past tense here would read as a completed action in all three.
    /// </remarks>
    [Fact]
    public void What_an_action_would_do_names_the_server_it_would_do_it_to()
    {
        foreach (ActionEntry entry in ActionCatalog.All.Where(a => a.Id != ActionCatalog.None))
        {
            string described = entry.Create("romestead").Describe();

            Assert.Contains("romestead", described, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ⚠ A name the evaluator answers itself cannot also be a measurement.
    /// </summary>
    /// <remarks>
    /// Tokens are resolved before bindings are consulted, so a rule binding one would save cleanly and
    /// then quietly say something else in every sentence that mentioned it. Refused at load instead,
    /// where it lands in <c>/status.problems</c> and somebody sees it.
    /// </remarks>
    [Fact]
    public void A_rule_cannot_bind_a_measurement_under_a_name_the_evaluator_owns()
    {
        // A real signal under a reserved name, so the only thing wrong with this rule is the name. A
        // binding of something this build does not measure would be refused anyway, and would let this
        // pass while proving nothing.
        RuleDefinition shadowing = ShippedRules.All.Single(r => r.Id == "give_up_backup") with
        {
            Signals = [SignalBinding.Of(MessageTemplate.OpenForToken, "world.running")],
        };

        IReadOnlyList<string> problems = RuleValidation.Problems(shadowing);

        Assert.Contains(problems, p =>
            p.Contains(MessageTemplate.OpenForToken, StringComparison.Ordinal)
            && p.Contains("already use for something else", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠ A sentence that cannot date its condition says so rather than dating it from now.
    /// </summary>
    /// <remarks>
    /// The tempting failure is filling the gap with the evaluation instant, which reads as a fault that
    /// has just started — the most misleading thing a message about a crash loop could say. An
    /// evaluation with no opening on record ends the sentence as "cannot tell" instead, which is what
    /// every other unreadable source does here.
    /// </remarks>
    [Fact]
    public async Task A_sentence_that_asks_when_it_began_is_answered_or_refused()
    {
        RuleDefinition dating = ShippedRules.All.Single(r => r.Id == "give_up_backup") with
        {
            Rows =
            [
                new([Clause.True("world.gaveUp")], VerdictKind.Holds,
                    "{subject} has been down for {openFor}, since {openedAt:HH:mm}"),
            ],
        };

        var world = new SupervisorSays(new InstanceRunState("failed", true, 3));
        DateTimeOffset now = new(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

        Verdict dated = await RuleEvaluator.EvaluateAsync(
            dating,
            new EvaluationScope("pz", now, world, new NoHistory(), new NoFootprints(),
                openedAt: now - TimeSpan.FromMinutes(40)),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Holds, dated.Kind);
        Assert.Equal("pz has been down for 40m, since 20:20", dated.Reason);

        Verdict undated = await RuleEvaluator.EvaluateAsync(
            dating,
            new EvaluationScope("pz", now, world, new NoHistory(), new NoFootprints()),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Unreadable, undated.Kind);
        Assert.Contains("cannot be dated", undated.Reason, StringComparison.Ordinal);
    }

    private sealed class SupervisorSays(InstanceRunState state) : IWorldView
    {
        public ValueTask<Reading<InstanceRunState>> InstanceAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<InstanceRunState>.Measured(state));

        public ValueTask<Reading<MemoryDeclaration>> MemoryDeclarationAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryDeclaration>.Unavailable("not asked here"));

        public ValueTask<Reading<InstanceSupervision>> SupervisionAsync(
            string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<InstanceSupervision>.Measured(new InstanceSupervision(3)));
    }

    private sealed class NoHistory : IRuleHistory
    {
        public HistoryEvent? LastOccurrence(string eventType, string subject, DateTimeOffset notBefore) => null;

        public IReadOnlyList<OpenEpisode> OpenEpisodes(
            string opensWith, string closesWith, DateTimeOffset notBefore) => [];

        public (TimeSpan P95, int Samples) EpisodeDuration(
            string opensWith, string closesWith, string subject, DateTimeOffset notBefore) =>
            (TimeSpan.Zero, 0);
    }

    private sealed class NoFootprints : IFootprintSource
    {
        public ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token) =>
            ValueTask.FromResult(Reading<IReadOnlyList<InstanceFootprint>>.Measured([]));

        public ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token) =>
            ValueTask.FromResult(Reading<MemoryTrend>.Unavailable("no series"));
    }

    /// <summary>Every sentence every seeded rule can conclude with, including its unreadable ones.</summary>
    public static TheoryData<string, string, string> EveryRow()
    {
        var rows = new TheoryData<string, string, string>();

        foreach (RuleDefinition rule in ShippedRules.All)
        {
            foreach (GuardRow row in rule.Rows.Append(rule.Default))
            {
                rows.Add(rule.Id, row.Outcome.ToString(), row.Message);

                if (row.UnreadableMessage is { } spare)
                    rows.Add(rule.Id, $"{row.Outcome} (unreadable)", spare);
            }
        }

        return rows;
    }
}
