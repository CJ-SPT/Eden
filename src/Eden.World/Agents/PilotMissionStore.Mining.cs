using Eden.Data.World.Agents;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
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

    private static IEnumerable<StaticSpaceObject> MiningContacts(WorldAction world, SavedMission mission, MissionProfile profile)
    {
        long first = MissionMiningRules.DepositId(mission.Id, 0);
        return MissionDeposits(world.Set<WorldAsteroid>(), mission.Id)
            .Where(a => a.Quantity > 0)
            .OrderBy(a => a.Id)
            .AsEnumerable()
            .Select(rock => profile.Deposits[(int)(rock.Id - first)].Contact with { Id = rock.Id, Position = new(rock.X, rock.Y, rock.Z) });
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

    internal static void MiningDepleted(WorldAction world, long deposit)
    {
        var mission = world.Set<SavedMission>().Find(MissionMiningRules.MissionId(deposit))!;
        mission.ObjectiveComplete = !MissionDeposits(world.Set<WorldAsteroid>(), mission.Id).Any(a => a.Quantity > 0);
    }
}
