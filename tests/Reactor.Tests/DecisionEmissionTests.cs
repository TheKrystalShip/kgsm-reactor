using System.Text.Json;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Events;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.Kgsm.Reactor.Rules;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// What reaches the journal, and what a reader of it gets.
/// </summary>
/// <remarks>
/// Two failures this guards against, both of which would leave a working-looking leaf. A journal
/// written per evaluation rather than per transition buries the six decisions of a day under the
/// thousands of sweeps that found nothing new; and a payload missing one field is only discovered by
/// the consumer that cannot render without it, in another repo, after the shape has been frozen.
/// </remarks>
public class DecisionEmissionTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kgsm-reactor-emit-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static Decision Sample(
        DecisionOutcome outcome = DecisionOutcome.Fired,
        string subject = "starbound",
        SubjectKind kind = SubjectKind.Instance,
        string? author = null) =>
        new(
            Id: Decision.IdFor("give_up_backup", subject, "kgsm-watchdog:s.ndjson:8748"),
            RuleId: "give_up_backup",
            Subject: subject,
            SubjectKind: kind,
            EpisodeKey: "kgsm-watchdog:s.ndjson:8748",
            Severity: EventSeverity.Danger,
            Mode: RuleMode.Observe,
            Outcome: outcome,
            Reason: "still given up on after 60s (6 consecutive failures)",
            RuleAuthor: author,
            Action: "take a pinned backup of starbound",
            ActionName: "create_backup",
            ActionInstance: "starbound",
            ActionState: ActionState.None,
            OpenedAt: Now.AddMinutes(-1),
            DecidedAt: Now,
            Source: new EventSource("kgsm-watchdog", "s.ndjson", 8748, null));

    private ObservationLedger OpenLedger()
    {
        var ledger = new ObservationLedger(_path);
        new DecisionStore(ledger).Initialize();
        return ledger;
    }

    /// <summary>
    /// ⚠ Who shaped the rule survives the ledger, and its absence survives it too.
    /// </summary>
    /// <remarks>
    /// A row written before rules carried an author reads back null rather than being backfilled with
    /// the build's name — the same discipline the subject kind is held to. Stamping an attribution on
    /// a decision that was taken without one would invent a hand that was never on it.
    /// </remarks>
    [Fact]
    public void Who_shaped_the_rule_survives_the_ledger_and_so_does_nobody_having()
    {
        using ObservationLedger ledger = OpenLedger();
        var store = new DecisionStore(ledger);

        store.Record(Sample(subject: "signed", author: "discord:tanya"));
        store.Record(Sample(subject: "unsigned"));

        IReadOnlyList<Decision> read = store.Since(Now.AddMinutes(-5));

        Assert.Equal("discord:tanya", Assert.Single(read, d => d.Subject == "signed").RuleAuthor);
        Assert.Null(Assert.Single(read, d => d.Subject == "unsigned").RuleAuthor);
    }

    /// <summary>
    /// ⚠ A rule edited between two sweeps of one open episode is re-stamped on the next verdict.
    /// </summary>
    /// <remarks>
    /// The decision names the hand that was on the rule when <em>this</em> verdict was reached, not the
    /// hand that happened to be on it when the episode opened.
    /// </remarks>
    [Fact]
    public void A_re_evaluated_episode_carries_whoever_shaped_the_rule_this_time()
    {
        using ObservationLedger ledger = OpenLedger();
        var store = new DecisionStore(ledger);

        store.Record(Sample(DecisionOutcome.Fired, author: "discord:tanya"));
        store.Record(Sample(DecisionOutcome.Settled, author: "local:claude"));

        Assert.Equal("local:claude", Assert.Single(store.Since(Now.AddMinutes(-5))).RuleAuthor);
    }

    [Fact]
    public void The_first_evaluation_of_an_episode_opens_it()
    {
        using ObservationLedger ledger = OpenLedger();
        var store = new DecisionStore(ledger);

        Assert.Equal(DecisionChange.Opened, store.Record(Sample()));
    }

    [Fact]
    public void Re_evaluating_to_the_same_verdict_changes_nothing()
    {
        // The one that matters. A state rule re-reads its condition every sweep; if this reported a
        // change, a threshold open all afternoon would append a journal line every thirty seconds and
        // the record would describe the reactor's polling rather than its judgment.
        using ObservationLedger ledger = OpenLedger();
        var store = new DecisionStore(ledger);

        store.Record(Sample());

        Assert.Equal(DecisionChange.Unchanged, store.Record(Sample()));
    }

    [Fact]
    public void A_verdict_that_moves_is_a_change()
    {
        using ObservationLedger ledger = OpenLedger();
        var store = new DecisionStore(ledger);

        store.Record(Sample(DecisionOutcome.Fired));

        Assert.Equal(DecisionChange.Changed, store.Record(Sample(DecisionOutcome.Settled)));
    }

    [Fact]
    public void A_decision_survives_the_ledger_with_its_kind_and_its_action_intact()
    {
        using ObservationLedger ledger = OpenLedger();
        var store = new DecisionStore(ledger);
        store.Record(Sample(subject: "k10temp/Tctl", kind: SubjectKind.Host));

        Decision read = Assert.Single(store.Since(Now.AddDays(-1)));

        Assert.Equal(SubjectKind.Host, read.SubjectKind);
        Assert.Equal("create_backup", read.ActionName);
        Assert.Equal("starbound", read.ActionInstance);
    }

    [Fact]
    public void An_action_that_operates_on_no_server_reads_back_as_null()
    {
        // Not an empty string. A consumer testing for a server has to be able to get "none", and an
        // empty string is a value that will eventually be rendered as one.
        using ObservationLedger ledger = OpenLedger();
        var store = new DecisionStore(ledger);
        store.Record(Sample() with { ActionName = "none", ActionInstance = null });

        Assert.Null(Assert.Single(store.Since(Now.AddDays(-1))).ActionInstance);
    }

    [Fact]
    public void The_payload_carries_every_field_a_consumer_renders_from()
    {
        // The acceptance test from the plan, as an assertion: kgsm-bot builds a ServerAnnouncement and
        // kgsm-api a NotificationEvent from ONE event, with no second lookup. Each name below is one
        // of those two consumers' inputs.
        JsonElement payload = JsonSerializer.SerializeToElement(
            DecisionEmitter.PayloadFor(Sample()), ReactorJsonContext.Default.DecidedPayload);

        Assert.Equal("give_up_backup", payload.GetProperty("Rule").GetString());
        Assert.Equal("starbound", payload.GetProperty("Subject").GetString());
        Assert.Equal("instance", payload.GetProperty("SubjectKind").GetString());
        Assert.Equal("danger", payload.GetProperty("Severity").GetString());
        Assert.Equal("observe", payload.GetProperty("Mode").GetString());
        Assert.Equal("fired", payload.GetProperty("Outcome").GetString());
        Assert.Equal("create_backup", payload.GetProperty("Action").GetString());
        Assert.Equal("starbound", payload.GetProperty("ActionInstance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("Reason").GetString()));

        // Invariant 1: the decision is never the only record, so the line it came from travels with it.
        Assert.Equal("kgsm-watchdog", payload.GetProperty("SourceProducer").GetString());
        Assert.Equal("s.ndjson", payload.GetProperty("SourceSegment").GetString());
        Assert.Equal(8748, payload.GetProperty("SourceOffset").GetInt64());
    }

    [Fact]
    public void The_source_pointer_carries_the_line_s_own_id()
    {
        // The position finds the line; the id proves it is the right one. A consumer following the
        // pointer into a rewritten segment finds the two disagree, where a position alone would hand
        // it a real, parseable event with nothing to notice.
        const string id = "01a016e9-d535-7b03-8a6a-b26ae718064c";

        JsonElement payload = JsonSerializer.SerializeToElement(
            DecisionEmitter.PayloadFor(Sample() with
            {
                Source = new EventSource("kgsm-watchdog", "s.ndjson", 8748, id),
            }),
            ReactorJsonContext.Default.DecidedPayload);

        Assert.Equal(id, payload.GetProperty("SourceEventId").GetString());
    }

    [Fact]
    public void A_source_line_with_no_id_is_written_as_null_rather_than_omitted()
    {
        // Same rule as ActionInstance, for the same reason: a property that disappeared when it had no
        // value would make a consumer distinguish "the line had no id" from "an older reactor that did
        // not carry this field". Both mean unknown; neither is a mismatch.
        JsonElement payload = JsonSerializer.SerializeToElement(
            DecisionEmitter.PayloadFor(Sample()), ReactorJsonContext.Default.DecidedPayload);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("SourceEventId").ValueKind);
    }

    [Fact]
    public void An_absent_action_instance_is_written_as_null_rather_than_omitted()
    {
        // Absent and null are one spelling in this ecosystem's journals. A property that disappeared
        // when it had no value would make a consumer distinguish "no server" from "an older reactor
        // that did not carry this field", which are different facts it cannot tell apart.
        JsonElement payload = JsonSerializer.SerializeToElement(
            DecisionEmitter.PayloadFor(Sample() with { ActionName = "none", ActionInstance = null }),
            ReactorJsonContext.Default.DecidedPayload);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("ActionInstance").ValueKind);
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
