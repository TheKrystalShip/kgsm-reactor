using System.Text.Json.Serialization;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>One row of kgsm-monitor's <c>GET /footprint</c>.</summary>
/// <remarks>
/// A local mirror of the monitor's response shape rather than a shared package. The monitor's
/// contracts package carries its <em>frame</em>; the footprint is served by the daemon from a
/// daemon-local DTO, so there is nothing to share and inventing something to share would couple two
/// leaves for the sake of one endpoint.
/// </remarks>
internal sealed record FootprintDto(
    string Instance,
    double? WorkingSetPeakBytes,
    double? WorkingSetAvgBytes,
    double? PeakBytes,
    long OomKills,
    long MaxEvents,
    double StallSeconds,
    long Runs,
    double ObservedHours,
    long Samples,
    string FirstSeen,
    string LastSeen);

/// <summary>The body of <c>GET /footprint</c>.</summary>
internal sealed record FootprintResponseDto(IReadOnlyList<FootprintDto> Footprints);

/// <summary>One point of a metrics-history series.</summary>
/// <remarks>
/// <c>Min</c>, <c>Max</c> and <c>N</c> are present only on the rolled-up tier and are not read here:
/// a trend is about where the middle of the distribution sat, and the extremes of a five-minute bucket
/// answer a different question.
/// </remarks>
internal sealed record HistoryPointDto(long Ts, double Value);

/// <summary>The body of <c>GET /metrics/history</c>.</summary>
internal sealed record MetricsHistoryDto(IReadOnlyDictionary<string, List<HistoryPointDto>> Series);

/// <summary>
/// Source-generated serialization for the monitor's responses, so reading them adds no reflection to
/// a Native-AOT leaf.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FootprintResponseDto))]
[JsonSerializable(typeof(MetricsHistoryDto))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;
