using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
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

    private static void SeedContainers(WorldAction world, CatalogDbContext catalog, SavedMission mission, MissionProfile profile)
    {
        for (int index = 0; index < profile.Definition.Containers.Length; index++)
        {
            var container = profile.Definition.Containers[index];
            if (container.DepositTypeId > 0)
            {
                continue;
            }
            long id = MissionContainerRules.Id(mission.Id, index);
            if (container.TypeId > 0)
            {
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    mission.PilotId,
                    id,
                    (int)InventoryFlag.Cargo,
                    container.TypeId,
                    container.Quantity
                );
            }
            foreach (var item in container.AdditionalItems)
            {
                InventoryPersistence.MergeOutput(world, catalog, mission.PilotId, id, (int)InventoryFlag.Cargo, item.TypeId, item.Quantity);
            }
            if (container.Analyzer is not null)
            {
                world.Add(
                    new SavedHackingContainer
                    {
                        Id = id,
                        SiteId = MissionRules.FirstSiteId + mission.Id,
                        Index = index,
                        OwnerId = mission.PilotId,
                        LootSeed = System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
                    }
                );
            }
        }
    }

    private sealed record ContainerAccess(Pilot Pilot, StaticSpaceObject Container, int SystemId, SavedMissionStructure? Structure = null)
    {
        internal MissionContainerDefinition? Definition { get; init; }
        internal SavedMission? Mission { get; init; }
        internal SavedMission? Objective { get; init; }
        internal int ObjectiveType { get; init; }
        internal ExplorationKind? Analyzer { get; init; }
    }

    private static ContainerAccess AccessContainer(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long containerId,
        long now,
        double? hackRange = null
    )
    {
        if (!MissionContainerRules.IsContainer(containerId))
        {
            throw new InvalidDataException("This is not a mission container.");
        }
        long missionId = (containerId - MissionContainerRules.FirstId) / MissionRules.NpcsPerMission;
        int index = (int)((containerId - MissionContainerRules.FirstId) % MissionRules.NpcsPerMission);
        var mission = world.Set<SavedMission>().Find(missionId);
        if (mission is null || mission.PilotId != pilotId || mission.State != MissionState.Accepted || mission.ExpiresAt <= now)
        {
            throw new InvalidDataException("The mission container is unavailable or belongs to another pilot.");
        }
        var pilot = ResidentPilot(world, pilotId);
        var position = pilot.ActiveShip.Position;
        if (position is null || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != mission.SystemId)
        {
            throw new InvalidDataException("The mission container is unavailable or belongs to another pilot.");
        }
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        if (
            profile.Definition.Scan is MissionScanKind.Data or MissionScanKind.Relic or MissionScanKind.Gas
            && world.Set<SavedResolvedSignature>().Find(pilotId, MissionRules.FirstSiteId + mission.Id)?.SystemId != mission.SystemId
        )
        {
            throw new InvalidDataException("Resolve this training signature to 100% before opening or hacking its containers.");
        }
        if (index >= profile.Definition.Containers.Length)
        {
            int structureIndex = index - profile.Definition.Containers.Length;
            if (structureIndex >= profile.Definition.Structures.Length || profile.Definition.Structures[structureIndex].Drop is null)
            {
                throw new InvalidDataException("This mission container is unavailable.");
            }
            long source = profile.Definition.Structures[structureIndex].ObjectId;
            var structure = world.Filter<SavedMissionStructure>(s => s.MissionId == missionId && s.ObjectId == source).SingleOrDefault();
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
        double shipRadius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
        bool guarded = definition.RequiresClear && !mission.ObjectiveComplete;
        if (hackRange is double range)
        {
            if (definition.RequiresMiningDepletion && MissionDeposits(world.Set<WorldAsteroid>(), mission.Id).Any(a => a.Quantity > 0))
            {
                throw new InvalidDataException("Mine the mission deposits before using the analyzer here.");
            }
            if (definition.CompletesOnHack && mission.ObjectiveComplete)
            {
                throw new InvalidDataException("This mission's hacking objective is already complete.");
            }
            if (
                guarded
                || definition.Analyzer is null
                || new SpacePosition(position.X, position.Y, position.Z).DistanceTo(container.Position) - shipRadius - container.Radius
                    > range
            )
            {
                throw new InvalidDataException("Clear the mission container's guards and move within analyzer range before hacking.");
            }
        }
        else
        {
            MissionContainerRules.Validate(new(position.X, position.Y, position.Z), shipRadius, container, guarded);
            if (definition.Analyzer is not null && world.Set<SavedHackingContainer>().Find(containerId)?.Hacked != true)
            {
                throw new InvalidDataException("Hack this training container before opening it.");
            }
        }
        return new(pilot, container, mission.SystemId)
        {
            Definition = definition,
            Mission = mission,
            Analyzer = definition.Analyzer,
            Objective =
                profile.Definition.Scan is not null
                || definition.Analyzer is not null
                || definition.CompletesOnTake
                || profile.Definition.CareerRescue
                    ? mission
                    : null,
            ObjectiveType = profile.CargoType,
        };
    }

    internal static (StaticSpaceObject Object, ExplorationKind Analyzer, int System) CareerHackAccess(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long containerId,
        double range,
        long now
    )
    {
        var access = AccessContainer(world, catalog, pilotId, containerId, now, range);
        return (
            access.Container,
            access.Analyzer ?? throw new InvalidDataException("This mission container has no analyzer objective."),
            access.SystemId
        );
    }

    private static StaticSpaceObject CareerContainerVisual(WorldAction world, StaticSpaceObject body)
    {
        if (!MissionContainerRules.IsContainer(body.Id) || world.Set<SavedHackingContainer>().Find(body.Id) is not { } state)
        {
            return body;
        }
        return body with { HackingState = state.Hacked ? 2 : 0, Container = new(state.OwnerId, state.Empty) };
    }

    private static void RetireMissionExploration(WorldAction world, SavedMission mission)
    {
        if ((CareerMissionCatalog.Find(mission.MissionId) ?? EpicArcCatalog.Find(mission.MissionId)) is not { } definition)
        {
            return;
        }
        long site = MissionRules.FirstSiteId + mission.Id;
        if (definition.Scan is not null && world.Set<SavedResolvedSignature>().Find(mission.PilotId, site) is { } resolved)
        {
            world.Remove(resolved);
        }
        if (!definition.Containers.Any(container => container.Analyzer is not null))
        {
            return;
        }
        var containers = world.Filter<SavedHackingContainer>(container => container.SiteId == site).ToArray();
        if (world.Set<SavedHackingAttempt>().Find(mission.PilotId) is { } attempt && containers.Any(c => c.Id == attempt.ContainerId))
        {
            PirateExplorationStore.ReleaseModule(world, attempt);
            world.Remove(attempt);
        }
        world.RemoveRange(containers);
    }

    public MissionContainerInventory Read(long pilotId, long containerId)
    {
        return LiveWorld.Execute(database.Path, () => ReadOwned(pilotId, containerId), nameof(Read));
    }

    private MissionContainerInventory ReadOwned(long pilotId, long containerId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect<MissionContainerInventory>(
            database,
            world =>
            {
                var access = AccessContainer(world, catalog, pilotId, containerId, Now);
                var container = access.Container;
                var definition = access.Definition;
                var contents = PilotInventoryStore.List(world, catalog, pilotId, containerId);
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
                    definition is { DepositTypeId: > 0 }
                        ? contents.Where(item => item.TypeId != definition.DepositTypeId).ToArray()
                        : contents
                )
                {
                    DepositCapacity = definition is { DepositTypeId: > 0 }
                        ? Math.Max(
                            PilotInventoryStore.Volume(catalog, definition.DepositTypeId) * definition.DepositQuantity,
                            contents.Where(item => item.TypeId != definition.DepositTypeId).Sum(item => item.Quantity * item.Volume)
                        )
                        : null,
                };
            }
        );
    }

    public IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, InventoryMoveRequest request)
    {
        return LiveWorld.Execute(database.Path, () => TakeOwned(pilotId, shipId, request), nameof(Take));
    }

    private IReadOnlyList<InventoryChange> TakeOwned(long pilotId, long shipId, InventoryMoveRequest request)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
            var world = transaction.World;
            using CatalogDbContext catalog = new(database.CatalogPath);
            var access = AccessContainer(world, catalog, pilotId, request.SourceId, Now);
            if (
                access.Definition is { DepositTypeId: > 0 } delivery
                && request.ItemIds.Any(id => world.Set<OwnedItem>().Find(id)?.TypeId == delivery.DepositTypeId)
            )
            {
                throw new InvalidDataException("Delivered mission cargo cannot be retrieved.");
            }
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
            if (
                access.Objective is { } mission
                && changes.Any(change => change.Item.TypeId == access.ObjectiveType)
                && !world.Filter<OwnedItem>(item => item.LocationId == request.SourceId && item.TypeId == access.ObjectiveType).Any()
            )
            {
                mission.ObjectiveComplete = true;
            }
            if (access.Analyzer is not null && world.Set<SavedHackingContainer>().Find(request.SourceId) is { } hacking)
            {
                hacking.Empty = !world.Filter<OwnedItem>(item => item.LocationId == request.SourceId && item.Quantity > 0).Any();
            }
            if (access.Structure is { } structure)
            {
                structure.LootEmpty = !world.Filter<OwnedItem>(i => i.LocationId == request.SourceId && i.Quantity > 0).Any();
                world.SaveChanges();
            }
            transaction.Commit();
            return changes;
        }
    }
}
