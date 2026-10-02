using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.ComponentConfig;

// What this daemon does to the engine as its own service account. A rule reading the world reads as
// this account alone; a rule acting, and an offer somebody confirms, act as this account AND the person
// behind it — the rule's author or the one who said yes — each needing the action at the server.

[assembly: Requires(KgsmActions.ServerRead, DeclaredScope.Instance,
    "Read a server's run state and supervision, which every rule is judged against")]
[assembly: Requires(KgsmActions.LibraryRead, DeclaredScope.Node,
    "Read what a server's blueprint declares it needs, to judge its footprint against")]
[assembly: Requires(KgsmActions.ServerBackupsRead, DeclaredScope.Instance,
    "Find the archive a backup or a rollback names")]
[assembly: Requires(KgsmActions.ServerBackupsCreate, DeclaredScope.Instance,
    "Archive the state a server was left in when a rule decides to keep it")]
[assembly: Requires(KgsmActions.ServerBackupsRestore, DeclaredScope.Instance,
    "Roll a server back to the archive taken before an update, once somebody confirms the offer")]

// What a person may do with this daemon, through the node's API: the API checks them before it relays
// to the socket, so they are declared here, where they are performed, and checked there.
[assembly: Action("reactor:rules.read", "See the reactor's rules, decisions and offers",
    DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action("reactor:rules.write", "Change reactor rules, and answer what they offer",
    DeclaredEffect.Write, DeclaredScope.Node)]
