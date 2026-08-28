using System.Text.Json;

using TheKrystalShip.Kgsm.Reactor.Engine;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.Kgsm.Reactor.Status;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// What <c>/catalog</c> publishes, and whether a panel built on it can write a file this leaf reads.
/// </summary>
/// <remarks>
/// ⚠ <b>The failure this guards is a panel that composes a rule the leaf refuses.</b> The editor is
/// rendered entirely from this payload, so an operator spelling it does not read back, or a signal it
/// names that the catalog does not carry, produces a rule that saves cleanly and is then dropped at
/// load — which presents as "I saved it and nothing happened", the one outcome this whole surface
/// exists to prevent.
/// </remarks>
public class ReactorCatalogTests
{
    private static ReactorCatalog Catalog => ReactorCatalog.Read();

    [Fact]
    public void Every_operator_it_offers_is_one_the_store_reads_back()
    {
        Assert.All(Catalog.Operators, entry =>
            Assert.Contains(
                Enum.GetValues<ClauseOperator>(),
                op => string.Equals(RuleStore.Wire(op), entry.Id, StringComparison.Ordinal)));

        // And nothing is left out: an operator the file accepts but the catalog hides is one nobody
        // can reach from the panel, which makes it a feature only somebody with SSH knows about.
        Assert.Equal(
            Enum.GetValues<ClauseOperator>().Length,
            Catalog.Operators.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_outcome_it_offers_is_one_a_step_can_conclude()
    {
        Assert.Equal(
            Enum.GetValues<VerdictKind>().Select(RuleStore.Wire).Order(StringComparer.Ordinal),
            Catalog.Outcomes.Select(o => o.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// ⚠ Everything the shipped rules use is offered, which is what makes the seeds a worked example.
    /// </summary>
    /// <remarks>
    /// A person's first rule is usually a copy of one already there. A signal a seed reads that the
    /// catalog does not name would leave them unable to rebuild what they can plainly see running.
    /// </remarks>
    [Fact]
    public void Everything_the_shipped_rules_use_is_something_the_catalog_offers()
    {
        HashSet<string> signals = [.. Catalog.Signals.Select(s => s.Id)];
        HashSet<string> sources = [.. Catalog.SubjectSources.Select(s => s.Id)];
        HashSet<string> actions = [.. Catalog.Actions.Select(a => a.Id)];

        Assert.All(ShippedRules.All, rule =>
        {
            Assert.Contains(rule.SubjectSource, sources);
            Assert.Contains(rule.ActionId, actions);

            foreach (SignalBinding binding in rule.Signals)
                Assert.Contains(binding.SignalId, signals);

            foreach (GuardRow row in rule.Rows.Append(rule.Default))
            {
                foreach (Clause clause in row.Clauses)
                {
                    // An alias is either bound by the rule or is a signal that needs no binding.
                    Assert.True(
                        rule.Binding(clause.Alias) is not null || signals.Contains(clause.Alias),
                        $"{rule.Id} compares '{clause.Alias}', which the catalog does not offer");
                }
            }
        });
    }

    /// <summary>
    /// ⚠ A signal that takes arguments says so, and every argument says whether it must be supplied.
    /// </summary>
    /// <remarks>
    /// A panel that rendered a required argument as optional produces a rule the leaf refuses at load
    /// with a message about a field the person was never shown.
    /// </remarks>
    [Fact]
    public void Every_argument_says_whether_it_must_be_supplied()
    {
        IEnumerable<ArgumentInfo> arguments =
            Catalog.Signals.SelectMany(s => s.Arguments)
                .Concat(Catalog.SubjectSources.SelectMany(s => s.Arguments));

        Assert.All(arguments, argument =>
        {
            Assert.False(string.IsNullOrWhiteSpace(argument.Key));
            Assert.False(string.IsNullOrWhiteSpace(argument.Label));
            // Required and having a default are the same question asked twice; they must not disagree.
            Assert.Equal(argument.Default is null, argument.Required);
        });
    }

    /// <summary>
    /// ⚠ The shape a rule has follows from its subject source, and the catalog says which.
    /// </summary>
    [Fact]
    public void Each_subject_source_declares_the_shape_it_produces()
    {
        Assert.All(Catalog.SubjectSources, source =>
            Assert.Equal(source.FromEvent ? "edge" : "state", source.Shape));
    }

    /// <summary>
    /// The authority ceiling comes from the leaf, so a panel does not have to know which phases exist.
    /// </summary>
    /// <remarks>
    /// Read from the engine rather than restated, and spelled the way every other enumerated value on
    /// the wire is. A literal here would be a second declaration of the ceiling, free to disagree with
    /// the one the daemon actually enforces — which is the exact failure the field exists to prevent.
    /// </remarks>
    [Fact]
    public void It_says_how_much_authority_this_build_will_honour()
    {
        Assert.Equal(RuleEngine.Honours.ToString().ToLowerInvariant(), Catalog.Honours);
        Assert.Contains(Catalog.Honours, new[] { "off", "observe", "propose", "act" });
    }

    /// <summary>
    /// ⚠ Every signal carries prose, because a list of ids is not something a person can compose from.
    /// </summary>
    [Fact]
    public void Every_signal_names_itself_in_words()
    {
        Assert.All(Catalog.Signals, signal =>
        {
            Assert.Matches("^[a-z][a-zA-Z0-9]*\\.[a-z][a-zA-Z0-9]*$", signal.Id);
            Assert.False(string.IsNullOrWhiteSpace(signal.Label));
            Assert.False(string.IsNullOrWhiteSpace(signal.Description));
        });
    }

    /// <summary>
    /// It serialises under Native AOT, where there is no reflection to fall back on.
    /// </summary>
    /// <remarks>
    /// A type nobody registered on the source-generated context throws at runtime rather than
    /// degrading, and this endpoint's payload is nested several records deep.
    /// </remarks>
    [Fact]
    public void The_whole_payload_serialises_through_the_generated_context()
    {
        string json = JsonSerializer.Serialize(
            Catalog, ReactorCatalogJsonContext.Default.ReactorCatalog);

        using JsonDocument parsed = JsonDocument.Parse(json);

        Assert.NotEmpty(parsed.RootElement.GetProperty("signals").EnumerateArray());
        Assert.NotEmpty(parsed.RootElement.GetProperty("actions").EnumerateArray());
        Assert.NotEmpty(parsed.RootElement.GetProperty("operators").EnumerateArray());
        Assert.Equal(
            RuleEngine.Honours.ToString().ToLowerInvariant(),
            parsed.RootElement.GetProperty("honours").GetString());
    }
}
