using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The questions a single event cannot answer.
/// </summary>
/// <remarks>
/// <c>threshold_stuck</c> rests entirely on these, and each one is wrong in a way that would still
/// look like a working rule: an episode attributed to the wrong subject, a duration measured from the
/// wrong opening, or a percentile quoted from three samples as though it described normality.
/// </remarks>
public class LedgerRuleHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-history-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private long _offset;

    private ObservationLedger Open() => new(_path);

    private void Record(ObservationLedger ledger, string type, string subject, DateTimeOffset at) =>
        ledger.Record([
            new Observation("kgsm-monitor", "s.ndjson", _offset++, null, type, EventClass.Threshold,
                SubjectKind.Host, subject, null, null, at, at),
        ]);

    [Fact]
    public void An_episode_with_no_closing_reads_as_open()
    {
        using ObservationLedger ledger = Open();
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-40));

        var history = new LedgerRuleHistory(ledger);
        OpenEpisode episode = Assert.Single(
            history.OpenEpisodes("host.threshold.breached", "host.threshold.cleared", Now.AddDays(-30)));

        Assert.Equal("k10temp/Tctl", episode.Subject);
        Assert.Equal(Now.AddMinutes(-40), episode.OpenedAt);
        // The opening line's position is the episode's identity, and it has to come back with it.
        Assert.Equal("kgsm-monitor", episode.Source.Producer);
    }

    [Fact]
    public void An_episode_that_closed_is_not_open()
    {
        using ObservationLedger ledger = Open();
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-40));
        Record(ledger, "host.threshold.cleared", "k10temp/Tctl", Now.AddMinutes(-35));

        var history = new LedgerRuleHistory(ledger);
        Assert.Empty(history.OpenEpisodes(
            "host.threshold.breached", "host.threshold.cleared", Now.AddDays(-30)));
    }

    [Fact]
    public void One_subject_closing_does_not_close_anothers_episode()
    {
        // Two sensors breaching the same metric are two episodes. Matched globally rather than per
        // subject, one clearing would read as though both had.
        using ObservationLedger ledger = Open();
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-40));
        Record(ledger, "host.threshold.breached", "nvme/Composite", Now.AddMinutes(-38));
        Record(ledger, "host.threshold.cleared", "k10temp/Tctl", Now.AddMinutes(-35));

        var history = new LedgerRuleHistory(ledger);
        OpenEpisode episode = Assert.Single(
            history.OpenEpisodes("host.threshold.breached", "host.threshold.cleared", Now.AddDays(-30)));

        Assert.Equal("nvme/Composite", episode.Subject);
    }

    [Fact]
    public void A_reopened_episode_reads_as_open_again()
    {
        using ObservationLedger ledger = Open();
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddHours(-4));
        Record(ledger, "host.threshold.cleared", "k10temp/Tctl", Now.AddHours(-3));
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-20));

        var history = new LedgerRuleHistory(ledger);
        OpenEpisode episode = Assert.Single(
            history.OpenEpisodes("host.threshold.breached", "host.threshold.cleared", Now.AddDays(-30)));

        Assert.Equal(Now.AddMinutes(-20), episode.OpenedAt);
    }

    [Fact]
    public void A_duration_is_measured_from_the_first_opening_of_an_episode()
    {
        // A condition that re-announces itself three times before clearing is one episode that lasted
        // the whole span, not three that lasted a third each. Measured the other way, every window
        // derived from this would be far too short.
        using ObservationLedger ledger = Open();
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-60));
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-55));
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-50));
        Record(ledger, "host.threshold.cleared", "k10temp/Tctl", Now.AddMinutes(-40));

        var history = new LedgerRuleHistory(ledger);
        (TimeSpan p95, int samples) = history.EpisodeDuration(
            "host.threshold.breached", "host.threshold.cleared", "k10temp/Tctl", Now.AddDays(-30));

        Assert.Equal(1, samples);
        Assert.Equal(TimeSpan.FromMinutes(20), p95);
    }

    [Fact]
    public void The_sample_count_comes_back_with_the_percentile()
    {
        // A p95 over three episodes is not a distribution, and a rule comparing against one has to be
        // able to refuse rather than pretend. That is only possible if the count travels with it.
        using ObservationLedger ledger = Open();
        for (int i = 1; i <= 3; i++)
        {
            Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddHours(-i * 2));
            Record(ledger, "host.threshold.cleared", "k10temp/Tctl", Now.AddHours(-i * 2).AddMinutes(5));
        }

        var history = new LedgerRuleHistory(ledger);
        (_, int samples) = history.EpisodeDuration(
            "host.threshold.breached", "host.threshold.cleared", "k10temp/Tctl", Now.AddDays(-30));

        Assert.Equal(3, samples);
    }

    [Fact]
    public void No_closed_episodes_reads_as_no_samples_rather_than_a_zero_duration()
    {
        using ObservationLedger ledger = Open();
        Record(ledger, "host.threshold.breached", "k10temp/Tctl", Now.AddMinutes(-40));

        var history = new LedgerRuleHistory(ledger);
        (TimeSpan p95, int samples) = history.EpisodeDuration(
            "host.threshold.breached", "host.threshold.cleared", "k10temp/Tctl", Now.AddDays(-30));

        Assert.Equal(0, samples);
        Assert.Equal(TimeSpan.Zero, p95);
    }

    [Fact]
    public void The_last_occurrence_respects_the_window_it_is_asked_about()
    {
        using ObservationLedger ledger = Open();
        Record(ledger, "server.update.finished", "Ketchup", Now.AddHours(-3));

        var history = new LedgerRuleHistory(ledger);

        Assert.NotNull(history.LastOccurrence("server.update.finished", "Ketchup", Now.AddHours(-4)));
        // Outside the window is not "no update ever" — it is "none in the window the rule asked about",
        // which is exactly what update_regression needs to distinguish.
        Assert.Null(history.LastOccurrence("server.update.finished", "Ketchup", Now.AddMinutes(-30)));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (string file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { /* a temp file the OS still holds */ }
        }
    }
}
