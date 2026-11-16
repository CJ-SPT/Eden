using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class WormholeEnvironmentHandler(IWormholeEnvironments environments)
{
    private IReadOnlyList<WormholeSystemEffect> _current = [];

    internal IReadOnlyList<WormholeSystemEffect> Read(DockedPilot pilot)
    {
        var effects = pilot.Position is null ? Array.Empty<WormholeSystemEffect>() : environments.Effects(pilot.SystemId);
        // The pinned client replaces its effect dictionary only for a nonempty GetAllInfo payload.
        if (effects.Count > 0)
        {
            _current = effects;
        }
        return effects;
    }

    internal void Advance(PilotCallContext context)
    {
        var pilot = context.Pilot;
        var effects = pilot.Position is null ? Array.Empty<WormholeSystemEffect>() : environments.Effects(pilot.SystemId);
        if (ReferenceEquals(_current, effects))
        {
            return;
        }
        foreach (var stopped in _current.Except(effects))
        {
            context.Notifications.Add(new WormholeEffectStopped(stopped));
        }
        foreach (var started in effects.Except(_current))
        {
            context.Notifications.Add(new WormholeEffectStarted(started));
        }
        _current = effects;
    }
}

internal sealed record WormholeEffectStarted(WormholeSystemEffect Effect) : PilotNotification("OnSystemWideEffectStart")
{
    public override object?[] Arguments => [Effect.EffectId, Effect.SourceItemId, Effect.SourceTypeId];
}

internal sealed record WormholeEffectStopped(WormholeSystemEffect Effect) : PilotNotification("OnSystemWideEffectStop")
{
    public override object?[] Arguments => [Effect.EffectId, Effect.SourceItemId, Effect.SourceTypeId];
}

internal sealed record WormholeEffectsWire(IReadOnlyList<WormholeSystemEffect> Effects) : BlueModel
{
    public override object ToBlue() =>
        new BlueDictionary(
            Effects
                .GroupBy(e => new { e.SourceItemId, e.SourceTypeId })
                .Select(group =>
                    (
                        (object?)new object?[] { group.Key.SourceItemId, group.Key.SourceTypeId },
                        (object?)new BlueConstructor("__builtin__.set", [group.Select(e => (object?)(long)e.EffectId).ToList()])
                    )
                )
                .ToArray()
        );
}
