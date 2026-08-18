using System.Text.Json;

using TheKrystalShip.Kgsm.Reactor.Classification;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// Reducing an envelope to the facts the ledger records.
/// </summary>
/// <remarks>
/// The cases here are the ones that go wrong <em>silently</em>: a type bucketed as the operation it
/// is the failure of, a subject read from the wrong property, and a payload that names its subject as
/// an explicit JSON null. Each would produce a ledger that looks complete and measures the wrong
/// thing.
/// </remarks>
public class EventClassifierTests
{
    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    private static EventFacts Classify(string eventType, string json = "{}", string producer = "kgsm") =>
        EventClassifier.Classify(eventType, Payload(json), producer);

    [Theory]
    [InlineData("instance_crashed")]
    [InlineData("instance_failed")]
    [InlineData("instance_update_failed")]
    [InlineData("instance_deploy_failed")]
    [InlineData("instance_download_failed")]
    [InlineData("instance_uninstall_failed")]
    public void A_failure_is_a_fault_even_when_it_shares_a_prefix_with_the_work_it_failed(string type)
    {
        // instance_update_failed starts with "instance_update", which is Maintenance. Bucketed there
        // it would be filed beside the successful updates and disappear from the fault reading — the
        // one reading a rule would be built on.
        Assert.Equal(EventClass.Fault, Classify(type, """{"InstanceName":"factorio"}""").Class);
    }

    // The expected bucket travels as a string because EventClass is internal to the daemon and a
    // public [InlineData] cannot name an internal type. Compared by name, which is also how the
    // ledger stores it.
    [Theory]
    [InlineData("instance_started", "Lifecycle")]
    [InlineData("instance_ready", "Lifecycle")]
    [InlineData("instance_stop_finished", "Lifecycle")]
    [InlineData("instance_restart_stopped", "Lifecycle")]
    [InlineData("instance_update_started", "Maintenance")]
    [InlineData("instance_backup_created", "Maintenance")]
    [InlineData("instance_player_joined", "Player")]
    [InlineData("instance_ports_opened", "Network")]
    [InlineData("instance_upnp_reasserted", "Network")]
    [InlineData("blueprint_updated", "Configuration")]
    [InlineData("assistant_action_declined", "Assistant")]
    [InlineData("host_threshold_breached", "Threshold")]
    public void Types_land_in_the_bucket_a_reader_would_expect(string type, string expected)
    {
        Assert.Equal(expected, Classify(type, """{"InstanceName":"factorio"}""").Class.ToString());
    }

    [Fact]
    public void An_unrecognised_type_is_still_recorded_with_its_subject()
    {
        // The whole reason ingestion is raw. A type this build has never heard of is exactly the sort
        // of thing a later rule might be about, and dropping it would look like an event that never
        // happened.
        EventFacts facts = Classify("instance_teleported_sideways", """{"InstanceName":"Ketchup"}""");

        Assert.Equal(EventClass.Other, facts.Class);
        Assert.Equal(SubjectKind.Instance, facts.SubjectKind);
        Assert.Equal("Ketchup", facts.Subject);
    }

    [Fact]
    public void A_payload_that_names_no_subject_says_so_rather_than_guessing()
    {
        EventFacts facts = Classify("instance_started", """{"Something":"else"}""");

        Assert.Equal(SubjectKind.Unknown, facts.SubjectKind);
        Assert.Equal(string.Empty, facts.Subject);
    }

    [Fact]
    public void A_leaf_event_is_about_the_component_that_wrote_it()
    {
        // leaf_* payloads name a component of the leaf, never the leaf itself. The producer is the
        // only thing that says which one it was.
        EventFacts facts = Classify(
            "leaf_degraded", """{"Component":"net-meter","Detail":"unreadable"}""", producer: "kgsm-monitor");

        Assert.Equal(EventClass.Leaf, facts.Class);
        Assert.Equal(SubjectKind.Leaf, facts.SubjectKind);
        Assert.Equal("kgsm-monitor", facts.Subject);
    }

