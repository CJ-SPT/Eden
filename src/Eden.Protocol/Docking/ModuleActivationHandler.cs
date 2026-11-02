using Eden.Protocol.Combat;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// One selected-pilot owner for the activation RPCs; feature controllers retain
// their own cycle state and are also advanced by the session's clock.
[Injectable]
internal sealed class ModuleActivationHandler(MiningHandler mining, CombatHandler combat, ShieldBoostHandler shields, InventoryHandler inventory) : ISelectedPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate") || args.Length < 2) { return PilotCallRouter.Unsupported; }
        return BlueMarshal.Text(args[1]) switch
        {
            MiningDefinitions.EffectName => mining.Call(context, service, method, args, keywords),
            CombatDefinitions.CivilianWeaponEffect => combat.Call(context, service, method, args, keywords),
            ShieldBoostDefinitions.EffectName => shields.Call(context, service, method, args),
            InventoryDefinitions.OnlineEffectName => Online(context, method, args),
            _ => PilotCallRouter.Unsupported,
        };
    }

    private object? Online(PilotCallContext context, string method, object?[] args) => (method, args) switch
    {
        // Pinned Godma EffectWrapper uses these calls from the module menu,
        // including when the first operation is nested inside MachoBindObject.
        ("Deactivate", [long module, _]) => inventory.SetOnline(context, context.Pilot.ShipId, module, false),
        ("Activate", [long module, _, var target, long repeats]) when
            (target is null || Equals(target, context.Pilot.ShipId)) && repeats is >= 0 and <= int.MaxValue =>
            inventory.SetOnline(context, context.Pilot.ShipId, module, true),
        _ => PilotCallRouter.Unsupported,
    };
}
