using Eden.Data.World.Characters;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.FactionWarfare;

internal static class MilitiaAdvantageStore
{
    internal static MilitiaAdvantageState Read(WorldAction world, WarzoneSystem system, long now)
    {
        var row = world.Set<SavedMilitiaAdvantage>().Find(system.SystemId);
        int firstFloor = 0;
        int secondFloor = 0;
        if (system.Adjacency == WarzoneAdjacency.Frontline)
        {
            foreach (int neighbor in FactionWarzoneCatalog.Systems[system.SystemId].Neighbors)
            {
                if (FactionWarzoneStore.Occupier(world, neighbor) == system.Warzone.FirstFaction)
                {
                    firstFloor += MilitiaAdvantageRules.NeighborFloor;
                }
                else
                {
                    secondFloor += MilitiaAdvantageRules.NeighborFloor;
                }
            }
        }
        return new(
            system.SystemId,
            Score(system.Warzone.FirstFaction, row?.FirstContribution ?? 0, row?.FirstUpdatedAt ?? now, firstFloor),
            Score(system.Warzone.SecondFaction, row?.SecondContribution ?? 0, row?.SecondUpdatedAt ?? now, secondFloor)
        );

        MilitiaAdvantageScore Score(MilitiaFaction faction, int saved, long updated, int terrain)
        {
            int floor = Math.Min(terrain, MilitiaAdvantageRules.Maximum);
            return new(faction, MilitiaAdvantageRules.Contribution(saved, updated, now, floor), floor);
        }
    }

    internal static int Apply(
        WorldAction world,
        WarzoneSystem system,
        MilitiaFaction faction,
        int delta,
        long now,
        Pilot? contributor = null
    )
    {
        var current = Read(world, system, now).Score(faction);
        int next = Math.Clamp(current.Contribution + delta, 0, MilitiaAdvantageRules.Maximum - current.Floor);
        if (next == current.Contribution)
        {
            return 0;
        }
        var row =
            world.Set<SavedMilitiaAdvantage>().Find(system.SystemId)
            ?? world.Add(new SavedMilitiaAdvantage { SystemId = system.SystemId }).Entity;
        if (faction == system.Warzone.FirstFaction)
        {
            row.FirstContribution = next;
            row.FirstUpdatedAt = now;
        }
        else
        {
            row.SecondContribution = next;
            row.SecondUpdatedAt = now;
        }
        int applied = next - current.Contribution;
        if (contributor is not null && LiveWorld.Find(world.WorldPath) is { } live)
        {
            long account = contributor.AccountId;
            MilitiaAdvantageContribution notice = new(system.SystemId, contributor.Id, faction, applied, now);
            world.AfterCommit(() => live.PublishMilitiaContribution(account, notice));
        }
        return applied;
    }

    internal static void Reset(WorldAction world, int systemId)
    {
        if (world.Set<SavedMilitiaAdvantage>().Find(systemId) is { } row)
        {
            world.Remove(row);
        }
    }
}
