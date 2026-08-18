using TheKrystalShip.Kgsm.Reactor.Rules;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The catalog's own integrity, and how a rule's mode is resolved.
/// </summary>
/// <remarks>
/// A rule that is enabled but structurally unable to evaluate is the failure this file exists to
/// prevent: it looks configured, it appears in the descriptor, and it decides nothing forever.
/// </remarks>
public class RuleCatalogTests
{
    [Fact]
    public void Every_rule_has_a_distinct_id()
    {
        // The id is the actor string an audit row would carry, and the key the decision id is derived
        // from. Two rules sharing one would silently merge their decisions.
        string[] ids = [.. RuleCatalog.All.Select(r => r.Id)];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_rule_names_at_least_one_event_that_wakes_it()
    {
        Assert.All(RuleCatalog.All, rule => Assert.NotEmpty(rule.Wakes));
    }

    [Fact]
    public void Every_state_rule_can_enumerate_its_own_subjects()
    {
        // A state rule's whole advantage is that it rediscovers its condition rather than depending on
        // an event arriving. One with nothing to enumerate has that advantage and no way to use it.
        Assert.All(
            RuleCatalog.All.Where(r => r.Shape == RuleShape.State),
            rule => Assert.NotNull(rule.Subjects));
    }

    [Fact]
    public void Every_rule_describes_the_action_it_would_take()
    {
        Assert.All(RuleCatalog.All, rule =>
            Assert.False(string.IsNullOrWhiteSpace(rule.Action("some-instance").Describe())));
    }

    [Fact]
    public void Only_a_restore_changes_the_server()
    {
        // The composition carve-out depends on this being right. A backup marked as changing the
        // server would let the proposal beside it supersede the very archive it refers to.
        Assert.False(new ReactorAction.CreateBackup("x").ChangesServerState);
        Assert.False(new ReactorAction.Nothing().ChangesServerState);
        Assert.True(new ReactorAction.ProposeRestore("x").ChangesServerState);
    }

    [Fact]
    public void A_rule_named_in_no_list_is_off()
    {
        ReactorOptions options = ReactorOptions.FromSettings(new ReactorSettings
        {
            RulesObserve = "give_up_backup",
        });

        Assert.Equal(RuleMode.Observe, options.ModeFor("give_up_backup"));
        Assert.Null(options.ModeFor("threshold_stuck"));
    }

    [Fact]
    public void A_rule_named_in_two_lists_gets_the_safer_one()
    {
        // Two lists disagreeing is a configuration mistake, and the only safe way to resolve one is
        // downwards: somebody who meant to grant more authority notices that nothing acted, where
        // somebody who meant to grant less would not notice that something did.
        ReactorOptions options = ReactorOptions.FromSettings(new ReactorSettings
        {
            RulesObserve = "give_up_backup",
            RulesAct = "give_up_backup",
        });

        Assert.Equal(RuleMode.Observe, options.ModeFor("give_up_backup"));
    }

    [Fact]
    public void Mode_lists_tolerate_the_spacing_a_person_writes()
    {
        ReactorOptions options = ReactorOptions.FromSettings(new ReactorSettings
        {
            RulesObserve = " give_up_backup , threshold_stuck ,",
        });

        Assert.Equal(RuleMode.Observe, options.ModeFor("give_up_backup"));
        Assert.Equal(RuleMode.Observe, options.ModeFor("threshold_stuck"));
        Assert.Null(options.ModeFor("update_regression"));
    }

    [Fact]
    public void The_shipped_default_puts_every_rule_in_observe_and_none_beyond_it()
    {
        // What a host gets when nobody has configured anything. Every rule watching, nothing staged,
        // nothing performed — the state a rule has to earn its way out of.
        ReactorOptions options = ReactorOptions.FromSettings(new ReactorSettings());

        Assert.All(RuleCatalog.All, rule =>
            Assert.Equal(RuleMode.Observe, options.ModeFor(rule.Id)));
    }
}
