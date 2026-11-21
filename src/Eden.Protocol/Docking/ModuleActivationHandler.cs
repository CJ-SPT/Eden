using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Defenses;
using Eden.Protocol.Fleets;
using Eden.Protocol.Flight;
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
    SharedTargetLocks? targetLocks = null,
    Eden.Protocol.Exploration.ProbeScanningHandler? probes = null,
    Eden.Protocol.Exploration.HackingHandler? hacking = null,
    SpatialModuleHandler? spatial = null,
    SmartbombHandler? smartbombs = null,
    CommandBurstHandler? commandBursts = null,
    InterdictionHandler? interdiction = null,
    AreaWeaponHandler? areaWeapons = null,
    Eden.Protocol.Sovereignty.EntosisHandler? entosis = null,
    CapitalTravelHandler? capital = null
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
        if (effectName == CapitalTravelHandler.ProtectionEffectName)
        {
            return keywords is { Count: > 0 }
                ? new PilotCallResult.Failure("Unexpected P.A.N.I.C. activation arguments.")
                : capital?.ActivateProtection(context, method, args) ?? PilotCallResult.Unsupported;
        }
        if (effectName == CapitalTravelHandler.CompressionEffectName)
        {
            return keywords is { Count: > 0 }
                ? new PilotCallResult.Failure("Unexpected compression activation arguments.")
                : capital?.ActivateCompression(context, method, args) ?? PilotCallResult.Unsupported;
        }
        if (CapitalTravelHandler.ModeEffect(effectName) is { } mode)
        {
            return keywords is { Count: > 0 }
                ? new PilotCallResult.Failure("Unexpected capital mode arguments.")
                : capital?.ActivateMode(context, method, args, mode) ?? PilotCallResult.Unsupported;
        }
        if (
            effectName
            is CapitalTravelHandler.CynoEffectName
                or CapitalTravelHandler.PortalEffectName
                or CapitalTravelHandler.TitanPortalEffectName
        )
        {
            return keywords is { Count: > 0 }
                ? new PilotCallResult.Failure("Unexpected cynosural activation arguments.")
                : capital?.Activate(context, method, args) ?? PilotCallResult.Unsupported;
        }
        if (
            method == "Activate"
            && (
                SpatialModuleHandler.Handles(effectName)
                || effectName is PropulsionWireDefinitions.EffectName or PropulsionWireDefinitions.MicrowarpdriveEffectName
            )
            && context.Flight.CapitalModuleActive
            && !(effectName == SpatialModuleHandler.MicroJumpEffect && context.Flight.MobileSiege)
        )
        {
            return new PilotCallResult.Failure("The active capital module prevents navigation modules from activating.");
        }
        if (method == "Activate" && args is [long module, _, _, long])
        {
            context.Flight.PrepareGateCloakModule(module);
        }
        if (effectName == Eden.Protocol.Sovereignty.EntosisHandler.EffectName && keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected Entosis activation arguments.");
        }
        bool offensive =
            CombatWireDefinitions.Handles(effectName)
            || effectName == MissileHandler.EffectName
            || InterdictionHandler.Effect(effectName)
                is DogmaEffect.WarpDisruptSphere
                    or DogmaEffect.FocusedWarpDisruption
                    or DogmaEffect.FocusedWarpScrambling
            || effectName == AreaWeaponHandler.BurstEffect
            || DoomsdayRules.Effect(effectName) is not null
            || effectName == SmartbombHandler.EffectName
            || TackleWire.Effect(effectName) is DogmaEffect.StasisWebifier or DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler
            || PlayerEwarWire.Effect(effectName) is not null
            || CapacitorWire.Effect(effectName) is DogmaEffect.EnergyNeutralizer or DogmaEffect.EnergyNosferatu;
        if (
            method == "Activate"
            && (offensive || SpatialModuleHandler.Handles(effectName) || effectName == Eden.Protocol.Sovereignty.EntosisHandler.EffectName)
            && context.Flight.IndustrialProtectionActive
        )
        {
            return new PilotCallResult.Failure("P.A.N.I.C. prevents using offensive, cloak and travel modules.");
        }
        // Nullification is explicitly permitted through an active cloak by the pinned effect.
        if (
            method == "Activate"
            && effectName != InterdictionHandler.NullifierEffectName
            && !SpatialModuleHandler.Handles(effectName)
            && spatial?.BlocksModule(context, offensive) == true
        )
        {
            return new PilotCallResult.Failure("The cloak prevents activating this module.");
        }
        if (effectName == MissileHandler.EffectName && interdiction?.CallLauncher(context, method, args) is { } interdictionResult)
        {
            return interdictionResult;
        }
        if (effectName == MissileHandler.EffectName && areaWeapons?.CallLauncher(context, method, args) is { } areaResult)
        {
            return areaResult;
        }
        if (effectName == MissileHandler.EffectName && probes?.CallModule(context, method, args) is { } probeResult)
        {
            return probeResult;
        }
        var result = effectName switch
        {
            Eden.Protocol.Sovereignty.EntosisHandler.EffectName => entosis?.Call(context, method, args) ?? PilotCallResult.Unsupported,
            var name when SpatialModuleHandler.Handles(name) => spatial?.Call(context, method, args) ?? PilotCallResult.Unsupported,
            Eden.Protocol.Exploration.HackingHandler.EffectName => hacking?.CallModule(context, method, args)
                ?? PilotCallResult.Unsupported,
            var name when InformationWire.Effect(name) is not null => information?.CallModule(context, method, args)
                ?? PilotCallResult.Unsupported,
            AssaultDamageControlHandler.EffectName or AssaultDamageControlHandler.BreachControlEffect => assaultDamageControl?.Call(
                context,
                service,
                method,
                args
            ) ?? PilotCallResult.Unsupported,
            SignatureSuppressionHandler.EffectName => signatureSuppression?.Call(context, service, method, args)
                ?? PilotCallResult.Unsupported,
            MiningWireDefinitions.EffectName or MiningWireDefinitions.GasEffectName => mining.Call(
                context,
                service,
                method,
                args,
                keywords
            ),
            var name when CombatWireDefinitions.Handles(name) => combat.Call(context, service, method, args, keywords),
            var name when CommandBurstHandler.Effect(name) is { } burst => commandBursts?.Call(context, method, burst, args)
                ?? PilotCallResult.Unsupported,
            var name when InterdictionHandler.Effect(name) is { } field => interdiction?.Call(context, method, field, args)
                ?? PilotCallResult.Unsupported,
            var name when DoomsdayRules.Effect(name) is { } doomsday => keywords is { Count: > 0 }
                ? new PilotCallResult.Failure("Unexpected doomsday arguments.")
                : areaWeapons?.CallModule(context, method, args, doomsday) ?? PilotCallResult.Unsupported,
            "doomsdayBeamDOT" or "doomsdaySlash" or "doomsdayConeDOT" or "debuffLance" => method == "Deactivate"
                ? new PilotCallResult.Failure("A paid area doomsday cannot be stopped early.")
                : new PilotCallResult.Failure("Aim the area doomsday from the tactical overlay."),
            AreaWeaponHandler.BurstEffect => areaWeapons?.CallModule(context, method, args, DogmaEffect.BurstJammer)
                ?? PilotCallResult.Unsupported,
            SmartbombHandler.EffectName => smartbombs?.Call(context, method, args) ?? PilotCallResult.Unsupported,
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
            ("Deactivate", [long module, _]) => inventory.SetOnline(
                context,
                context.ControlledStructureId ?? context.Pilot.ShipId,
                module,
                false
            ),
            ("Activate", [long module, _, var target, long repeats])
                when (target is null || Equals(target, context.ControlledStructureId ?? context.Pilot.ShipId))
                    && repeats is >= 0 and <= int.MaxValue => inventory.SetOnline(
                context,
                context.ControlledStructureId ?? context.Pilot.ShipId,
                module,
                true
            ),
            _ => PilotCallResult.Unsupported,
        };
}
