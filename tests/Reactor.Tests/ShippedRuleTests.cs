using TheKrystalShip.Kgsm.Reactor.Engine;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The rules a host starts with, and how much authority any rule can actually have.
/// </summary>
/// <remarks>
/// A rule that is live but structurally unable to evaluate is the failure this file exists to prevent:
/// it looks configured, it appears on the status socket, and it decides nothing forever. Every check
/// here runs through the same validator the daemon runs at load, so a sample cannot be held to a
/// different standard from a rule somebody writes.
/// </remarks>
public class ShippedRuleTests
{
    [Fact]
    public void Every_shipped_sample_loads()
    {
        Assert.Empty(ShippedRules.Problems);
        Assert.NotEmpty(ShippedRules.All);
    }

    /// <summary>
    /// A sample arrives observing, which is the authority a rule has to earn its way out of.
    /// </summary>
    /// <remarks>
    /// It is the difference between a host that starts recording what it would have concluded and one
    /// that starts acting on rules nobody has read yet. Promoting one is a decision somebody makes
    /// after reading what it decided.
    /// </remarks>
    [Fact]
    public void Every_shipped_sample_arrives_observing() =>
        Assert.All(ShippedRules.All, rule => Assert.Equal(RuleMode.Observe, rule.Mode));

    /// <summary>
    /// The id inside a sample is the name of its file.
    /// </summary>
    /// <remarks>
    /// ⚠ The loader refuses a mismatch, so this is not what protects the daemon — it is what stops a
    /// sample from being shipped that the loader would then refuse on every host that installs it.
    /// </remarks>
    [Fact]
    public void Every_shipped_sample_is_named_for_its_id() =>
        Assert.All(ShippedRules.All, rule => Assert.True(
            File.Exists(Path.Combine(ShippedRules.Directory, rule.Id + ".json")),
            $"{rule.Id} is not in a file called {rule.Id}.json"));

    [Fact]
    public void Every_shipped_rule_passes_the_validator_a_written_one_faces()
    {
        Assert.All(ShippedRules.All, rule =>
            Assert.Empty(RuleValidation.Problems(rule)));
    }

