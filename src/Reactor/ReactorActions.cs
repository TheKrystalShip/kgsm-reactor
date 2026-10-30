using TheKrystalShip.Kgsm.Reactor;
using TheKrystalShip.KGSM.ComponentConfig;

// What a person may do with this daemon, through the node's API: the API checks them before it relays
// to the socket and the daemon checks the relayed token again, so they are declared here, where they are
// performed.
[assembly: Action(ReactorActionIds.RulesRead, "See the reactor's rules, decisions and offers",
    DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ReactorActionIds.RulesWrite, "Change reactor rules, and answer what they offer",
    DeclaredEffect.Write, DeclaredScope.Node)]

namespace TheKrystalShip.Kgsm.Reactor
{
    /// <summary>The ids of the actions this daemon declares.</summary>
    internal static class ReactorActionIds
    {
        /// <summary>See the rules, decisions and offers.</summary>
        public const string RulesRead = "reactor:rules.read";

        /// <summary>Change rules and answer offers.</summary>
        public const string RulesWrite = "reactor:rules.write";
    }
}
