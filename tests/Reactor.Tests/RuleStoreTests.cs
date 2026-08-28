using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The rules directory: what a host runs, and what it does with a rule it cannot honour.
/// </summary>
/// <remarks>
/// ⚠ <b>Nothing here may throw and nothing here may be silent.</b> A daemon that refused to start over
/// one bad rule would take every other rule down with it; one that quietly dropped it would leave
/// somebody watching for a decision that was never going to come. Every case below asserts both halves
/// — what still runs, and what was said about what does not.
/// </remarks>
public class RuleStoreTests
{
    /// <summary>A directory holding one file per entry, named for the id it is given.</summary>
    private static string Dir(params (string Id, string Body)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        foreach ((string id, string body) in files)
            File.WriteAllText(Path.Combine(dir, id + ".json"), body);

        return dir;
    }

    /// <summary>The same, for rules that already exist as definitions.</summary>
    private static string DirOf(params RuleDefinition[] rules) =>
        Dir([.. rules.Select(r => (r.Id, RuleStore.Write(r)))]);

    /// <summary>
    /// A host whose directory is empty judges nothing, and that is not a fault.
    /// </summary>
    /// <remarks>
    /// No rule exists in code, so there is nothing to fall back to and nothing that should be invented.
    /// Somebody who deleted every rule meant to.
    /// </remarks>
    [Fact]
    public void An_empty_directory_means_no_rules()
    {
        RuleSet set = RuleStore.LoadDirectory(Dir());

        Assert.Empty(set.Rules);
        Assert.Empty(set.Retired);
        Assert.Empty(set.Problems);
    }

    [Fact]
    public void A_directory_that_is_not_there_means_no_rules()
    {
        RuleSet set = RuleStore.LoadDirectory(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        Assert.Empty(set.Rules);
        Assert.Empty(set.Problems);
    }

    /// <summary>
    /// ⚠ A rule survives being written out and read back unchanged.
    /// </summary>
    /// <remarks>
    /// Every edit is a write followed by a read, so a round trip that lost a field would change a rule
    /// each time somebody touched it — and would do so most to the rules people edit most.
    /// </remarks>
    [Fact]
    public void A_rule_round_trips_through_its_file()
    {
        RuleSet set = RuleStore.LoadDirectory(DirOf([.. ShippedRules.All]));

        Assert.Empty(set.Problems);
        Assert.Equal(ShippedRules.All.Select(r => r.Id), set.Rules.Select(r => r.Id));
        Assert.Equal(
            ShippedRules.All.Select(RuleStore.Write),
            set.Rules.Select(RuleStore.Write));
    }

    [Fact]
    public void A_stored_rule_decides_exactly_as_the_one_it_was_written_from()
    {
        string dir = DirOf([.. ShippedRules.All]);
        RuleDefinition stored = RuleStore.LoadDirectory(dir).Rules.Single(r => r.Id == "give_up_backup");

        Assert.Equal(ShippedRules.Named("give_up_backup").Rows[0].Message, stored.Rows[0].Message);
        Assert.Equal(TimeSpan.FromMinutes(2), stored.Settle);
        Assert.Equal(TimeSpan.FromMinutes(15), stored.Suppression);
        Assert.Equal(ActionCatalog.CreateBackup, stored.ActionId);
    }

    /// <summary>
    /// ⚠ A file that cannot be parsed costs one rule, not the directory.
    /// </summary>
    /// <remarks>
    /// This is the whole reason a rule is a file. One document meant a typo anywhere took every rule
    /// down with it, at the moment somebody was editing one of them.
    /// </remarks>
    [Fact]
    public void A_file_that_cannot_be_parsed_leaves_the_others_running_and_says_where()
    {
        string dir = Dir(
            ("broken", """{ "id": "broken",, }"""),
            ("ok", RuleStore.Write(Minimal())));

        RuleSet set = RuleStore.LoadDirectory(dir);

        Assert.Equal("ok", Assert.Single(set.Rules).Id);
        string problem = Assert.Single(set.Problems);
        Assert.Contains("broken.json", problem);
        Assert.Contains("line", problem);
    }

    /// <summary>
    /// ⚠ The filename is checked against the id, never used as one.
    /// </summary>
    /// <remarks>
    /// A file somebody copied and renamed without editing would otherwise install a second rule under
    /// the first one's identity, folding two rules' decisions together under one actor.
    /// </remarks>
    [Fact]
    public void A_file_whose_name_disagrees_with_the_id_inside_it_is_refused()
    {
        RuleSet set = RuleStore.LoadDirectory(Dir(("copy_of_ok", RuleStore.Write(Minimal()))));

        Assert.Empty(set.Rules);
        Assert.Contains(set.Problems, p => p.Contains("copy_of_ok.json") && p.Contains("'ok'"));
    }

    [Fact]
    public void Anything_that_is_not_a_rule_file_is_left_alone()
    {
        string dir = DirOf([Minimal()]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "a reminder about the rule next door");
        File.WriteAllText(Path.Combine(dir, "ok.json.bak"), "{ not json at all");

        RuleSet set = RuleStore.LoadDirectory(dir);

        Assert.Equal("ok", Assert.Single(set.Rules).Id);
        Assert.Empty(set.Problems);
    }

    [Fact]
    public void Comments_and_a_trailing_comma_are_what_a_person_writes()
    {
        string dir = Dir(("quiet", """
            // why this rule exists
            {
              "id": "quiet", "name": "Never says anything",
              "wakes": ["server.crashed"],
              "subjects": { "source": "from_event" },
              "rows": [],
              "default": { "then": "doesNotHold", "say": "nothing to report about {subject}" },
              "action": "none", "severity": "info", "settleSeconds": 60,
            }
            """));

        RuleSet set = RuleStore.LoadDirectory(dir);

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
        Assert.Null(RuleStore.LoadDirectory(DirOf([Minimal()])).Rules.Single().Author);
    }

    [Fact]
    public void The_attribution_a_decision_carries_is_the_last_hand_on_the_rule()
    {
        RuleDefinition edited = Minimal() with
        {
            CreatedBy = new RuleAuthorship("discord:tanya", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            UpdatedBy = new RuleAuthorship("local:claude", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)),
        };

        Assert.Equal("local:claude", RuleStore.LoadDirectory(DirOf([edited])).Rules.Single().Author);
    }

    [Fact]
    public void An_authorship_with_an_empty_actor_is_no_authorship()
    {
        string dir = Dir(("ok", """
            {
              "id": "ok", "name": "The first one", "wakes": ["server.crashed"],
              "subjects": { "source": "from_event" },
              "default": { "then": "doesNotHold", "say": "nothing about {subject}" },
              "action": "none", "settleSeconds": 60,
              "createdBy": { "actor": "  ", "at": "2026-08-01T00:00:00+00:00" }
            }
            """));

        Assert.Null(RuleStore.LoadDirectory(dir).Rules.Single().Author);
    }

    // ---- modes ----

    /// <summary>A mode nobody can read is not a licence to guess upward.</summary>
    [Fact]
    public void An_unreadable_mode_observes()
    {
        string dir = Dir(("ok", RuleStore.Write(Minimal() with { Mode = RuleMode.Act })
            .Replace("\"act\"", "\"whatever\"", StringComparison.Ordinal)));

        Assert.Equal(RuleMode.Observe, RuleStore.LoadDirectory(dir).Rules.Single().Mode);
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
