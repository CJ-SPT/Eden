using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// One selected-pilot owner for the activation RPCs; feature controllers retain
// their own cycle state and are also advanced by the session's clock.
[Injectable]
internal sealed class ModuleActivationHandler(
    MiningHandler mining,
    CombatHandler combat,
    ShieldBoostHandler shields,
    FittingOperations inventory,
    PropulsionHandler propulsion,
    ArmorRepairHandler armor
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate") || args.Length < 2)
        {
            return PilotCallResult.Unsupported;
        }
        return BlueMarshal.Text(args[1]) switch
        {
            MiningWireDefinitions.EffectName => mining.Call(context, service, method, args, keywords),
            CombatWireDefinitions.CivilianWeaponEffect => combat.Call(context, service, method, args, keywords),
            CombatWireDefinitions.HybridWeaponEffect => combat.Call(context, service, method, args, keywords),
            ShieldBoostWireDefinitions.EffectName => shields.Call(context, service, method, args),
            ArmorRepairWireDefinitions.EffectName => armor.Call(context, service, method, args),
            PropulsionWireDefinitions.EffectName => propulsion.Call(context, service, method, args),
            InventoryWireDefinitions.OnlineEffectName => Online(context, method, args),
            _ => PilotCallResult.Unsupported,
        };
    }

    private PilotCallResult Online(PilotCallContext context, string method, object?[] args) =>
        (method, args) switch
        {
            // Pinned Godma EffectWrapper uses these calls from the module menu,
            // including when the first operation is nested inside MachoBindObject.
            ("Deactivate", [long module, _]) => inventory.SetOnline(context, context.Pilot.ShipId, module, false),
            ("Activate", [long module, _, var target, long repeats])
                when (target is null || Equals(target, context.Pilot.ShipId)) && repeats is >= 0 and <= int.MaxValue => inventory.SetOnline(
                context,
                context.Pilot.ShipId,
                module,
                true
            ),
            _ => PilotCallResult.Unsupported,
        };
}
