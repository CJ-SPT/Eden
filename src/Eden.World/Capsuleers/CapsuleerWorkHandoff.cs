using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal static class CapsuleerWorkHandoff
{
    // Founder rows use the pinned solar-system IDs and negative slot IDs. Alternate workers use the pilot ID here.
    private const int AlternateWorkerIdBase = 1_000_000_000;

    internal static void ToMining(CapsuleerOperation operation, EconomyTransaction world, DockedPilot pilot, Ship ship, long ticks)
    {
        long id = pilot.PilotId;
        var rows = world.Set<SavedCapsuleerCommunity>();
        var row = rows.SingleOrDefault(row => row.PilotId == id);
        if (row is null)
        {
            var agent = world.Set<SavedCapsuleerAgent>().Find(id)!;
            row = world
                .Add(
                    new SavedCapsuleerCommunity
                    {
                        Id = checked(AlternateWorkerIdBase + (int)id),
                        PilotId = id,
                        Career = CapsuleerCareer.Mining,
                        Seed = unchecked((int)id),
                        HomeStationId = pilot.StationId,
                        FounderShipId = agent.FounderShipId,
                    }
                )
                .Entity;
        }
        var organization = CapsuleerCorporations.Organization(world, id);
        CapsuleerCorporations.SaveOrganization(world, id, organization with { ActiveWorker = CapsuleerCareer.Mining });
        operation.IncomingMiner = new(
            id,
            new MinerProgress(
                MinerActivity.Docked,
                0,
                0,
                0,
                0,
                0,
                0,
                new NavigationState().Capture(),
                ship.Id,
                ActiveTicks: ticks,
                DueTicks: NextTurn(ticks),
                ClockOffsetTicks: ticks
            )
            {
                CareerPlan = MinerCheckpoint.Read(row.Progress)?.CareerPlan,
            }
        );
    }

    internal static void ToFlight(CapsuleerOperation operation, EconomyTransaction world, DockedPilot pilot, Ship ship, long ticks)
    {
        long id = pilot.PilotId;
        var rows = world.Set<SavedCapsuleerHauler>();
        var row = rows.SingleOrDefault(row => row.PilotId == id);
        if (row is null)
        {
            var agent = world.Set<SavedCapsuleerAgent>().Find(id)!;
            row = world
                .Add(
                    new SavedCapsuleerHauler
                    {
                        Id = checked(AlternateWorkerIdBase + (int)id),
                        PilotId = id,
                        SourceStationId = pilot.StationId,
                        DestinationStationId = pilot.StationId,
                        FounderShipId = agent.FounderShipId,
                        RouteSystems = [pilot.SystemId],
                    }
                )
                .Entity;
        }
        var organization = CapsuleerCorporations.Organization(world, id);
        CapsuleerCorporations.SaveOrganization(world, id, organization with { ActiveWorker = CapsuleerCareer.Hauling });
        operation.IncomingHauler = new(
            id,
            new HaulerProgress(
                HaulerActivity.Docked,
                0,
                0,
                ship.Id,
                pilot.StationId,
                0,
                new NavigationState().Capture(),
                ActiveTicks: ticks,
                DueTicks: NextTurn(ticks),
                ClockOffsetTicks: ticks
            )
            {
                CareerPlan = HaulerCheckpoint.Read(row.Progress)?.CareerPlan,
            }
        );
    }

    private static long NextTurn(long ticks) => checked(ticks + TimeSpan.TicksPerSecond);
}