    [Fact]
    public void A_host_scoped_threshold_is_subjected_to_its_measured_reference()
    {
        // Two sensors breaching the same metric are two episodes. A subject that collapsed them to
        // the metric would make one look like a repeat of the other — which is precisely the reading
        // a suppression window would be derived from.
        EventFacts facts = Classify(
            "host_threshold_breached",
            """{"RuleKey":"host-temp","Metric":"HostTempC","Scope":"host","Ref":"k10temp/Tctl","ServerId":null}""",
            producer: "kgsm-monitor");

        Assert.Equal(SubjectKind.Host, facts.SubjectKind);
        Assert.Equal("k10temp/Tctl", facts.Subject);
    }

    [Fact]
    public void A_server_scoped_threshold_is_subjected_to_the_server()
    {
        EventFacts facts = Classify(
            "host_threshold_breached",
            """{"Metric":"MemPct","Scope":"server","Ref":"cgroup","ServerId":"Ketchup"}""",
            producer: "kgsm-monitor");

        Assert.Equal(SubjectKind.Instance, facts.SubjectKind);
        Assert.Equal("Ketchup", facts.Subject);
    }

    [Fact]
    public void A_threshold_with_neither_a_server_nor_a_reference_falls_back_to_the_metric()
    {
        EventFacts facts = Classify(
            "host_threshold_cleared", """{"Metric":"DiskPct","ServerId":null}""", producer: "kgsm-monitor");

        Assert.Equal("DiskPct", facts.Subject);
    }

    [Fact]
    public void An_explicit_json_null_reads_the_same_as_an_absent_property()
    {
        // Several payloads carry an explicit null subject. Read as a value it would become the string
        // "null" or throw; the question being asked is only ever "did the payload name this".
        EventFacts facts = Classify("instance_started", """{"InstanceName":null}""");

        Assert.Equal(SubjectKind.Unknown, facts.SubjectKind);
        Assert.Equal(string.Empty, facts.Subject);
    }

    [Fact]
    public void A_payload_that_is_not_an_object_is_survived()
    {
        Assert.Equal(SubjectKind.Unknown, Classify("instance_started", "[]").SubjectKind);
        Assert.Equal(SubjectKind.Unknown, Classify("instance_started", "\"text\"").SubjectKind);
    }

    [Fact]
    public void This_leafs_own_decision_is_recorded_with_the_subject_it_names()
    {
        // Recorded, not dropped: a decision about starbound is genuinely a fact about starbound, and
        // filing it under nothing would leave a hole in that server's history for no gain. Safe
        // because no rule wakes on a reactor_* event — see RuleCatalogTests.
        EventFacts facts = EventClassifier.Classify(
            "reactor_decided",
            JsonDocument.Parse(
                """{"Rule":"give_up_backup","Subject":"starbound","SubjectKind":"instance"}""")
                .RootElement,
            "kgsm-reactor");

        Assert.Equal(EventClass.Reactor, facts.Class);
        Assert.Equal(SubjectKind.Instance, facts.SubjectKind);
        Assert.Equal("starbound", facts.Subject);
    }

    [Fact]
    public void A_decision_about_the_host_keeps_the_kind_it_was_written_with()
    {
        // The bot routes on an instance name and a host subject has no channel to follow, so this is
        // the field that decides whether a consumer can tell the two apart at all.
        EventFacts facts = EventClassifier.Classify(
            "reactor_decided",
            JsonDocument.Parse(
                """{"Rule":"threshold_stuck","Subject":"k10temp/Tctl","SubjectKind":"host"}""")
                .RootElement,
            "kgsm-reactor");

        Assert.Equal(SubjectKind.Host, facts.SubjectKind);
        Assert.Equal("k10temp/Tctl", facts.Subject);
    }

    [Fact]
    public void A_subject_kind_this_build_does_not_know_reads_as_unknown()
    {
        // A newer reactor's vocabulary reaching an older one. Unknown is the honest answer; mapping it
        // onto the nearest familiar kind would be a guess no reader could later distinguish from a
        // reading that was actually taken.
        EventFacts facts = EventClassifier.Classify(
            "reactor_decided",
            JsonDocument.Parse("""{"Subject":"something","SubjectKind":"cluster"}""").RootElement,
            "kgsm-reactor");

        Assert.Equal(SubjectKind.Unknown, facts.SubjectKind);
        Assert.Equal("something", facts.Subject);
    }

}
