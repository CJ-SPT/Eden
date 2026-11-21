using Eden.Data.World.Characters;
using Eden.Data.World.Drones;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    private static void SetFighterBomb(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldDrone drone,
        DroneContact contact,
        DroneWork work,
        FighterBomb bomb,
        SpacePosition? point,
        int system,
        long now
    )
    {
        var cycle = (work.FighterSalvo ?? FighterSalvoCycle.Full(bomb.Charges, bomb.RearmMilliseconds)).Refresh(now);
        if (point is null)
        {
            StopFighterChargeCycle(drone, work, cycle, now);
            return;
        }
        RequireFighterCharge(drone, cycle, now);
        var aim = point - contact.Position;
        if (!double.IsFinite(aim.Length) || aim.Length <= 0)
        {
            throw new InvalidDataException("Aim the fighter bombs away from the squadron.");
        }
        if (CrimewatchPersistence.Security(catalog, system) > 0 || world.TetherProtected(pilot.ActiveShipId, now))
        {
            throw new InvalidDataException("Launch fighter bombs in null-security or wormhole space while untethered.");
        }
        cycle = cycle.Fire(now, bomb.DurationMilliseconds);
        AreaWeaponStore.LaunchFighterBomb(world, pilot, contact, bomb, aim.Unit, system, now, cycle.EndsAt);
        WriteWork(drone, work with { FighterSalvo = cycle });
    }
}
