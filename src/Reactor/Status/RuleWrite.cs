using System.Text.Json.Serialization;

using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

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

    /// <summary>
    /// Who is doing this, as <c>provider:name</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Required, and checked for having been NAMED rather than for being allowed. This leaf holds no
    /// identity system and no tiers; the surface that authenticated the person is what knows whether
    /// editing a rule is theirs to do. What it refuses is an anonymous write, because the actor is
    /// stamped onto the rule and travels from there onto every decision the rule goes on to make.
    /// </remarks>
    [JsonPropertyName("by")]
    public string? By { get; set; }
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
    /// ⚠ <b>The whole point of answering the request rather than a later status read.</b> A refusal
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
