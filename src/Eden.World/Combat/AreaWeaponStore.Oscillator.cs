using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class AreaWeaponStore
{
    private void Oscillate(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        AreaWeaponFlight flight,
        int system,
        long now,
        Dictionary<long, SpacePosition> displacements
    )
    {
        // The pulse moves eligible ships together to a remote part of the same system.
        double azimuth = 2 * Math.PI * Random.Shared.NextDouble();
        double elevation = 2 * Random.Shared.NextDouble() - 1;
        double planar = Math.Sqrt(1 - elevation * elevation);
        double distance = (1 + Random.Shared.NextDouble() * 3) * WarpCapacitorRules.AstronomicalUnit;
        var offset = new SpacePosition(Math.Cos(azimuth) * planar, elevation, Math.Sin(azimuth) * planar) * distance;
        var field = flight.Module.Field!;
        foreach (
            var target in Targets(
                world,
                catalog,
                system,
                now,
                (position, radius) => position.DistanceTo(flight.Point) <= field.Radius + radius,
                shipsOnly: true
            )
        )
        {
            if (target.Id == source.ActiveShipId || displacements.ContainsKey(target.Id))
            {
                continue;
            }
            Aggression(world, catalog, source, source.ActiveShipId, flight.Module.TypeId, target, system, now, now);
            if (target.Attributes.GetValueOrDefault(DogmaAttributeId.IsCapitalSize) > 0)
            {
                var disruption =
                    world.Set<SavedLanceDisruption>().Find(target.Id) ?? world.Add(new SavedLanceDisruption { ShipId = target.Id }).Entity;
                disruption.OscillatorEndsAt = Math.Max(disruption.OscillatorEndsAt, now + DoomsdayFieldRules.OscillatorWarpLock);
                continue;
            }
            var destination = target.Position + offset;
            SqliteWorld.SetPosition(world, catalog, target.Id, destination, system, now);
            displacements[target.Id] = destination;
        }
    }
}
