using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// The thresholds file, as it sits on disk.
/// </summary>
/// <remarks>
/// Keyed by rule id, then by parameter key. A wrapper object rather than a bare map so the file has
/// somewhere to grow a sibling field without every reader having to distinguish one from a rule id.
/// </remarks>
internal sealed class RuleTuningDocument
{
    /// <summary>Rule id to parameter key to value.</summary>
    [JsonPropertyName("rules")]
    public Dictionary<string, Dictionary<string, double>> Rules { get; set; } = [];
}

[JsonSourceGenerationOptions(
    // Hand-editable over SSH as well as panel-written, so it tolerates what a person writes: comments
    // explaining why a threshold was moved, and a trailing comma after the last one.
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RuleTuningDocument))]
internal sealed partial class RuleTuningJsonContext : JsonSerializerContext;

/// <summary>
/// Reads the per-rule thresholds an operator has set.
/// </summary>
/// <remarks>
/// <para>
/// <b>A structured file of its own, and the panel owns it.</b> Thresholds are two-dimensional — rule
/// by parameter — and the env-file surface every other knob rides is one flat key per value, which
/// can express that only by flattening the product into a key per rule per threshold and growing the
/// settings type every time a rule gains one.
/// </para>
/// <para>
/// <b>It stays a leaf-owned file even when the panel writes it</b>, which is what keeps this leaf
/// standalone. The default path is inside this daemon's own state directory, so a host with no
/// kgsm-api reads and writes it directly; a panel that manages it writes its own copy and points the
/// daemon at it through <c>Reactor__RulesPath</c> on the existing override channel. The leaf is told
/// a path and never learns whose it is — no leaf depends on the API, and this does not become the
/// first one that does.
/// </para>
/// <para>
/// <b>Read once, at startup.</b> No watcher: applying a configuration change already restarts the
/// unit, and a file provider that watches costs one inotify watch per directory out of the same
/// per-user budget the game servers on this host draw from.
/// </para>
/// </remarks>
internal static class RuleTuningFile
{
    /// <summary>
    /// Load the file, or explain why it could not be.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Nothing here throws and nothing here is silent.</b> A daemon that refused to start over a
    /// stray comma in a thresholds file would be a worse answer than one that starts on its shipped
    /// figures and says what it could not read — but a daemon that quietly fell back would be worse
    /// than both, because the operator would be watching a rule run on numbers they thought they had
    /// changed.
    /// </remarks>
    /// <param name="path">Where the file lives. Absent is the ordinary case, not a fault.</param>
    /// <param name="problem">What stopped it being read, or null when nothing did.</param>
    /// <returns>The overrides, or null when there are none to apply.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? Load(
        string path, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using FileStream stream = File.OpenRead(path);

            RuleTuningDocument? document =
                JsonSerializer.Deserialize(stream, RuleTuningJsonContext.Default.RuleTuningDocument);

            if (document is null)
            {
                problem = $"{path} holds no object — its thresholds were ignored";
                return null;
            }

            return document.Rules.ToDictionary(
                rule => rule.Key,
                rule => (IReadOnlyDictionary<string, double>)rule.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            // The position is the whole value of this message: "line 7, position 22" is the difference
            // between a fixable typo and a file somebody rewrites from scratch.
            problem =
                $"{path} could not be parsed at line {ex.LineNumber}, position {ex.BytePositionInLine}: "
                + $"{ex.Message} — every rule is running on its shipped thresholds";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{path} could not be read: {ex.Message} — every rule is running on its shipped thresholds";
            return null;
        }
    }

    /// <summary>Load the file and resolve it against the catalog, logging whatever could not be honoured.</summary>
    public static RuleTuning Resolve(IReadOnlyList<Rule> rules, string path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? overrides =
            Load(path, out string? problem);

        RuleTuning tuning = RuleTuning.Resolve(rules, overrides, problem);

        foreach (string entry in tuning.Problems)
            logger.LogWarning("rule thresholds: {Problem}", entry);

        return tuning;
    }
}
