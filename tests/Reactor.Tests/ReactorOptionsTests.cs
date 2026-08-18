using Microsoft.Extensions.Configuration;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The settings → options step: what an operator wrote becoming something every consumer can use
/// without re-checking it.
/// </summary>
/// <remarks>
/// Bound through <see cref="IConfiguration"/> rather than by setting properties directly, because the
/// binder's own behaviour is half of what these assert — a blank value and a JSON null reach the
/// settings type differently, and both have to end up as "unset".
/// </remarks>
public class ReactorOptionsTests
{
    private static ReactorOptions Bind(params (string Key, string? Value)[] written)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(written.Select(w =>
                new KeyValuePair<string, string?>($"{ReactorSettings.Section}:{w.Key}", w.Value)))
            .Build();

        ReactorSettings settings =
            configuration.GetSection(ReactorSettings.Section).Get<ReactorSettings>() ?? new ReactorSettings();
        return ReactorOptions.FromSettings(settings);
    }

    [Fact]
    public void Nothing_written_yields_the_coded_defaults()
    {
        ReactorOptions options = Bind();

        Assert.True(options.Enabled);
        Assert.Equal("/usr/bin/kgsm", options.KgsmPath);
        Assert.Equal("/var/lib/kgsm/events", options.JournalDir);
        Assert.Equal(ReactorOptions.DefaultRetentionDays, options.RetentionDays);
        Assert.Equal(ReactorOptions.DefaultFlushIntervalSeconds, options.FlushIntervalSeconds);
    }

    [Fact]
    public void Observation_is_on_unless_it_is_switched_off()
    {
        // A leaf installed and then silently doing nothing is indistinguishable from a broken one.
        Assert.True(Bind().Enabled);
        Assert.False(Bind((nameof(ReactorSettings.Enabled), "false")).Enabled);
    }

    [Fact]
    public void A_blank_number_reads_as_unset_rather_than_taking_the_daemon_down()
    {
        // One stray `Reactor__RetentionDays=` in an env file. Against a non-nullable int the binder
        // throws and the unit fails to start; nullable turns it into "not written".
        ReactorOptions options = Bind((nameof(ReactorSettings.RetentionDays), string.Empty));

        Assert.Equal(ReactorOptions.DefaultRetentionDays, options.RetentionDays);
    }

    [Fact]
    public void Numbers_below_their_floor_are_raised_rather_than_refused()
    {
        ReactorOptions options = Bind(
            (nameof(ReactorSettings.RetentionDays), "0"),
            (nameof(ReactorSettings.FlushIntervalSeconds), "0"));

        Assert.Equal(ReactorOptions.MinRetentionDays, options.RetentionDays);
        Assert.Equal(ReactorOptions.MinFlushIntervalSeconds, options.FlushIntervalSeconds);
    }

    [Fact]
    public void Blank_paths_fall_back_and_written_ones_are_trimmed()
    {
        Assert.Equal("/usr/bin/kgsm", Bind((nameof(ReactorSettings.KgsmPath), "   ")).KgsmPath);
        Assert.Equal("/opt/kgsm/kgsm.sh",
            Bind((nameof(ReactorSettings.KgsmPath), "  /opt/kgsm/kgsm.sh  ")).KgsmPath);
    }

    [Fact]
    public void A_blank_state_root_stays_null_so_the_library_owns_the_default()
    {
        // Null rather than a literal "/var/lib": repeating the library's default here is how the two
        // come to disagree after one of them moves.
        Assert.Null(Bind().StateRoot);
        Assert.Equal("/srv/state", Bind((nameof(ReactorSettings.StateRoot), "/srv/state")).StateRoot);
    }

    [Fact]
    public void The_ledger_lands_in_the_systemd_state_directory_when_nothing_names_it()
    {
        string? previous = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", "/var/lib/kgsm-reactor");
            Assert.Equal(
                Path.Combine("/var/lib/kgsm-reactor", "reactor.db"),
                Bind().LedgerPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", previous);
        }
    }

    [Fact]
    public void Several_state_directories_resolve_to_this_units_own()
    {
        // systemd exports StateDirectory= colon-separated when a unit declares more than one. The
        // first is this unit's; splitting on the wrong one would put the ledger in another service's
        // directory, which it may not even be able to write.
        string? previous = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", "/var/lib/kgsm-reactor:/var/lib/other");
            Assert.Equal(
                Path.Combine("/var/lib/kgsm-reactor", "reactor.db"),
                Bind().LedgerPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", previous);
        }
    }

    [Fact]
    public void A_written_ledger_path_wins_over_the_state_directory()
    {
        string? previous = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", "/var/lib/kgsm-reactor");
            Assert.Equal("/srv/reactor.db",
                Bind((nameof(ReactorSettings.LedgerPath), "/srv/reactor.db")).LedgerPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", previous);
        }
    }
}
