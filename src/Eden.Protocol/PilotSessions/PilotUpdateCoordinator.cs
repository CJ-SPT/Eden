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
    SupportModuleHandler support,
    TackleHandler tackle,
    DeferredRefitPrimes refits
)
{
    public bool IsMonitoring => industry.IsMonitoring;

    public void Enter(PilotCallContext context)
    {
        repairs.Enter(context);
        npcCombat.Enter(context);
    }

    public void Leave(PilotCallContext context)
    {
        context.Flight.Checkpoint();
        mining.Cancel(context);
        combat.Cancel(context);
        missiles.Cancel(context);
        shields.Cancel(context);
        repairs.Cancel(context);
        hardeners.Cancel(context);
        capacitorBoosters.Cancel(context);
        support.Cancel(context);
        tackle.Leave(context);
        propulsion.Cancel(context);
        targeting.Reset();
        npcCombat.Leave();
        industry.Reset();
        refits.Clear();
    }

    public DockedPilot? Advance(PilotCallContext context)
    {
        targeting.Advance(context);
        capacitorBoosters.Advance(context);
        hardeners.Advance(context);
        support.Advance(context);
        tackle.Advance(context);
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
