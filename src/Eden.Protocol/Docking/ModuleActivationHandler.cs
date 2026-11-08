using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Defenses;
using Eden.Protocol.Information;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

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
    LocalRepairHandler repairs,
    HardenerHandler hardeners,
    CapacitorBoosterHandler capacitorBoosters,
    MissileHandler? missiles = null,
    SupportModuleHandler? support = null,
    TackleHandler? tackle = null,
    PlayerEwarHandler? electronicWarfare = null,
    RemoteRepairHandler? remoteRepairs = null,
    CapacitorWarfareHandler? capacitorWarfare = null,
    RemoteAssistanceHandler? remoteAssistance = null,
    SignatureSuppressionHandler? signatureSuppression = null,
    AssaultDamageControlHandler? assaultDamageControl = null,
    InformationHandler? information = null,
    SharedTargetLocks? targetLocks = null
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
        string effectName = BlueMarshal.Text(args[1]);
        var result = effectName switch
        {
            var name when InformationWire.Effect(name) is not null => information?.CallModule(context, method, args)
                ?? PilotCallResult.Unsupported,
            AssaultDamageControlHandler.EffectName => assaultDamageControl?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            SignatureSuppressionHandler.EffectName => signatureSuppression?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            MiningWireDefinitions.EffectName => mining.Call(context, service, method, args, keywords),
            CombatWireDefinitions.CivilianWeaponEffect => combat.Call(context, service, method, args, keywords),
            CombatWireDefinitions.AmmoWeaponEffect => combat.Call(context, service, method, args, keywords),
            MissileHandler.EffectName => missiles?.Call(context, service, method, args) ?? PilotCallResult.Unsupported,
            ShieldBoostWireDefinitions.EffectName or ShieldBoostWireDefinitions.AncillaryEffectName => shields.Call(
                context,
                service,
                method,
                args
            ),
            ArmorRepairWireDefinitions.EffectName
            or LocalRepairWireDefinitions.HullEffectName
            or LocalRepairWireDefinitions.AncillaryEffectName => repairs.Call(context, service, method, args),
            CapacitorBoosterHandler.EffectName => capacitorBoosters.Call(context, service, method, args),
            HardenerWireDefinitions.ShieldEffectName or HardenerWireDefinitions.ArmorEffectName => hardeners.Call(
                context,
                service,
                method,
                args
            ),
            PropulsionWireDefinitions.EffectName or PropulsionWireDefinitions.MicrowarpdriveEffectName => propulsion.Call(
                context,
                service,
                method,
                args
            ),
            var name when SupportModuleWire.Effect(name) is not null => support?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            var name when TackleWire.Effect(name) is not null => tackle?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            var name when PlayerEwarWire.Effect(name) is not null => electronicWarfare?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            var name when RemoteRepairWire.Effect(name) is not null => remoteRepairs?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            var name when CapacitorWire.Effect(name) is not null => capacitorWarfare?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            var name when RemoteAssistanceWire.Effect(name) is not null => remoteAssistance?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            InventoryWireDefinitions.OnlineEffectName => Online(context, method, args),
            _ => PilotCallResult.Unsupported,
        };
        bool offensive =
            effectName is CombatWireDefinitions.CivilianWeaponEffect or CombatWireDefinitions.AmmoWeaponEffect or MissileHandler.EffectName
            || TackleWire.Effect(effectName) is DogmaEffect.StasisWebifier or DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler
            || PlayerEwarWire.Effect(effectName) is not null
            || CapacitorWire.Effect(effectName) is DogmaEffect.EnergyNeutralizer or DogmaEffect.EnergyNosferatu;
        if (method == "Activate" && result is PilotCallResult.Success && offensive && args is [_, _, long target, _])
        {
            targetLocks?.Reveal(context.Pilot.ShipId, target);
        }
        return result;
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
