using TheKrystalShip.KGSM.LeafConfig;

// What the Control Panel shows about this daemon, declared beside the configuration it describes.
// TheKrystalShip.KGSM.LeafConfig reads this out of the built assembly and writes
// deploy/kgsm-reactor.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/reactor.json,
// where kgsm-api scans for it. The daemon itself never reads any of this.

[assembly: Leaf(
    id: "reactor",
    displayName: "Reactor",
    unit: "kgsm-reactor.service",
    role: "Watches every component's event journal, judges what it sees against a table of rules, and "
        + "records what it would do about it. Every rule observes and dispatches nothing, which is how "
        + "one earns the right to act.")]

[assembly: LeafGroup("general", "General", 1)]
[assembly: LeafGroup("wiring", "Connections", 2)]
[assembly: LeafGroup("retention", "Observations", 3)]
[assembly: LeafGroup("rules", "Rules", 4)]

// Lowest precedence first — the same order the daemon resolves them in.
[assembly: LeafFloorSource("appsettings", "/opt/kgsm-reactor/kgsm-reactor.settings.json")]
[assembly: LeafFloorSource("systemd-unit", "/etc/kgsm-reactor/systemd/kgsm-reactor.service")]
[assembly: LeafFloorSource("env-file", "/etc/kgsm-reactor/kgsm-reactor.env")]

[assembly: LeafFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: LeafFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = LeafType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
