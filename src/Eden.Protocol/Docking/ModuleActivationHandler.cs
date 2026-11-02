using Eden.Protocol.Combat;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// One selected-pilot owner for the activation RPCs; feature controllers retain
// their own cycle state and are also advanced by the session's clock.
[Injectable]
internal sealed class ModuleActivationHandler(MiningHandler mining, CombatHandler combat) : ISelectedPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate") || args.Length < 2) { return PilotCallRouter.Unsupported; }
        return BlueMarshal.Text(args[1]) switch
        {
            MiningDefinitions.EffectName => mining.Call(context, service, method, args, keywords),
            CombatDefinitions.CivilianWeaponEffect => combat.Call(context, service, method, args, keywords),
            _ => PilotCallRouter.Unsupported,
        };
    }
}
