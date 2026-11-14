using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerPopulationStore
{
    internal static void Reconcile(WorldAction world, CatalogDbContext catalog)
    {
        if (world.Set<SavedCapsuleerCommunity>().Find(1) is not { PilotId: long pilot } miner)
        {
            return;
        }
        int system = catalog.RequireStation(miner.HomeStationId).SolarSystemId;
        if (world.Set<SavedCapsuleerPopulation>().Find(system) is null)
        {
            world.Add(
                new SavedCapsuleerPopulation
                {
                    SystemId = system,
                    HomeStationId = miner.HomeStationId,
                    Seed = miner.Seed,
                    Version = PopulationVersion,
                }
            );
        }
        AddAgent(world, new(pilot, system, miner.Career, miner.FounderShipId, miner.FounderModuleId, miner.StartingCents));
        if (world.Set<SavedCapsuleerHauler>().Find(1) is { } hauler)
        {
            AddAgent(world, new(hauler.PilotId, system, CapsuleerCareer.Hauling, hauler.FounderShipId, 0, hauler.StartingCents));
        }
    }

    internal static void ScheduleFounders(WorldAction world)
    {
        long elapsed = world.ActiveTicks;
        foreach (var agent in world.Set<SavedCapsuleerAgent>().OrderBy(a => a.PilotId).ToArray())
        {
            if (world.Set<SavedCapsuleerJob>().Find(agent.PilotId) is not null)
            {
                continue;
            }
            CapsuleerSchedule schedule = new(
                checked(elapsed + TimeSpan.TicksPerSecond),
                agent.Career == CapsuleerCareer.Mining ? CapsuleerAction.Prepare : CapsuleerAction.Trade
            );
            bool inSpace = world.Pilots.Find(agent.PilotId)?.ActiveShip.Position is not null;
            if (agent.Career == CapsuleerCareer.Mining)
            {
                var row = world.Filter<SavedCapsuleerCommunity>(c => c.PilotId == agent.PilotId).Single();
                if (MinerCheckpoint.Read(row.Progress) is { } work)
                {
                    work = work with
                    {
                        ActiveTicks = elapsed,
                        ClockOffsetTicks = checked(elapsed - (long)work.Stamp * TimeSpan.TicksPerSecond),
                        DueTicks = CapsuleerScheduleRules.MigrateDeadline(elapsed, work.Stamp, work.DueStamp),
                    };
                    new MinerCheckpoint(agent.PilotId, work).Apply(world);
                    schedule = CapsuleerScheduleRules.Next(work, inSpace);
                }
            }
            else
            {
                var row = world.Filter<SavedCapsuleerHauler>(h => h.PilotId == agent.PilotId).Single();
                if (HaulerCheckpoint.Read(row.Progress) is { } work)
                {
                    work = work with
                    {
                        ActiveTicks = elapsed,
                        ClockOffsetTicks = checked(elapsed - (long)work.Stamp * TimeSpan.TicksPerSecond),
                        DueTicks = CapsuleerScheduleRules.MigrateDeadline(elapsed, work.Stamp, work.DueStamp),
                    };
                    new HaulerCheckpoint(agent.PilotId, work).Apply(world);
                    schedule = CapsuleerScheduleRules.Next(work, inSpace);
                }
            }
            CapsuleerJobCheckpoint.Apply(world, new(agent.PilotId, 0, 0, schedule.DueTicks, schedule.Action));
        }
    }

    internal static void AddAgent(WorldAction world, CapsuleerAgent agent)
    {
        if (world.Set<SavedCapsuleerAgent>().Find(agent.PilotId) is not null)
        {
            return;
        }
        world.Add(
            new SavedCapsuleerAgent
            {
                PilotId = agent.PilotId,
                CommunitySystemId = agent.CommunitySystemId,
                Career = agent.Career,
                FounderShipId = agent.FounderShipId,
                FounderModuleId = agent.FounderModuleId,
                StartingCents = agent.StartingCents,
                Slot = agent.Slot,
            }
        );
    }
}
