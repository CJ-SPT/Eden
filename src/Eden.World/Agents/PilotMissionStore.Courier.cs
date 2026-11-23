using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    internal static IEnumerable<MissionProfile> StarterCourierProfiles(CatalogDbContext catalog, CatalogAgent agent, double room) =>
        ProfilesForAgent(catalog, agent, agent.Level, false)
            .Where(profile =>
                profile.Kind == MissionKind.Courier
                && profile.Definition.Chain is null
                && profile.CargoType > 0
                && profile.CargoQuantity > 0
                && profile.CargoVolume > 0
                && profile.CargoVolume * profile.CargoQuantity <= room + InventoryDefinitions.ResourceTolerance
            );

    internal int? RunStarterCourier(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot ship,
        CatalogAgent agent,
        int destination,
        long now,
        bool startNew = true
    )
    {
        if (ship.Position is not null || ship.InStructure || agent.StationId is not int source)
        {
            return null;
        }
        long id = ship.PilotId;
        int dock = ship.StationId;
        var pilot = world.Pilots.Find(id)!;
        var active = world
            .Filter<SavedMission>(mission => mission.PilotId == id)
            .SingleOrDefault(mission => mission.AgentId == agent.Id && mission.ClosedAt == null);
        if (active is not null && Expire(world, catalog, active, now))
        {
            active = null;
        }
        if (active is { State: MissionState.Accepted } delivery)
        {
            var profile = MissionCatalog.Find(catalog, delivery.MissionId);
            if (Cargo(world, pilot, delivery, profile).Sum(item => (long)item.Quantity) >= profile.CargoQuantity)
            {
                if (dock == delivery.DestinationStation)
                {
                    Act(world, catalog, pilot, agent, MissionAction.Complete, delivery.Id, now, []);
                    return startNew ? source : null;
                }
                return delivery.DestinationStation;
            }
            // A destroyed ship's cargo cannot complete an existing delivery.
            Act(world, catalog, pilot, agent, MissionAction.Quit, delivery.Id, now, []);
            return !startNew || dock == source ? null : source;
        }
        if (!startNew)
        {
            return null;
        }
        if (dock != source)
        {
            return source;
        }
        if (!Access(world, catalog, pilot, agent).Allowed)
        {
            return null;
        }
        int cargoFlag = (int)InventoryFlag.Cargo;
        double room =
            PilotInventoryStore.HoldCapacity(world, catalog, id, ship.ShipId, cargoFlag)
            - InventoryPersistence.UsedVolume(world, catalog, id, ship.ShipId, cargoFlag);
        if (active is null)
        {
            var choices = StarterCourierProfiles(catalog, agent, room).OrderBy(profile => profile.Definition.MissionId).ToArray();
            if (choices.Length == 0)
            {
                return null;
            }
            int completed = world
                .Filter<SavedMission>(mission => mission.PilotId == id)
                .Count(mission => mission.AgentId == agent.Id && mission.ClosedAt != null);
            active = Offer(world, catalog, pilot, agent, agent.Level, false, now, choices[completed % choices.Length], destination);
            world.SaveChanges();
        }
        var accepted = Act(world, catalog, pilot, agent, MissionAction.Accept, active.Id, now, []);
        var cargo = accepted.Items.Single().Item;
        PilotInventoryStore.Move(world, catalog, id, ship.ShipId, new([cargo.Id], dock, cargoFlag, accepted.Offer!.Cargo!.Quantity), now);
        return active.DestinationStation;
    }

    private static InventoryChange CreateCourierCargo(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int stationId,
        MissionProfile profile
    )
    {
        if (!profile.CargoSingleton)
        {
            return InventoryPersistence.MergeOutput(
                world,
                catalog,
                pilotId,
                stationId,
                (int)InventoryFlag.Hangar,
                profile.CargoType,
                profile.CargoQuantity
            );
        }
        // The admitted catalog singleton objectives contain one indivisible item.
        // Give each acceptance its own identity, even beside another item of this type.
        var item = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    LocationId = stationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = profile.CargoType,
                    Quantity = 1,
                    Singleton = true,
                }
            )
            .Entity;
        return new(PilotInventoryStore.Read(catalog, item), null);
    }
}
