using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// Writing a rule, and what the daemon is running afterwards.
/// </summary>
/// <remarks>
/// <b>The property everything here exists for: what is on disk and what is running never disagree.</b>
/// A rule the daemon declines to run must not be stored, and a rule that was stored must be in force
/// before the caller is told so. Both halves are asserted on every write below, because a write that
/// half-happened reads to a person as "I saved it and nothing changed".
/// </remarks>
public sealed class RuleRegistryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"kgsm-reactor-registry-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Opened with no samples: these cases are about writing, and a seeded directory would put four
    // rules in front of every assertion about one.
    private RuleRegistry Open() => new(
        _dir, NullLogger<RuleRegistry>.Instance,
        samples: Path.Combine(Path.GetTempPath(), "kgsm-reactor-no-samples"));

    private static RuleDefinition Rule(string id) => new(
        Id: id,
        Name: "A rule called " + id,
        Wakes: ["server.crashed"],
        SubjectSource: SubjectSourceCatalog.FromEvent,
        SubjectArguments: new Dictionary<string, string>(),
        Signals: [],
        Rows: [],
        Default: new([], VerdictKind.DoesNotHold, "nothing to report about {subject}"),
        ActionId: ActionCatalog.None,
        Severity: KGSM.Events.EventSeverity.Info,
        Settle: TimeSpan.FromSeconds(60));

    [Fact]
    public void A_written_rule_is_on_disk_and_running_before_the_call_returns()
    {
        using RuleRegistry registry = Open();

        Assert.Empty(registry.Replace(Rule("first")));

        Assert.True(File.Exists(Path.Combine(_dir, "first.json")));
        Assert.Equal("first", Assert.Single(registry.Current.Rules).Id);
    }

    /// <summary>
    /// A rule that cannot be honoured is refused, and nothing is written.
    /// </summary>
    /// <remarks>
    /// Storing it and reporting the problem afterwards would leave the directory holding a rule the
    /// daemon declines to run — which survives a restart, and which nobody is looking at any more.
    /// </remarks>
    [Fact]
    public void A_rule_that_cannot_be_honoured_is_refused_and_not_written()
    {
        using RuleRegistry registry = Open();

        IReadOnlyList<string> problems =
            registry.Replace(Rule("silent") with { Default = new([], VerdictKind.Holds, "   ") });

        Assert.Contains(problems, p => p.Contains("without saying why"));
        Assert.False(File.Exists(Path.Combine(_dir, "silent.json")));
        Assert.Empty(registry.Current.Rules);
    }

    /// <summary>
    /// Judged against the set it would join, not on its own.
    /// </summary>
    /// <remarks>
    /// Whether an id collides is a fact about the other rules, so it is the one refusal that cannot be
    /// made by looking at a rule alone — and the one that matters most, because two rules sharing an id
    /// fold their decisions together under one actor.
    /// </remarks>
    [Fact]
    public void Replacing_a_rule_that_exists_overwrites_it_rather_than_colliding()
    {
        using RuleRegistry registry = Open();
        Assert.Empty(registry.Replace(Rule("first")));

        Assert.Empty(registry.Replace(Rule("first") with { Name = "The same rule, rewritten" }));

        RuleDefinition only = Assert.Single(registry.Current.Rules);
        Assert.Equal("The same rule, rewritten", only.Name);
    }

    /// <summary>
    /// Switching a rule off leaves it in the live list, holding what it would do.
    /// </summary>
    /// <remarks>
    /// <b>Both halves matter to the switch that writes it.</b> A rule dropped from the live list is a
    /// rule the panel cannot draw a control for, so switching one off would be a one-way trip; and an
    /// authority lost on the way off is one nobody can be shown before they switch it back on.
    /// </remarks>
    [Fact]
    public void A_rule_switched_off_stays_listed_and_keeps_its_authority()
    {
        using RuleRegistry registry = Open();
        Assert.Empty(registry.Replace(Rule("first") with { Mode = RuleMode.Act }));

        Assert.Empty(registry.Replace(Rule("first") with { Mode = RuleMode.Act, Enabled = false }));

        RuleDefinition off = Assert.Single(registry.Current.Rules);
        Assert.False(off.Enabled);
        Assert.Equal(RuleMode.Act, off.Mode);
        Assert.Empty(registry.Current.Retired);
    }

    /// <summary>The switch survives a restart, because it is a fact about the rule and not a session.</summary>
    [Fact]
    public void A_rule_switched_off_is_still_off_after_a_restart()
    {
        using (RuleRegistry first = Open())
        {
            Assert.Empty(first.Replace(Rule("first") with { Mode = RuleMode.Propose }));
            Assert.Empty(first.Replace(Rule("first") with { Mode = RuleMode.Propose, Enabled = false }));
        }

        using RuleRegistry second = Open();

        RuleDefinition off = Assert.Single(second.Current.Rules);
        Assert.False(off.Enabled);
        Assert.Equal(RuleMode.Propose, off.Mode);
    }

    [Fact]
    public void A_retired_rule_keeps_its_file_and_stops_running()
    {
        using RuleRegistry registry = Open();
        Assert.Empty(registry.Replace(Rule("first")));

        Assert.Empty(registry.Replace(Rule("first") with { Retired = true }));

        Assert.True(File.Exists(Path.Combine(_dir, "first.json")));
        Assert.Empty(registry.Current.Rules);
        Assert.Equal("first", Assert.Single(registry.Current.Retired).Id);
    }

    [Fact]
    public void Removing_a_rule_takes_its_file_with_it()
    {
        using RuleRegistry registry = Open();
        Assert.Empty(registry.Replace(Rule("first")));

        Assert.True(registry.Remove("first"));

        Assert.False(File.Exists(Path.Combine(_dir, "first.json")));
        Assert.Empty(registry.Current.Rules);
        Assert.False(registry.Remove("first"));
    }

    /// <summary>
    /// Everything holding the registry sees a write, rather than the copy it started with.
    /// </summary>
    /// <remarks>
    /// The engine judges through these rules and a redemption re-derives through the same ones. A
    /// holder keeping its own copy would leave the two judging by different rules for as long as the
    /// daemon ran, and nothing would report the disagreement.
    /// </remarks>
    [Fact]
    public void A_write_is_announced_to_whatever_is_holding_the_set()
    {
        using RuleRegistry registry = Open();

        int announced = 0;
        RuleSet? seen = null;
        registry.Changed += set => { announced++; seen = set; };

        Assert.Empty(registry.Replace(Rule("first")));

        Assert.Equal(1, announced);
        Assert.Equal("first", Assert.Single(seen!.Rules).Id);
    }

    /// <summary>
    /// A file written by hand is picked up without anything restarting.
    /// </summary>
    /// <remarks>
    /// Timing-dependent by nature — the watch is debounced, and the assertion polls rather than
    /// sleeping a fixed span so a slow machine waits longer instead of failing.
    /// </remarks>
    [Fact]
    public async Task A_rule_written_by_hand_is_picked_up()
    {
        using RuleRegistry registry = Open();
        Assert.Empty(registry.Current.Rules);

        File.WriteAllText(Path.Combine(_dir, "byhand.json"), RuleStore.Write(Rule("byhand")));

        for (int i = 0; i < 100 && registry.Current.Rules.Count == 0; i++)
            await Task.Delay(50);

        Assert.Equal("byhand", Assert.Single(registry.Current.Rules).Id);
    }

    /// <summary>
    /// A host that has never run the reactor starts with the rules it shipped.
    /// </summary>
    /// <remarks>
    /// No package scriptlet and no privileged step: the leaf creates its own state directory anyway,
    /// so the one moment it knows a host is new is the moment it creates one. Copied rather than
    /// linked — from here they are ordinary rules, and an upgrade must not reach them.
    /// </remarks>
    [Fact]
    public void A_directory_that_did_not_exist_is_seeded_with_the_shipped_samples()
    {
        using RuleRegistry registry = new(_dir, NullLogger<RuleRegistry>.Instance, samples: ShippedRules.Directory);

        Assert.Equal(
            ShippedRules.All.Select(r => r.Id).Order(StringComparer.Ordinal),
            registry.Current.Rules.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Empty(registry.Current.Problems);
    }

    /// <summary>
    /// <b>Deleting every rule sticks.</b>
    /// </summary>
    /// <remarks>
    /// The first-run signal is whether the directory EXISTED, never whether it holds anything. Seeding
    /// an empty directory would put the samples back on the next start and quietly undo somebody who
    /// meant it — and "this host judges nothing" is a state a host is allowed to be in.
    /// </remarks>
    [Fact]
    public void An_emptied_directory_is_not_seeded_again()
    {
        using (RuleRegistry first = new(_dir, NullLogger<RuleRegistry>.Instance, samples: ShippedRules.Directory))
            Assert.NotEmpty(first.Current.Rules);

        foreach (string file in Directory.EnumerateFiles(_dir, "*.json"))
            File.Delete(file);

        using RuleRegistry second = new(_dir, NullLogger<RuleRegistry>.Instance, samples: ShippedRules.Directory);

        Assert.Empty(second.Current.Rules);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.json"));
    }

    /// <summary>An edited rule survives a restart, which is the other half of seeding once.</summary>
    [Fact]
    public void A_second_start_leaves_an_edited_rule_alone()
    {
        using (RuleRegistry first = new(_dir, NullLogger<RuleRegistry>.Instance, samples: ShippedRules.Directory))
        {
            RuleDefinition edited = first.Current.Rules[0] with { Name = "Renamed by somebody" };
            Assert.Empty(first.Replace(edited));
        }

        using RuleRegistry second = new(_dir, NullLogger<RuleRegistry>.Instance, samples: ShippedRules.Directory);

        Assert.Contains(second.Current.Rules, r => r.Name == "Renamed by somebody");
    }

    [Fact]
    public void A_host_with_no_samples_to_install_starts_empty_rather_than_failing()
    {
        using RuleRegistry registry = new(
            _dir, NullLogger<RuleRegistry>.Instance,
            samples: Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        Assert.Empty(registry.Current.Rules);
        Assert.Empty(registry.Current.Problems);
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void A_host_with_no_directory_configured_refuses_a_write_rather_than_pretending()
    {
        using var registry = new RuleRegistry(string.Empty, NullLogger<RuleRegistry>.Instance);

        Assert.Contains(registry.Replace(Rule("first")), p => p.Contains("no rules directory"));
        Assert.Empty(registry.Current.Rules);
    }
}
