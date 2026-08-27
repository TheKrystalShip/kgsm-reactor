using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Kgsm.Reactor.Rules;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The thresholds an operator writes, and what happens to the ones that cannot be honoured.
/// </summary>
/// <remarks>
/// <b>Every case here is about the same failure.</b> A threshold that is silently not applied and a
/// threshold that was never written look identical from outside — the rule goes on deciding, on
/// figures the operator believes they changed. So the assertions are as much about what reaches
/// <c>Problems</c> as about what reaches the rule.
/// </remarks>
public class RuleTuningTests
{
    private const string Drift = "memory_declaration_drift";

    private static RuleTuning Resolve(string rule, params (string Key, double Value)[] written) =>
        RuleTuning.Resolve(
            RuleCatalog.All,
            new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                [rule] = written.ToDictionary(w => w.Key, w => w.Value),
            });

    [Fact]
    public void Every_declared_threshold_is_present_before_anything_overrides_it()
    {
        RuleTuning tuning = RuleTuning.Defaults(RuleCatalog.All);

        Assert.All(RuleCatalog.All, rule =>
        {
            IReadOnlyDictionary<string, double> values = tuning.For(rule.Id);
            Assert.All(rule.Parameters, p => Assert.Equal(p.Default, values[p.Key]));
        });

        Assert.Empty(tuning.Problems);
    }

    [Fact]
    public void A_written_threshold_is_what_the_rule_runs_on()
    {
        RuleTuning tuning = Resolve(Drift, ("min_span_days", 0.5));

        Assert.Equal(0.5, tuning.For(Drift)["min_span_days"]);
        Assert.Empty(tuning.Problems);
    }

    [Fact]
    public void The_thresholds_nobody_wrote_keep_the_figures_they_ship_with()
    {
        RuleTuning tuning = Resolve(Drift, ("min_span_days", 0.5));
        RuleParameter hours = RuleCatalog.ById(Drift)!.Parameters.Single(p => p.Key == "min_observed_hours");

        Assert.Equal(hours.Default, tuning.For(Drift)["min_observed_hours"]);
    }

    /// <summary>
    /// ⚠ The case this whole surface exists for: a misspelling must not read as a default.
    /// </summary>
    [Fact]
    public void A_threshold_the_rule_does_not_declare_is_reported_rather_than_ignored()
    {
        RuleTuning tuning = Resolve(Drift, ("min_span_hours", 5));

        string problem = Assert.Single(tuning.Problems);
        Assert.Contains("min_span_hours", problem);
        // The message names what it could have meant. A rejection that does not is a dead end.
        Assert.Contains("min_span_days", problem);
    }

    [Fact]
    public void A_rule_this_build_does_not_ship_is_reported_rather_than_ignored()
    {
        RuleTuning tuning = Resolve("memory_drift", ("min_span_days", 2));

        string problem = Assert.Single(tuning.Problems);
        Assert.Contains("memory_drift", problem);
        Assert.Contains(Drift, problem);
    }

    [Fact]
    public void A_threshold_below_its_floor_is_raised_to_it_and_said_out_loud()
    {
        RuleTuning tuning = Resolve(Drift, ("min_span_days", -3));

        Assert.Equal(0, tuning.For(Drift)["min_span_days"]);
        Assert.Contains("floor", Assert.Single(tuning.Problems));
    }

    /// <summary>Zero is a setting, not a mistake: it asks for a verdict on whatever has been seen.</summary>
    [Fact]
    public void A_gate_may_be_turned_off_without_complaint()
    {
        RuleTuning tuning = Resolve(Drift, ("min_span_days", 0), ("min_observed_hours", 0));

        Assert.Equal(0, tuning.For(Drift)["min_span_days"]);
        Assert.Empty(tuning.Problems);
    }

    [Fact]
    public void Rule_ids_match_however_they_are_cased_as_they_do_in_the_mode_lists()
    {
        RuleTuning tuning = Resolve("Memory_Declaration_Drift", ("min_span_days", 3));

        Assert.Equal(3, tuning.For(Drift)["min_span_days"]);
        Assert.Empty(tuning.Problems);
    }

    // ---- the file ----

    [Fact]
    public void No_file_is_the_ordinary_case_and_says_nothing()
    {
        RuleTuning tuning = RuleTuningFile.Resolve(
            RuleCatalog.All, Path.Combine(Path.GetTempPath(), "no-such-rules.json"), NullLogger.Instance);

        Assert.Empty(tuning.Problems);
        Assert.Equal(
            RuleCatalog.ById(Drift)!.Parameters.Single(p => p.Key == "min_span_days").Default,
            tuning.For(Drift)["min_span_days"]);
    }

    [Fact]
    public void A_file_is_read_into_the_thresholds_the_rule_runs_on()
    {
        string path = Write("""
            {
              "rules": {
                "memory_declaration_drift": { "min_span_days": 1, "min_observed_hours": 2 }
              }
            }
            """);

        RuleTuning tuning = RuleTuningFile.Resolve(RuleCatalog.All, path, NullLogger.Instance);

        Assert.Equal(1, tuning.For(Drift)["min_span_days"]);
        Assert.Equal(2, tuning.For(Drift)["min_observed_hours"]);
        Assert.Empty(tuning.Problems);
    }

    /// <summary>It is hand-edited over SSH as well as panel-written, so it takes what a person writes.</summary>
    [Fact]
    public void Comments_and_a_trailing_comma_are_read_the_way_a_person_writes_them()
    {
        string path = Write("""
            {
              "rules": {
                // narrowed while the footprint is still young
                "memory_declaration_drift": { "min_span_days": 1, }
              }
            }
            """);

        RuleTuning tuning = RuleTuningFile.Resolve(RuleCatalog.All, path, NullLogger.Instance);

        Assert.Equal(1, tuning.For(Drift)["min_span_days"]);
        Assert.Empty(tuning.Problems);
    }

    /// <summary>
    /// ⚠ A file that cannot be parsed leaves the daemon running and says where the fault is.
    /// </summary>
    [Fact]
    public void An_unparseable_file_keeps_the_shipped_figures_and_names_the_position()
    {
        string path = Write("""{ "rules": { "memory_declaration_drift": { "min_span_days": } } }""");

        RuleTuning tuning = RuleTuningFile.Resolve(RuleCatalog.All, path, NullLogger.Instance);

        Assert.Equal(
            RuleCatalog.ById(Drift)!.Parameters.Single(p => p.Key == "min_span_days").Default,
            tuning.For(Drift)["min_span_days"]);

        string problem = Assert.Single(tuning.Problems);
        Assert.Contains("line", problem);
        Assert.Contains("shipped thresholds", problem);
    }

    private static string Write(string json)
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
