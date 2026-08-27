using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The rules file: what a host runs, and what it does with a rule it cannot honour.
/// </summary>
/// <remarks>
/// ⚠ <b>Nothing here may throw and nothing here may be silent.</b> A daemon that refused to start over
/// one bad rule would take every other rule down with it; one that quietly dropped it would leave
/// somebody watching for a decision that was never going to come. Every case below asserts both halves
/// — what still runs, and what was said about what does not.
/// </remarks>
public class RuleStoreTests
{
    private static string Write(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void A_host_with_no_file_runs_the_rules_this_build_ships()
    {
        RuleSet set = RuleStore.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"));

        Assert.Equal(SeededRules.All.Count, set.Rules.Count);
        Assert.Empty(set.Problems);
        Assert.Empty(set.Retired);
    }

    /// <summary>
    /// ⚠ The seeds survive being written out and read back unchanged.
    /// </summary>
    /// <remarks>
    /// Which is what makes seeding a file a safe thing to do: a host that writes its shipped rules out
    /// so they can be edited must get back exactly the rules it was running, or the act of exposing
    /// them changes them.
    /// </remarks>
    [Fact]
    public void The_shipped_rules_round_trip_through_the_file()
    {
        string path = Write(RuleStore.Write(SeededRules.All));

        RuleSet set = RuleStore.Load(path);

        Assert.Empty(set.Problems);
        Assert.Equal(
            SeededRules.All.Select(r => r.Id),
            set.Rules.Select(r => r.Id));
        Assert.Equal(RuleStore.Write(SeededRules.All), RuleStore.Write(set.Rules));
    }

    [Fact]
    public void A_stored_rule_decides_exactly_as_the_seed_it_was_written_from()
    {
        string path = Write(RuleStore.Write(SeededRules.All));
        RuleDefinition stored = RuleStore.Load(path).Rules.Single(r => r.Id == "give_up_backup");

        Assert.Equal(
            SeededRules.All.Single(r => r.Id == "give_up_backup").Rows[0].Message,
            stored.Rows[0].Message);
        Assert.Equal(TimeSpan.FromMinutes(2), stored.Settle);
        Assert.Equal(TimeSpan.FromMinutes(15), stored.Suppression);
        Assert.Equal(ActionCatalog.CreateBackup, stored.ActionId);
    }

    [Fact]
    public void A_file_that_cannot_be_parsed_leaves_the_shipped_rules_running_and_says_where()
    {
        string path = Write("""{ "rules": [ { "id": "x",, } ] }""");

        RuleSet set = RuleStore.Load(path);

        Assert.Equal(SeededRules.All.Count, set.Rules.Count);
        string problem = Assert.Single(set.Problems);
        Assert.Contains("line", problem);
        Assert.Contains("the shipped rules are running", problem);
    }

    [Fact]
    public void Comments_and_a_trailing_comma_are_what_a_person_writes()
    {
        string path = Write("""
            {
              // why this rule exists
              "rules": [
                {
                  "id": "quiet", "name": "Never says anything",
                  "wakes": ["server.crashed"],
                  "subjects": { "source": "from_event" },
                  "rows": [],
                  "default": { "then": "doesNotHold", "say": "nothing to report about {subject}" },
                  "action": "none", "severity": "info", "settleSeconds": 60,
                },
              ],
            }
            """);

        RuleSet set = RuleStore.Load(path);

        Assert.Empty(set.Problems);
        Assert.Equal("quiet", Assert.Single(set.Rules).Id);
    }

    /// <summary>
    /// ⚠ The loop guard, enforced by the catalog rather than by a test over it.
    /// </summary>
    /// <remarks>
    /// The reactor tails every producer's journal including its own, so a rule woken by a decision it
    /// wrote would decide about its own decision, write that, and be woken by it — at the sweep
    /// interval, forever, with a plausible-looking ledger.
    /// </remarks>
    [Fact]
    public void A_rule_cannot_wake_on_what_this_leaf_wrote_itself()
    {
        RuleSet set = RuleStore.Resolve([Minimal() with { Wakes = ["reactor.decided"] }], []);

        Assert.Empty(set.Rules);
        Assert.Contains(set.Problems, p => p.Contains("its own decisions"));
    }

    [Fact]
    public void A_rule_reading_something_this_build_does_not_measure_is_refused_and_the_rest_run()
    {
        RuleDefinition broken = Minimal() with
        {
            Id = "broken",
            Rows = [new([new Clause("footprint.spanDaze", ClauseOperator.LessThan, Comparand.Literal.Number(2))],
                VerdictKind.Unreadable, "too little")],
        };

        RuleSet set = RuleStore.Resolve([broken, Minimal()], []);

        Assert.Equal("ok", Assert.Single(set.Rules).Id);
        Assert.Contains(set.Problems, p => p.Contains("footprint.spanDaze"));
    }

    [Fact]
    public void A_rule_whose_sentence_names_something_it_never_binds_is_refused()
    {
        RuleSet set = RuleStore.Resolve(
            [Minimal() with { Default = new([], VerdictKind.DoesNotHold, "nothing about {mystery}") }], []);

        Assert.Empty(set.Rules);
        Assert.Contains(set.Problems, p => p.Contains("mystery"));
    }

    [Fact]
    public void A_step_with_no_sentence_is_refused()
    {
        RuleSet set = RuleStore.Resolve(
            [Minimal() with { Default = new([], VerdictKind.Holds, "  ") }], []);

        Assert.Empty(set.Rules);
        Assert.Contains(set.Problems, p => p.Contains("without saying why"));
    }

    [Fact]
    public void A_rule_judged_the_instant_its_event_lands_is_refused()
    {
        RuleSet set = RuleStore.Resolve([Minimal() with { Settle = TimeSpan.Zero }], []);

        Assert.Empty(set.Rules);
        Assert.Contains(set.Problems, p => p.Contains("the instant its event lands"));
    }

    [Fact]
    public void A_rule_that_would_do_something_this_build_cannot_do_is_refused()
    {
        RuleSet set = RuleStore.Resolve([Minimal() with { ActionId = "uninstall" }], []);

        Assert.Empty(set.Rules);
        Assert.Contains(set.Problems, p => p.Contains("uninstall"));
    }

    [Fact]
    public void An_edge_rule_that_names_no_event_would_be_permanently_silent_and_is_refused()
    {
        RuleSet set = RuleStore.Resolve([Minimal() with { Wakes = [] }], []);

        Assert.Empty(set.Rules);
        Assert.Contains(set.Problems, p => p.Contains("names no event to wake on"));
    }

    /// <summary>
    /// ⚠ An id names one rule forever, retired ones included.
    /// </summary>
    /// <remarks>
    /// It is the actor on every journal line and ledger row the rule produced. An id that resolved to
    /// one rule last year and a different one now is worse than having no name at all.
    /// </remarks>
    [Fact]
    public void Two_rules_cannot_share_an_id()
    {
        RuleSet set = RuleStore.Resolve([Minimal(), Minimal() with { Name = "The other one" }], []);

        Assert.Equal("The first one", Assert.Single(set.Rules).Name);
        Assert.Contains(set.Problems, p => p.Contains("names more than one rule"));
    }

    [Fact]
    public void A_retired_rule_is_kept_and_never_evaluated()
    {
        RuleSet set = RuleStore.Resolve([Minimal() with { Retired = true }], []);

        Assert.Empty(set.Rules);
        Assert.Equal("ok", Assert.Single(set.Retired).Id);
        Assert.Empty(set.Problems);
    }

    // ---- authorship ----

    /// <summary>
    /// ⚠ A rule written by hand over SSH carries no identity and is not given one.
    /// </summary>
    /// <remarks>
    /// The same enforcement made everywhere else in this ecosystem that an actor is stamped: there is
    /// no fallback to the OS user, because the OS user is the daemon rather than a person, and a rule
    /// attributed to <c>kgsm</c> would read as somebody having authored it.
    /// </remarks>
    [Fact]
    public void A_rule_nobody_signed_stays_unattributed()
    {
        string path = Write(RuleStore.Write([Minimal()]));

        Assert.Null(RuleStore.Load(path).Rules.Single().Author);
    }

    [Fact]
    public void The_attribution_a_decision_carries_is_the_last_hand_on_the_rule()
    {
        RuleDefinition edited = Minimal() with
        {
            CreatedBy = new RuleAuthorship("discord:tanya", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            UpdatedBy = new RuleAuthorship("local:claude", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)),
        };

        string path = Write(RuleStore.Write([edited]));

        Assert.Equal("local:claude", RuleStore.Load(path).Rules.Single().Author);
    }

    [Fact]
    public void An_authorship_with_an_empty_actor_is_no_authorship()
    {
        string path = Write("""
            {
              "rules": [
                {
                  "id": "ok", "name": "The first one", "wakes": ["server.crashed"],
                  "subjects": { "source": "from_event" },
                  "default": { "then": "doesNotHold", "say": "nothing about {subject}" },
                  "action": "none", "settleSeconds": 60,
                  "createdBy": { "actor": "  ", "at": "2026-08-01T00:00:00+00:00" }
                }
              ]
            }
            """);

        Assert.Null(RuleStore.Load(path).Rules.Single().Author);
    }

    // ---- modes ----

    /// <summary>A mode nobody can read is not a licence to guess upward.</summary>
    [Fact]
    public void An_unreadable_mode_observes()
    {
        string path = Write(RuleStore.Write([Minimal() with { Mode = RuleMode.Act }])
            .Replace("\"act\"", "\"whatever\"", StringComparison.Ordinal));

        Assert.Equal(RuleMode.Observe, RuleStore.Load(path).Rules.Single().Mode);
    }

    [Fact]
    public void Being_off_and_being_retired_are_different_things()
    {
        RuleSet set = RuleStore.Resolve(
            [Minimal() with { Mode = RuleMode.Off }, Minimal() with { Id = "gone", Retired = true }], []);

        // Off is still a live rule: listed, editable, one field from running again.
        Assert.Equal(RuleMode.Off, Assert.Single(set.Rules).Mode);
        Assert.Equal("gone", Assert.Single(set.Retired).Id);
    }

    /// <summary>The smallest rule that can run: one event, one conclusion, one sentence.</summary>
    private static RuleDefinition Minimal() => new(
        Id: "ok",
        Name: "The first one",
        Wakes: ["server.crashed"],
        SubjectSource: SubjectSourceCatalog.FromEvent,
        SubjectArguments: new Dictionary<string, string>(),
        Signals: [],
        Rows: [],
        Default: new([], VerdictKind.DoesNotHold, "nothing to report about {subject}"),
        ActionId: ActionCatalog.None,
        Severity: KGSM.Events.EventSeverity.Info,
        Settle: TimeSpan.FromSeconds(60));
}