    [Fact]
    public void Every_shipped_rule_has_a_distinct_id()
    {
        // The id is the actor string an audit row carries, and the key the decision id is derived from.
        // Two rules sharing one would silently merge their decisions.
        string[] ids = [.. ShippedRules.All.Select(r => r.Id)];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// ⚠ The loop guard, which is now enforced by construction rather than by this test.
    /// </summary>
    /// <remarks>
    /// The reactor tails every producer's journal including its own, so a decision it writes comes
    /// straight back in. A rule waking on one would decide about its own decision, write that, and be
    /// woken by it — at the sweep interval, forever, with a plausible-looking ledger. What this asserts
    /// now is that the refusal exists at all: the samples are clean, and a rule that named one is
    /// rejected at load with every other rule still running.
    /// </remarks>
    [Fact]
    public void No_rule_can_wake_on_something_this_leaf_wrote_itself()
    {
        Assert.All(ShippedRules.All, rule =>
            Assert.DoesNotContain(rule.Wakes, type =>
                type.StartsWith(Events.ReactorEvents.Prefix, StringComparison.Ordinal)));

        RuleDefinition looping = ShippedRules.All[0] with
        {
            Id = "loops",
            Wakes = [Events.ReactorEvents.Decided],
        };

        Assert.Contains(
            RuleValidation.Problems(looping),
            problem => problem.Contains("its own decisions"));
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
    /// ⚠ Everything a rule may do is a case of a closed union, and the catalog renders it rather than
    /// widening it.
    /// </summary>
    /// <remarks>
    /// The never-list — never uninstall, never delete a backup, never rewrite instance config, never
    /// moderate a player — is a compiler question, and this is what keeps it one now that a rule is
    /// data: a rule naming an action outside the catalog is refused, and the catalog cannot grow a case
    /// the union does not have.
    /// </remarks>
    [Fact]
    public void The_action_catalog_offers_exactly_what_the_union_holds()
    {
        Assert.Equal(
            new[] { "none", "create_backup", "propose_restore" }.Order(StringComparer.Ordinal),
            ActionCatalog.All.Select(a => a.Id).Order(StringComparer.Ordinal));

        Assert.All(ActionCatalog.All, entry =>
            Assert.Equal(entry.Id, entry.Create("an-instance").Name));
    }

    /// <summary>
    /// Every rule's settle window is a positive span.
    /// </summary>
    /// <remarks>
    /// ⚠ A settle of zero means the rule is judged the instant its event lands, which for a condition
    /// that ever resolves itself is a guarantee of noise — measured here as twelve of twelve threshold
    /// breaches clearing on their own. The floor is not a style rule, and it is refused at load rather
    /// than only failing a build.
    /// </remarks>
    [Fact]
    public void Every_rule_waits_before_it_judges()
    {
        Assert.All(ShippedRules.All, rule => Assert.True(
            rule.Settle > TimeSpan.Zero,
            $"{rule.Id} is judged the instant its event lands"));

        Assert.Contains(
            RuleValidation.Problems(ShippedRules.All[0] with { Settle = TimeSpan.Zero }),
            problem => problem.Contains("the instant its event lands"));
    }

    /// <summary>
    /// The gate values as measured, pinned so that changing one is a decision rather than a drift.
    /// </summary>
    /// <remarks>
    /// Each came from 30 days of this host's journals, and each has a reason recorded beside it in the
    /// sample file that carries it. A test that only asserted "some positive number" would let a future edit
    /// quietly undo the measurement; this fails and points at the field that moved.
    /// </remarks>
    [Theory]
    [InlineData("give_up_backup", 120, 15)]       // self-resolve min 83.5s; repeats p95 10.3m
    [InlineData("update_regression", 60, null)]   // crash→ready p95 38s; crash repeats p50 25s → host-wide
    [InlineData("threshold_stuck", 2700, 240)]    // breach→cleared max 39.7m; repeats p50 4.1h
    [InlineData("memory_declaration_drift", 120, 1440)] // nothing to settle; a fortnight's figure holds a day
    public void The_measured_gate_values_are_what_ships(string id, int settleSeconds, int? suppressionMinutes)
    {
        RuleDefinition rule = Assert.Single(ShippedRules.All, r => r.Id == id);

        Assert.Equal(TimeSpan.FromSeconds(settleSeconds), rule.Settle);
        Assert.Equal(
            suppressionMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            rule.Suppression);
    }

    /// <summary>
    /// The shape follows from where a rule's subjects come from, and cannot disagree with them.
    /// </summary>
    /// <remarks>
    /// A rule whose subject arrives with the event is edge-shaped by definition; one that enumerates
    /// its own is state-shaped and cannot miss a wake. Asking somebody to state both would be asking
    /// them to contradict themselves.
    /// </remarks>
    [Theory]
    [InlineData("give_up_backup", "edge")]
    [InlineData("update_regression", "edge")]
    [InlineData("threshold_stuck", "state")]
    [InlineData("memory_declaration_drift", "state")]
    public void A_rules_shape_follows_from_where_its_subjects_come_from(string id, string shape)
    {
        Assert.Equal(
            shape,
            Assert.Single(ShippedRules.All, r => r.Id == id).Shape.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// ⚠ An edge rule that names no event would be permanently silent while looking configured.
    /// </summary>
    [Fact]
    public void An_edge_rule_must_name_something_that_wakes_it()
    {
        Assert.All(
            ShippedRules.All.Where(r => r.Shape == RuleShape.Edge),
            rule => Assert.NotEmpty(rule.Wakes));

        Assert.Contains(
            RuleValidation.Problems(ShippedRules.All[0] with { Wakes = [] }),
            problem => problem.Contains("names no event to wake on"));
    }

    // ---- how much authority a rule can have ----

    [Fact]
    public void Every_shipped_rule_starts_in_observe()
    {
        // What a host gets when nobody has configured anything. Every rule watching, nothing staged,
        // nothing performed — the state a rule has to earn its way out of.
        Assert.All(ShippedRules.All, rule => Assert.Equal(RuleMode.Observe, rule.Mode));
    }

    /// <summary>
    /// A rule gets exactly the authority it asks for, up to the ceiling this build holds.
    /// </summary>
    /// <remarks>
    /// ⚠ The failure this guards is a status page contradicting the daemon. Being set to act and
    /// silently observed is exactly what the engine refuses to allow quietly — a surface echoing the
    /// mode the rule asked for would make the refusal invisible, since the warning it logs lives in a
    /// journal nobody reads.
    /// </remarks>
    [Fact]
    public void A_rule_gets_the_authority_it_asks_for_up_to_the_ceiling()
    {
        Assert.All(
            Enum.GetValues<RuleMode>().Where(m => m <= RuleEngine.Honours),
            asked => Assert.Equal(asked, RuleEngine.Effective(asked)));

        Assert.All(
            Enum.GetValues<RuleMode>().Where(m => m > RuleEngine.Honours),
            asked => Assert.Equal(RuleEngine.Honours, RuleEngine.Effective(asked)));
    }

    [Fact]
    public void The_effective_mode_never_raises_what_was_asked_for()
    {
        // Whatever this build grows to honour, the clamp is downwards only: a rule left in observe must
        // never be lifted by a later phase teaching the engine to act, and a rule that is off must never
        // be woken by one.
        foreach (RuleMode asked in Enum.GetValues<RuleMode>())
            Assert.True(RuleEngine.Effective(asked) <= asked);

        Assert.Equal(RuleMode.Off, RuleEngine.Effective(RuleMode.Off));
    }
}
