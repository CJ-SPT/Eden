using Eden.Gameplay.Docking;
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
    DeferredRefitPrimes refits
)
{
    internal void Replace(PilotCallContext context, DockedPilot capsule)
    {
        context.Flight.BeginShipLoss();
        mining.Cancel(context);
        weapons.Cancel(context);
        shields.Cancel(context);
        propulsion.Cancel(context);
        refits.Clear();
        // Wreck.Prepare needs the still-loaded hull for rotation/explosion timing.
        context.Flight.RefreshWrecks();
        context.ReplaceDestroyedShip(capsule);
    }
}
