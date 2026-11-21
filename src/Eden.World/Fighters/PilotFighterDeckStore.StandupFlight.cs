using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Fighters;

internal sealed partial class PilotFighterDeckStore
{
    public void Assign(long pilotId, long structureId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: [structureId]));
        foreach (var squadron in StandupSquadrons(world, structureId).Where(squadron => squadron.PilotId != pilotId))
        {
            squadron.PilotId = pilotId;
        }
        world.Commit();
    }

    public void Release(long pilotId, long structureId, long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: [structureId]));
        foreach (var squadron in StandupSquadrons(world, structureId).Where(squadron => squadron.PilotId == pilotId))
        {
            StartStandupReturn(squadron, structureId, now);
        }
        world.Commit();
    }

    public void Recall(long pilotId, long structureId, IReadOnlyList<long> squadrons, long now)
    {
        using var world = Live.Economy.Begin(new(pilots: [pilotId], ships: [structureId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var host = RequireStockDeck(world, catalog, pilotId, structureId, FighterDeckRules.FirstTube);
        var active = StandupSquadrons(world, structureId).ToDictionary(squadron => squadron.ItemId);
        if (
            !host.Standup
            || squadrons.Count == 0
            || squadrons.Distinct().Count() != squadrons.Count
            || squadrons.Any(id => !active.TryGetValue(id, out var squadron) || squadron.PilotId != pilotId)
        )
        {
            throw new InvalidDataException("Recall squadrons controlled by the current Upwell structure.");
        }
        foreach (long id in squadrons)
        {
            StartStandupReturn(active[id], structureId, now);
        }
        world.Commit();
    }

    public void AdvanceReturns(long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        var systems = live.Economy.Inspect(world =>
            world
                .Filter<WorldDrone>(row => row.Activity == DroneActivity.ReturningToBay)
                .AsEnumerable()
                .Where(row => row.SystemId is not null && world.Set<SavedStructure>().Find(row.ControllerShipId) is not null)
                .Select(row => row.SystemId!.Value)
                .Distinct()
                .ToArray()
        );
        if (systems.Length == 0)
        {
            return;
        }
        using var world = live.Economy.Begin(new(systems: systems));
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var row in world.Filter<WorldDrone>(drone => drone.Activity == DroneActivity.ReturningToBay).ToArray())
        {
            if (
                row.SystemId is not int system
                || !systems.Contains(system)
                || world.Set<SavedStructure>().Find(row.ControllerShipId) is not { } structure
                || world.Set<OwnedItem>().Find(row.ItemId) is not { } item
            )
            {
                continue;
            }
            if (structure.State is StructureState.Destroyed or StructureState.Unanchored)
            {
                world.Remove(item);
                continue;
            }
            var contact = PilotDroneStore.Contact(world, catalog, row, now);
            var target = new SpacePosition(structure.X, structure.Y, structure.Z);
            float radius = (float)(catalog.RequireItem(structure.TypeId).Radius ?? 0);
            if (contact.Position.DistanceTo(target) - radius - contact.Profile.Radius <= DroneRules.RecallRange)
            {
                item.LocationId = structure.Id;
                row.SystemId = null;
                row.ControllerShipId = 0;
                row.Activity = DroneActivity.Idle;
                row.TargetId = 0;
                continue;
            }
            double seconds = Math.Clamp((now - row.UpdatedAt) / (double)TimeSpan.TicksPerSecond, 0, 1);
            var moved = DroneRules.Move(contact, target, radius, false, 0, seconds);
            (row.X, row.Y, row.Z) = (moved.Position.X, moved.Position.Y, moved.Position.Z);
            (row.VelocityX, row.VelocityY, row.VelocityZ) = (moved.Velocity.X, moved.Velocity.Y, moved.Velocity.Z);
            row.SpeedLimit = moved.SpeedLimit;
            row.Orbiting = moved.Orbiting;
            row.UpdatedAt = now;
        }
        world.Commit();
    }

    private static IEnumerable<WorldDrone> StandupSquadrons(WorldAction world, long structureId) =>
        world.Filter<WorldDrone>(row => row.ControllerShipId == structureId && row.SystemId != null);

    private static void StartStandupReturn(WorldDrone row, long structureId, long now)
    {
        if (row.Activity != DroneActivity.ReturningToBay)
        {
            row.Activity = DroneActivity.ReturningToBay;
            row.TargetId = structureId;
            row.CommandedAt = now;
            row.Orbiting = false;
        }
    }
}
