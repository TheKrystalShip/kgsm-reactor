using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.Kgsm.Reactor.Status;
using TheKrystalShip.KGSM;
using TheKrystalShip.Auth.Sessions;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>What saving a rule requires of the person saving it.</summary>
public sealed class RuleWriteTests
{
    private static SessionPerson Person(params string[] actions) =>
        new("usr_1", "Tanya", new HashSet<string>(actions, StringComparer.Ordinal), DateTimeOffset.MaxValue);

    private static RuleDefinition Rule(RuleMode mode, string action = ActionCatalog.CreateBackup) => new(
        Id: "ok",
        Name: "The first one",
        Wakes: ["server.crashed"],
        SubjectSource: SubjectSourceCatalog.FromEvent,
        SubjectArguments: new Dictionary<string, string>(),
        Signals: [],
        Rows: [],
        Default: new([], VerdictKind.DoesNotHold, "nothing to report about {subject}"),
        ActionId: action,
        Mode: mode,
        Severity: KGSM.Events.EventSeverity.Info,
        Settle: TimeSpan.FromSeconds(60));

    [Fact]
    public void A_rule_that_acts_needs_a_token_listing_what_it_performs()
    {
        Assert.Equal(KgsmActions.ServerBackupsCreate, RuleWrite.MissingAction(Rule(RuleMode.Act), Person()));
        Assert.Null(RuleWrite.MissingAction(Rule(RuleMode.Act), Person(KgsmActions.ServerBackupsCreate)));
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Observe")]
    [InlineData("Propose")]
    public void A_rule_that_never_acts_on_its_own_needs_nothing(string mode) =>
        Assert.Null(RuleWrite.MissingAction(Rule(Enum.Parse<RuleMode>(mode)), Person()));

    [Fact]
    public void A_rule_that_performs_nothing_needs_nothing() =>
        Assert.Null(RuleWrite.MissingAction(Rule(RuleMode.Act, ActionCatalog.None), Person()));
}
