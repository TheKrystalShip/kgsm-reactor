using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.ComponentConfig;

// The engine and watchdog operations this daemon performs on its own host, as itself. A rule reading
// the world reads; a rule acting, and an offer somebody confirms, perform with this daemon's authority
// after the save-time or confirm-time check of the person behind them.

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
