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
    public void No_rule_wakes_on_something_this_leaf_wrote_itself()
    {
        // The loop guard, and the reason it is a test rather than a comment. The reactor tails every
        // producer's journal including its own, so a decision it writes comes straight back in. A rule
        // waking on one would decide about its own decision, write that, and be woken by it — at the
        // sweep interval, forever, with a plausible-looking ledger.
        Assert.All(RuleCatalog.All, rule =>
            Assert.DoesNotContain(rule.Wakes, type =>
                type.StartsWith(Events.ReactorEvents.Prefix, StringComparison.Ordinal)));
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

    /// <summary>
    /// Every rule's settle window is a positive span, and no two of them are the same by accident.
    /// </summary>
    /// <remarks>
    /// ⚠ A settle of zero means the rule is judged the instant its event lands, which for a condition
    /// that ever resolves itself is a guarantee of noise — measured here as twelve of twelve threshold
    /// breaches clearing on their own. The floor is not a style rule.
    /// </remarks>
    [Fact]
    public void Every_rule_waits_before_it_judges()
    {
        Assert.All(RuleCatalog.All, rule => Assert.True(
            rule.Settle > TimeSpan.Zero,
            $"{rule.Id} is judged the instant its event lands"));
    }

    /// <summary>
    /// The gate values as measured, pinned so that changing one is a decision rather than a drift.
    /// </summary>
    /// <remarks>
    /// Each of these came from 30 days of this host's journals, and each has a reason recorded beside
    /// it in <c>RuleCatalog</c>. A test that only asserted "some positive number" would let a future
    /// edit quietly undo the measurement; this fails and points at the field that moved.
    /// </remarks>
    [Theory]
    [InlineData("give_up_backup", 120, 15)]       // self-resolve min 83.5s; repeats p95 10.3m
    [InlineData("update_regression", 60, null)]   // crash→ready p95 38s; crash repeats p50 25s → host-wide
    [InlineData("threshold_stuck", 2700, 240)]    // breach→cleared max 39.7m; repeats p50 4.1h
    public void The_measured_gate_values_are_what_ships(string id, int settleSeconds, int? suppressionMinutes)
    {
        Rule rule = Assert.Single(RuleCatalog.All, r => r.Id == id);

        Assert.Equal(TimeSpan.FromSeconds(settleSeconds), rule.Settle);
        Assert.Equal(
            suppressionMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            rule.Suppression);
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
