using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static long SceneObjectId(MissionProfile profile, long missionId, long objectId, int sceneryIndex)
    {
        for (int index = 0; index < profile.Definition.Containers.Length; index++)
        {
            if (profile.Definition.Containers[index].ObjectId == objectId)
            {
                return MissionContainerRules.Id(missionId, index);
            }
        }
        return checked(MissionRules.FirstObjectId + missionId * MissionRules.NpcsPerMission + sceneryIndex);
    }

    private static void SeedContainers(WorldDbContext world, CatalogDbContext catalog, SavedMission mission, MissionProfile profile)
    {
        for (int index = 0; index < profile.Definition.Containers.Length; index++)
        {
            var container = profile.Definition.Containers[index];
            InventoryPersistence.MergeOutput(
                world,
                catalog,
                mission.PilotId,
                MissionContainerRules.Id(mission.Id, index),
                (int)InventoryFlag.Cargo,
                container.TypeId,
                container.Quantity
            );
        }
    }

    private sealed record ContainerAccess(Pilot Pilot, StaticSpaceObject Container, int SystemId, SavedMissionStructure? Structure = null);

    private ContainerAccess AccessContainer(WorldDbContext world, CatalogDbContext catalog, long pilotId, long containerId)
    {
        if (!MissionContainerRules.IsContainer(containerId))
        {
            throw new InvalidDataException("This is not a mission container.");
        }
        long missionId = (containerId - MissionContainerRules.FirstId) / MissionRules.NpcsPerMission;
        int index = (int)((containerId - MissionContainerRules.FirstId) % MissionRules.NpcsPerMission);
        var mission = world.Set<SavedMission>().Find(missionId);
        if (mission is null || mission.PilotId != pilotId || mission.State != MissionState.Accepted || mission.ExpiresAt <= Now)
        {
            throw new InvalidDataException("The mission container is unavailable or belongs to another pilot.");
        }
        var pilot = ReadPilot(world, pilotId);
        var position = pilot.ActiveShip.Position;
        if (position is null || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != mission.SystemId)
        {
            throw new InvalidDataException("The mission container is unavailable or belongs to another pilot.");
        }
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        if (index >= profile.Definition.Containers.Length)
        {
            int structureIndex = index - profile.Definition.Containers.Length;
            if (structureIndex >= profile.Definition.Structures.Length || profile.Definition.Structures[structureIndex].Drop is null)
            {
                throw new InvalidDataException("This mission container is unavailable.");
            }
            long source = profile.Definition.Structures[structureIndex].ObjectId;
            var structure = world.Set<SavedMissionStructure>().SingleOrDefault(s => s.MissionId == missionId && s.ObjectId == source);
            if (structure?.DestroyedAt is null)
            {
                throw new InvalidDataException("Destroy the mission structure before recovering its cargo.");
            }
            var recovery = RecoveryContainer(catalog, mission, profile, structureIndex, structure.LootEmpty);
            MissionContainerRules.Validate(
                new(position.X, position.Y, position.Z),
                catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0,
                recovery,
                false
            );
            return new(pilot, recovery, mission.SystemId, structure);
        }
        var definition = profile.Definition.Containers[index];
        var room = profile.Rooms.Single(r => r.Objects.Any(o => o.Id == definition.ObjectId));
        var item = room.Objects.Single(o => o.Id == definition.ObjectId);
        var container = item with { Id = containerId, Position = new SpacePosition(mission.X, mission.Y, mission.Z) + item.Position };
        MissionContainerRules.Validate(
            new(position.X, position.Y, position.Z),
            catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0,
            container,
            definition.RequiresClear && !mission.ObjectiveComplete
        );
        return new(pilot, container, mission.SystemId);
    }

    public MissionContainerInventory Read(long pilotId, long containerId)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            var access = AccessContainer(world, catalog, pilotId, containerId);
            var container = access.Container;
            return new(
                new(
                    container.Id,
                    container.TypeId,
                    pilotId,
                    access.SystemId,
                    (int)InventoryFlag.AutoFit,
                    1,
                    container.GroupId,
                    container.CategoryId,
                    PilotInventoryStore.Volume(catalog, container.TypeId),
                    true
                ),
                PilotInventoryStore.List(world, catalog, pilotId, containerId)
            );
        }
    }

    public IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, InventoryMoveRequest request)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(database.CatalogPath);
            var access = AccessContainer(world, catalog, pilotId, request.SourceId);
            var changes = SpaceLootTransfer.Take(
                world,
                catalog,
                access.Pilot,
                shipId,
                request.SourceId,
                request.ItemIds,
                request.Quantity,
                (InventoryFlag)request.FlagId
            );
            world.SaveChanges();
            if (access.Structure is { } structure)
            {
                structure.LootEmpty = !world.Set<OwnedItem>().Any(i => i.LocationId == request.SourceId && i.Quantity > 0);
                world.SaveChanges();
            }
            transaction.Commit();
            return changes;
        }
    }
}
