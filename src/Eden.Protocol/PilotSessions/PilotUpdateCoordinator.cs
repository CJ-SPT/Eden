using Eden.Gameplay.Combat;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Industry;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotUpdateCoordinator(
    TargetingHandler targeting,
    MiningHandler mining,
    CombatHandler combat,
    ShieldBoostHandler shields,
    PropulsionHandler propulsion,
    IndustryHandler industry,
    NpcCombatHandler npcCombat
)
{
    public bool IsMonitoring => industry.IsMonitoring;

    public void Enter(PilotCallContext context) => npcCombat.Enter(context);

    public DockedPilot? Advance(PilotCallContext context)
    {
        targeting.Advance(context);
        mining.Advance(context);
        combat.Advance(context);
        shields.Advance(context);
        propulsion.Advance(context);
        industry.Advance(context);
        DockedPilot? beforeAttack = context.ActiveFlight?.Pilot;
        npcCombat.Advance(context);
        targeting.Advance(context);
        return beforeAttack;
    }
}
