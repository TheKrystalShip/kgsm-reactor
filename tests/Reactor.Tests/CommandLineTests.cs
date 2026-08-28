using System.Diagnostics;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// What the binary does with its arguments before it becomes a daemon.
/// </summary>
/// <remarks>
/// The failure this exists for was real and quiet: an unrecognised flag fell through to the daemon
/// path and started a <b>second</b> reactor against the same SQLite ledger — two writers, one of them
/// nobody knew about. Exercised through the built binary rather than a parser, because the defect was
/// in what <c>Main</c> did with the arguments, and a unit test of a parser would have passed
/// throughout.
/// </remarks>
public class CommandLineTests
{
    private static readonly string Binary = Path.Combine(
        AppContext.BaseDirectory, "kgsm-reactor");

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var start = new ProcessStartInfo(Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in args)
            start.ArgumentList.Add(arg);

        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        // Generous, and it is not a timing assertion: every path under test refuses or prints and
        // exits immediately. A run that reaches this timeout has become a daemon, which is the bug.
        Assert.True(process.WaitForExit(20_000), "the binary did not exit — it started the daemon");

        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void An_unrecognised_flag_is_refused_rather_than_starting_the_daemon()
    {
        (int exit, _, string err) = Run("--version");

        Assert.Equal(2, exit);
        Assert.Contains("unrecognised argument: --version", err, StringComparison.Ordinal);
        // The refusal names what it would accept. A refusal that does not is one somebody argues with.
        Assert.Contains("--report", err, StringComparison.Ordinal);
        Assert.Contains("--decisions", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_what_it_does_is_not_an_error()
    {
        (int exit, string output, _) = Run("--help");

        Assert.Equal(0, exit);
        Assert.Contains("--report", output, StringComparison.Ordinal);
        Assert.Contains("--decisions", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_ledger_is_reported_rather_than_created()
    {
        // The report must never bring a ledger into being: an empty database answers every question
        // with "nothing happened", which is indistinguishable from a host that was quiet.
        string absent = Path.Combine(Path.GetTempPath(), $"kgsm-reactor-absent-{Guid.NewGuid():N}.db");

        (int exit, _, string err) = Run("--decisions", "--ledger", absent);

        Assert.Equal(1, exit);
        Assert.Contains("no ledger at", err, StringComparison.Ordinal);
        Assert.False(File.Exists(absent), "the report created the ledger it was meant to read");
    }
}
