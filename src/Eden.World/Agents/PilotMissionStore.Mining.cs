using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static void SeedMiningDeposits(WorldAction world, SavedMission mission, MissionProfile profile)
    {
        for (int index = 0; index < profile.Deposits.Length; index++)
        {
            var deposit = profile.Deposits[index];
            var position = new SpacePosition(mission.X, mission.Y, mission.Z) + deposit.Contact.Position;
            world.Add(
                new WorldAsteroid
                {
                    Id = MissionMiningRules.DepositId(mission.Id, index),
                    SystemId = mission.SystemId,
                    TypeId = deposit.Contact.TypeId,
                    Quantity = deposit.Quantity,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    Radius = deposit.Contact.Radius,
                }
            );
        }
    }

    private static IEnumerable<StaticSpaceObject> MiningContacts(WorldAction world, SavedMission mission, MissionProfile profile, int room)
    {
        long first = MissionMiningRules.DepositId(mission.Id, 0);
        return MissionDeposits(world.Set<WorldAsteroid>(), mission.Id)
            .Where(a => a.Quantity > 0)
            .OrderBy(a => a.Id)
            .AsEnumerable()
            .Select(rock => (Rock: rock, Deposit: profile.Deposits[(int)(rock.Id - first)]))
            .Where(pair => pair.Deposit.RoomId == 0 || pair.Deposit.RoomId == room)
            .Select(pair => pair.Deposit.Contact with { Id = pair.Rock.Id, Position = new(pair.Rock.X, pair.Rock.Y, pair.Rock.Z) });
    }

    private static IQueryable<WorldAsteroid> MissionDeposits(IQueryable<WorldAsteroid> deposits, long mission)
    {
        long first = MissionMiningRules.DepositId(mission, 0);
        long end = first + MissionMiningRules.DepositsPerMission;
        return deposits.Where(a => a.Id >= first && a.Id < end);
    }

    private static void RequireMiningAccess(SavedMission? mission, long pilot, long now)
    {
        if (mission?.PilotId != pilot || mission.State != MissionState.Accepted || mission.ExpiresAt <= now)
        {
            throw new InvalidDataException("This mining deposit is only available in your active mission pocket.");
        }
    }

    internal static void RequireMiningAccess(WorldDbContext world, long deposit, long pilot, long now) =>
        RequireMiningAccess(world.Set<SavedMission>().Find(MissionMiningRules.MissionId(deposit)), pilot, now);

    internal static void RequireMiningAccess(WorldAction world, long deposit, long pilot, long now) =>
        RequireMiningAccess(world.Set<SavedMission>().Find(MissionMiningRules.MissionId(deposit)), pilot, now);

    internal static void MiningDepleted(WorldDbContext world, long deposit)
    {
        var mission = world.Set<SavedMission>().Find(MissionMiningRules.MissionId(deposit))!;
        mission.ObjectiveComplete = !MissionDeposits(world.Set<WorldAsteroid>(), mission.Id).Any(a => a.Quantity > 0);
    }

    internal static void MiningProgress(WorldAction world, CatalogDbContext catalog, long deposit, bool depleted, long now)
    {
        var mission = world.Set<SavedMission>().Find(MissionMiningRules.MissionId(deposit))!;
        if (!MissionCatalog.IsScriptedMining(mission.MissionId))
        {
            if (depleted)
            {
                mission.ObjectiveComplete = !MissionDeposits(world.Set<WorldAsteroid>(), mission.Id).Any(a => a.Quantity > 0);
            }
            return;
        }
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        if (profile.Definition.Mining)
        {
            mission.ObjectiveComplete =
                !profile.Definition.EpicArcId.HasValue
                || depleted && !MissionDeposits(world.Set<WorldAsteroid>(), mission.Id).Any(a => a.Quantity > 0);
            return;
        }
        SpawnTriggeredWaves(world, catalog, mission, profile, MissionWaveTrigger.Mining, 0, now);
    }
}
