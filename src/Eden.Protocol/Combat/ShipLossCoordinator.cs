using Eden.Gameplay.Docking;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;

namespace Eden.Protocol.Combat;

[Injectable]
internal sealed class ShipLossCoordinator(
    MiningHandler mining,
    CombatHandler weapons,
    ShieldBoostHandler shields,
    PropulsionHandler propulsion,
    DeferredRefitPrimes refits,
    LocalRepairHandler repairs,
    HardenerHandler hardeners,
    CapacitorBoosterHandler capacitorBoosters,
    MissileHandler? missiles = null,
    SupportModuleHandler? support = null
)
{
    internal void Replace(PilotCallContext context, DockedPilot capsule)
    {
        context.Flight.BeginShipLoss();
        mining.Cancel(context);
        weapons.Cancel(context);
        missiles?.Cancel(context);
        shields.Cancel(context);
        repairs.Cancel(context);
        hardeners.Cancel(context);
        support?.Cancel(context);
        capacitorBoosters.Cancel(context);
        propulsion.Cancel(context);
        refits.Clear();
        // Wreck.Prepare needs the still-loaded hull for rotation/explosion timing.
        context.Flight.RefreshWrecks();
        context.ReplaceDestroyedShip(capsule);
    }
}
