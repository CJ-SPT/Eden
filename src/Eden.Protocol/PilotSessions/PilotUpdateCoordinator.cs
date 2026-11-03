using Eden.Gameplay.Docking;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Industry;
using Eden.Protocol.Mining;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotUpdateCoordinator(
    TargetingHandler targeting,
    MiningHandler mining,
    CombatHandler combat,
    ShieldBoostHandler shields,
    PropulsionHandler propulsion,
    IndustryHandler industry,
    NpcCombatHandler npcCombat,
    LocalRepairHandler repairs,
    HardenerHandler hardeners,
    CapacitorBoosterHandler capacitorBoosters,
    MissileHandler missiles,
    SupportModuleHandler support
)
{
    public bool IsMonitoring => industry.IsMonitoring;

    public void Enter(PilotCallContext context)
    {
        repairs.Enter(context);
        npcCombat.Enter(context);
    }

    public DockedPilot? Advance(PilotCallContext context)
    {
        targeting.Advance(context);
        capacitorBoosters.Advance(context);
        hardeners.Advance(context);
        support.Advance(context);
        mining.Advance(context);
        combat.Advance(context);
        missiles.Advance(context);
        shields.Advance(context);
        repairs.Advance(context);
        propulsion.Advance(context);
        industry.Advance(context);
        DockedPilot? beforeAttack = context.ActiveFlight?.Pilot;
        npcCombat.Advance(context);
        targeting.Advance(context);
        return beforeAttack;
    }
}
