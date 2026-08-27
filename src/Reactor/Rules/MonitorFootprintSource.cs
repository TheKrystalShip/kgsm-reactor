using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// Reads kgsm-monitor over its unix socket.
/// </summary>
/// <remarks>
/// <para>
/// The same transport shape kgsm-lib uses for the watchdog: an ordinary <see cref="HttpClient"/> whose
/// connections are dialed at a <see cref="UnixDomainSocketEndPoint"/>. The host in the URI is a
/// placeholder the daemon ignores.
/// </para>
/// <para>
/// <b>Every failure is a reading, not an exception.</b> A monitor that is not installed, not running,
/// or mid-redeploy has to reach the rule as "cannot tell" — an unreachable socket and a measurement
/// of zero must never look alike to something that decides.
/// </para>
/// </remarks>
internal sealed class MonitorFootprintSource : IFootprintSource, IDisposable
{
    /// <summary>
    /// The series a trend is read from.
    /// </summary>
    /// <remarks>
    /// The working set, not <c>memBytes</c>. That one charges reclaimable page cache, which grows to
    /// fill whatever allowance it is given — a trend computed from it would report cache filling as a
    /// workload growing.
    /// </remarks>
    private const string WorkingSetMetric = "memAnonBytes";

    /// <summary>The window a trend is computed over.</summary>
    /// <remarks>
    /// The longest the monitor retains, because the question is whether a world has stopped growing
    /// and a fortnight of it is the shortest answer worth having.
    /// </remarks>
    private const string TrendRange = "30d";

    /// <summary>
    /// The fewest points a trend may be computed from.
    /// </summary>
    /// <remarks>
    /// Below this the halves being compared are single readings and their difference is noise wearing
    /// a direction. A rule told "flat" on that basis would lower a figure on no evidence.
    /// </remarks>
    private const int MinTrendPoints = 24;

    private readonly HttpClient _http;
    private readonly ILogger<MonitorFootprintSource> _logger;
    private bool _disposed;

    public MonitorFootprintSource(string socketPath, ILogger<MonitorFootprintSource> logger)
    {
        _logger = logger;
        _http = new HttpClient(BuildSocketHandler(socketPath))
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>Test seam: an injected client stands in for the daemon.</summary>
    internal MonitorFootprintSource(HttpClient http, ILogger<MonitorFootprintSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    private static SocketsHttpHandler BuildSocketHandler(string socketPath) => new()
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    /// <inheritdoc/>
    public async ValueTask<Reading<IReadOnlyList<InstanceFootprint>>> AllAsync(CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("/footprint", token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The endpoint is not mapped, which the monitor does when the footprint is switched
                // off. Nothing is being accumulated, which is a different fact from nothing having
                // been measured yet — and the rule must not read it as an empty history.
                return Reading<IReadOnlyList<InstanceFootprint>>.Unavailable(
                    "the monitor is not accumulating footprints");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Reading<IReadOnlyList<InstanceFootprint>>.Unavailable(
                    $"the monitor answered {(int)response.StatusCode} for /footprint");
            }

            FootprintResponseDto? body = await response.Content
                .ReadFromJsonAsync(MonitorJsonContext.Default.FootprintResponseDto, token)
                .ConfigureAwait(false);

            if (body is null)
                return Reading<IReadOnlyList<InstanceFootprint>>.Unavailable("the monitor sent no body");

            return Reading<IReadOnlyList<InstanceFootprint>>.Measured(
                [.. body.Footprints.Select(Convert)]);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "the monitor's /footprint is not a shape this build can read");
            return Reading<IReadOnlyList<InstanceFootprint>>.Unavailable(
                $"the monitor's /footprint is not a shape this build can read: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "could not read /footprint from the monitor");
            return Reading<IReadOnlyList<InstanceFootprint>>.Unavailable(
                "the monitor could not be reached");
        }
    }

    /// <inheritdoc/>
    public async ValueTask<Reading<MemoryTrend>> TrendAsync(string instance, CancellationToken token)
    {
        try
        {
            string path = $"/metrics/history?kind=server&id={Uri.EscapeDataString(instance)}&range={TrendRange}";
            using HttpResponseMessage response = await _http.GetAsync(path, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Reading<MemoryTrend>.Unavailable(
                    $"the monitor answered {(int)response.StatusCode} for the history of {instance}");

            MetricsHistoryDto? body = await response.Content
                .ReadFromJsonAsync(MonitorJsonContext.Default.MetricsHistoryDto, token)
                .ConfigureAwait(false);

            if (body is null || !body.Series.TryGetValue(WorkingSetMetric, out List<HistoryPointDto>? points))
                return Reading<MemoryTrend>.Unavailable($"no working-set series for {instance}");

            if (points.Count < MinTrendPoints)
            {
                return Reading<MemoryTrend>.Unavailable(
                    $"{points.Count} working-set points for {instance}, fewer than the {MinTrendPoints} "
                    + "a direction can be read from");
            }

            return Reading<MemoryTrend>.Measured(Compute(points));
        }
        catch (JsonException ex)
        {
            // ⚠ Not folded into the catch below. A daemon that is not answering and one whose answer
            // this build cannot parse call for opposite responses — start the monitor, or fix the
            // reader — and a single message for both sends every reader to the wrong one first.
            _logger.LogWarning(
                ex, "the monitor's history of {Instance} is not a shape this build can read", instance);
            return Reading<MemoryTrend>.Unavailable(
                $"the monitor's history is not a shape this build can read: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "could not read the working-set history of {Instance}", instance);
            return Reading<MemoryTrend>.Unavailable("the monitor could not be reached");
        }
    }

    /// <summary>
    /// Compare the later half of a window against the earlier half.
    /// </summary>
    /// <remarks>
    /// Halves rather than a regression: the question is only which way this has been going, the series
    /// is irregular (it exists only while the instance runs), and a slope fitted to that would carry a
    /// precision the sampling does not support.
    /// </remarks>
    internal static MemoryTrend Compute(IReadOnlyList<HistoryPointDto> points)
    {
        var ordered = points.OrderBy(p => p.Ts).ToList();
        int half = ordered.Count / 2;

        double first = ordered.Take(half).Average(p => p.Value);
        double second = ordered.Skip(ordered.Count - half).Average(p => p.Value);

        double growth = first <= 0 ? 0 : (second - first) / first * 100.0;
        return new MemoryTrend(ordered.Count, Math.Round(growth, 1));
    }

    private static InstanceFootprint Convert(FootprintDto d) => new(
        Instance: d.Instance,
        WorkingSetPeakBytes: d.WorkingSetPeakBytes,
        WorkingSetAvgBytes: d.WorkingSetAvgBytes,
        PeakBytes: d.PeakBytes,
        OomKills: d.OomKills,
        MaxEvents: d.MaxEvents,
        StallSeconds: d.StallSeconds,
        Runs: d.Runs,
        ObservedHours: d.ObservedHours,
        SpanDays: SpanDays(d.FirstSeen, d.LastSeen),
        Samples: d.Samples);

    /// <summary>
    /// Calendar days the observations span, or 0 when either end cannot be read.
    /// </summary>
    /// <remarks>
    /// Zero is the honest floor here rather than a null: a span that cannot be established is a span
    /// of nothing as far as a coverage gate is concerned, and the gate refuses on it either way.
    /// </remarks>
    private static double SpanDays(string first, string last)
    {
        if (!DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset from)
            || !DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset to))
        {
            return 0;
        }

        double days = (to - from).TotalDays;
        return days < 0 ? 0 : days;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
