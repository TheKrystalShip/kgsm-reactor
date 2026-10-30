using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;
using TheKrystalShip.KGSM.Sessions;

namespace TheKrystalShip.Kgsm.Reactor.Status;

/// <summary>What a rule write carries.</summary>
/// <remarks>
/// The rule is the same shape a rule file holds, so a panel saves exactly the object it previewed and
/// there is no second schema to keep in step.
/// </remarks>
internal sealed class RuleWriteRequest
{
    /// <summary>The rule to store, in the file's own shape.</summary>
    [JsonPropertyName("rule")]
    public RuleDocument? Rule { get; set; }
}

/// <summary>What saving a rule requires of the person saving it.</summary>
internal static class RuleWrite
{
    /// <summary>
    /// The first action <paramref name="definition"/> performs that <paramref name="person"/>'s token does
    /// not list, or null when they hold every one.
    /// </summary>
    /// <remarks>
    /// Only a rule in <c>act</c> mode performs anything on its own: an offer is carried out for whoever
    /// confirms it, with their own token. The check is cluster-wide because an action is: a token lists an
    /// action or it does not.
    /// </remarks>
    public static string? MissingAction(RuleDefinition definition, KgsmPerson person) =>
        definition.Mode != RuleMode.Act
            ? null
            : ActionCatalog.Build(definition.ActionId, string.Empty).Performs.FirstOrDefault(a => !person.Holds(a));
}

/// <summary>What became of a write.</summary>
internal sealed class RuleWriteResult
{
    /// <summary>Whether the rule is now running.</summary>
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    /// <summary>
    /// What could not be honoured, empty when it was stored.
    /// </summary>
    /// <remarks>
    /// <b>The whole point of answering the request rather than a later status read.</b> A refusal
    /// arrives while the person is still looking at what they wrote, and nothing was written — so a
    /// rule can never sit on disk in a state the daemon then declines to run.
    /// </remarks>
    [JsonPropertyName("problems")]
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>The ids running after the write, so a caller sees the set it joined.</summary>
    [JsonPropertyName("live")]
    public required IReadOnlyList<string> Live { get; init; }
}

/// <summary>
/// The serializer for the rule-write endpoints.
/// </summary>
/// <remarks>
/// Source-generated, because this binary is Native AOT: there is no reflection fallback, and a type
/// nobody registered throws at runtime rather than degrading.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RuleWriteRequest))]
[JsonSerializable(typeof(RuleWriteResult))]
internal partial class RuleWriteJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
