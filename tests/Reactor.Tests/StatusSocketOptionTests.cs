namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// How the status socket is configured.
/// </summary>
/// <remarks>
/// Permission bits are the whole access boundary on this endpoint — there is no auth in front of it,
/// deliberately, because it is a unix socket and the filesystem is the gate. That makes a mis-read
/// mode string the one configuration mistake here that has a security consequence, so the parsing is
/// pinned rather than assumed.
/// </remarks>
public class StatusSocketOptionTests
{
    private static ReactorOptions From(string? path = null, string? mode = null) =>
        ReactorOptions.FromSettings(new ReactorSettings
        {
            StatusSocketPath = path ?? "/run/kgsm-reactor/status.sock",
            StatusSocketMode = mode ?? "660",
            LedgerPath = "/unused",
        });

    [Fact]
    public void The_shipped_default_is_owner_and_group_read_write()
    {
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            From().StatusSocketMode);
    }

    [Fact]
    public void A_mode_is_read_as_octal_not_decimal()
    {
        // 640 octal is user rw + group r. Read as decimal it would be 640 = 0o1200, which sets bits
        // nobody asked for — this is the reason the knob is a quoted string rather than a JSON number.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            From(mode: "640").StatusSocketMode);
    }

    [Fact]
    public void A_leading_zero_is_accepted_the_way_a_person_writes_one()
    {
        Assert.Equal(From(mode: "660").StatusSocketMode, From(mode: "0660").StatusSocketMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rw-rw----")]
    [InlineData("998")]      // not octal digits
    [InlineData("7777")]     // beyond the nine permission bits
    [InlineData("0")]
    public void An_unparseable_mode_falls_back_rather_than_widening(string mode)
    {
        // Never a throw and never a wider mode. A typo must not be able to open the socket to the
        // whole host, and a daemon that refused to start over one would be a worse answer than one
        // that started safe — the fallback is the same value the default ships with.
        Assert.Equal(From().StatusSocketMode, From(mode: mode).StatusSocketMode);
    }

    [Fact]
    public void A_blank_path_turns_the_endpoint_off()
    {
        // Off, not defaulted. Somebody who blanked this wants the reactor judging and answering
        // nobody, and quietly restoring the default path would give them a listening socket they
        // deliberately removed.
        Assert.Equal(string.Empty, From(path: "  ").StatusSocketPath);
    }
}
