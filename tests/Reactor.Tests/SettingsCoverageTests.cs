using System.Reflection;
using System.Text.Json;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The settings file and the settings type say the same thing.
/// </summary>
/// <remarks>
/// <para>
/// The file is the declared floor and the type is what the daemon binds, and the failure when they
/// disagree is silent in both directions: a key in the file that no property matches binds to
/// nothing, so an operator sets it and watches it do nothing; a property with no key is a knob that
/// exists but is documented nowhere, and therefore is not in the leaf descriptor the Control Panel
/// renders either.
/// </para>
/// <para>
/// The descriptor itself needs no test here — the generator writes it from the same type on every
/// build, so it cannot lag the code.
/// </para>
/// </remarks>
public class SettingsCoverageTests
{
    /// <summary>
    /// The shipped settings file, found by walking up from the test binary to the repo.
    /// </summary>
    /// <remarks>
    /// Read from source rather than copied to the output, deliberately: what this asserts about is the
    /// file that gets installed beside the binary, and a copy is one build step away from being a
    /// different file.
    /// </remarks>
    private static string SettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "Reactor", "kgsm-reactor.settings.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "src/Reactor/kgsm-reactor.settings.json was not found above " + AppContext.BaseDirectory);
    }

    private static JsonElement ReactorSection()
    {
        var options = new JsonDocumentOptions
        {
            // The file is commented, deliberately — it is the one place every knob is explained.
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath()), options);
        return document.RootElement.GetProperty(ReactorSettings.Section).Clone();
    }

    private static IEnumerable<string> BindableProperties() =>
        typeof(ReactorSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name);

    [Fact]
    public void Every_key_in_the_settings_file_binds_to_a_property()
    {
        HashSet<string> properties = [.. BindableProperties()];

        List<string> orphans = ReactorSection()
            .EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !properties.Contains(name))
            .ToList();

        Assert.True(orphans.Count == 0,
            "these keys are declared in kgsm-reactor.settings.json but bind to nothing: "
            + string.Join(", ", orphans));
    }

    [Fact]
    public void Every_property_is_declared_in_the_settings_file()
    {
        HashSet<string> keys = [.. ReactorSection().EnumerateObject().Select(p => p.Name)];

        List<string> undocumented = BindableProperties()
            .Where(name => !keys.Contains(name))
            .ToList();

        Assert.True(undocumented.Count == 0,
            "these knobs exist on ReactorSettings but are declared nowhere in "
            + "kgsm-reactor.settings.json: " + string.Join(", ", undocumented));
    }
}
