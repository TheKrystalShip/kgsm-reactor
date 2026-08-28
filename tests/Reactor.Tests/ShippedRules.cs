using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The rules this build ships, read from the files it ships them as.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the artifacts, not a restatement of them.</b> <c>deploy/rules.d/</c> is copied into
/// the test output and loaded through <see cref="RuleStore.LoadDirectory(string)"/> — the same
/// parser, the same validator and the same refusals the daemon uses. A sample that stops parsing,
/// names a signal this build dropped, or collides with another's id fails the build here rather than
/// on a host.
/// </para>
/// <para>
/// It is also what makes the loader worth trusting: no rule exists in code, so the path every
/// hand-written rule depends on is the path the shipped ones travel too.
/// </para>
/// </remarks>
internal static class ShippedRules
{
    /// <summary>Where the copied sample files land beside the test assembly.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "rules.d");

    private static readonly RuleSet Set = RuleStore.LoadDirectory(Directory);

    /// <summary>Every sample that loads, in id order.</summary>
    public static IReadOnlyList<RuleDefinition> All { get; } =
        [.. Set.Rules.OrderBy(r => r.Id, StringComparer.Ordinal)];

    /// <summary>Whatever could not be honoured. Asserted empty — a sample must always load.</summary>
    public static IReadOnlyList<string> Problems => Set.Problems;

    /// <summary>One sample by id, or a failure naming what is actually there.</summary>
    public static RuleDefinition Named(string id) =>
        All.SingleOrDefault(r => r.Id == id)
        ?? throw new InvalidOperationException(
            $"no shipped rule '{id}' in {Directory} — it holds: {string.Join(", ", All.Select(r => r.Id))}");
}
