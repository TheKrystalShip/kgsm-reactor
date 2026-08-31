using TheKrystalShip.KGSM.ComponentConfig;

// What the Control Panel shows about this daemon, declared beside the configuration it describes.
// TheKrystalShip.KGSM.ComponentConfig reads this out of the built assembly and writes
// deploy/kgsm-reactor.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/reactor.json,
// where kgsm-api scans for it. The daemon itself never reads any of this.

[assembly: Leaf(
    id: "reactor",
    displayName: "Reactor",
    unit: "kgsm-reactor.service",
    role: "Watches every component's event journal, judges what it sees against a table of rules, and "
        + "records what it would do about it. Every rule observes and dispatches nothing, which is how "
        + "one earns the right to act.")]

[assembly: ConfigGroup("general", "General", 1)]
[assembly: ConfigGroup("wiring", "Connections", 2)]
[assembly: ConfigGroup("retention", "Observations", 3)]
[assembly: ConfigGroup("rules", "Rules", 4)]

// Lowest precedence first — the same order the daemon resolves them in.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-reactor/kgsm-reactor.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-reactor.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-reactor/kgsm-reactor.env")]

[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
