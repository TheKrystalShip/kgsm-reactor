using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Kgsm.Reactor.Actions;
using TheKrystalShip.Kgsm.Reactor.Rules;
using TheKrystalShip.KGSM.Engine.Contracts.Invocation;
using TheKrystalShip.KGSM.Engine.Reading;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

public class ActionPerformerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "reactor-performer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private sealed class FakeProcess(int exit, string stdout, string stderr = "") : IEngineProcess
    {
        public List<string> Arguments { get; } = [];

        public Task<EngineProcessResult> RunAsync(
            string binary, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            string? standardInput, CancellationToken cancellationToken)
        {
            Arguments.AddRange(arguments);
            return Task.FromResult(new EngineProcessResult(exit, stdout, stderr));
        }

        public Task<EngineStreamResult> RunStreamingAsync(
            string binary, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            Stream? source, Stream? destination, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private KgsmActionPerformer Performer(FakeProcess process)
    {
        Directory.CreateDirectory(_home);
        var reader = new EngineReader(new EngineReaderOptions
        {
            InstallRoot = _home,
            HomeFallback = _home,
            Environment = new Dictionary<string, string?> { ["HOME"] = _home },
        });
        return new KgsmActionPerformer(
            new EngineCli(new EngineCliOptions { BinaryPath = "kgsm" }, process), reader, NullLogger<KgsmActionPerformer>.Instance);
    }

    [Fact]
    public async Task ACapturePinsAnIncidentBackupAsTheRuleAndNamesTheArchiveTheEngineReported()
    {
        var process = new FakeProcess(0, """
            {"backup":{"id":"b-1","createdAt":"2026-01-02T03:04:05+00:00","reason":"incident","retention":"pinned","compressed":true,"sources":[],"sizeBytes":2097152,"fileCount":3}}
            """);

        ActionResult result = await Performer(process).PerformAsync(
            new ReactorAction.CreateBackup("g-1"), "rule:crash", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("b-1", result.Artifact);
        Assert.Contains("(2 MB)", result.Detail, StringComparison.Ordinal);
        Assert.Equal(
            ["backup", "create", "g-1", "--reason", "incident", "--retention", "pinned", "--json", "--actor", "rule:crash", "--origin", "reactor"],
            process.Arguments);
    }

    [Fact]
    public async Task AFailedCaptureCarriesTheEnginesOwnWords()
    {
        var process = new FakeProcess(
            1, string.Empty, """{"code":"failed","message":"disk is full","exitCode":1,"details":[]}""");

        ActionResult result = await Performer(process).PerformAsync(
            new ReactorAction.CreateBackup("g-1"), "rule:crash", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("disk is full", result.Detail);
    }
}
