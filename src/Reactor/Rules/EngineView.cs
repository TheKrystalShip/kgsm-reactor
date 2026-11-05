using TheKrystalShip.KGSM.Engine.Contracts.Blueprints;
using TheKrystalShip.KGSM.Engine.Contracts.Instances;
using TheKrystalShip.KGSM.Engine.Reading;

namespace TheKrystalShip.Kgsm.Reactor.Rules;

/// <summary>
/// What a rule reads from the engine about an instance and its game: the declared configuration and
/// the blueprint's declared needs.
/// </summary>
/// <remarks>Behind an interface so a rule's world can be built in a test without a data home.</remarks>
internal interface IEngineView
{
    /// <summary>The instance's configuration, or null when the engine knows no such instance or cannot read its config.</summary>
    InstanceConfig? Instance(string id);

    /// <summary>The named blueprint, or null when none exists or it does not validate.</summary>
    Blueprint? Blueprint(string name);
}

/// <summary><see cref="IEngineView"/> over the engine core, in-process.</summary>
internal sealed class EngineView(EngineReader engine) : IEngineView
{
    public InstanceConfig? Instance(string id) => engine.FindInstance(id)?.Config;

    public Blueprint? Blueprint(string name) => engine.ReadBlueprint(name)?.Blueprint;
}
