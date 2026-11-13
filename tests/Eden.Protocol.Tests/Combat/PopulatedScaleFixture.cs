using System.Diagnostics;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Combat;

internal sealed record ScalePilot(long Pilot, long Account, long Ship, int System, long Asteroid, bool Mining, bool Docked);

internal static class PopulatedScaleFixture
{
    private const long FirstPilot = 1900000000;
    private const long FirstAccount = 200000000;
    private const long FirstShip = 9000000000000;
    private const long FirstModule = 9100000000000;
    private const int ModulesPerShip = 100;
    private const int Veldspar = 1230;
    private const long MiningReserve = 1_000_000;

    internal static ScalePilot[] Seed(WorldDatabase database, long template, int players)
    {
        using WorldDbContext world = new(database.Path);
        var prototype = world.Pilots.Single(pilot => pilot.Id == template);
        var ship = world.Ships.Single(row => row.Id == prototype.ActiveShipId);
        if (world.Set<OwnedItem>().Count(item => item.LocationId == ship.Id) >= ModulesPerShip)
        {
            throw new InvalidDataException("The replay template exceeds the reserved module ID range.");
        }
        var stationSystems = database
            .Catalog.Stations.Values.GroupBy(station => station.SolarSystemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(station => station.Id).First());
        var rockIds = world
            .Set<WorldAsteroid>()
            .Where(rock => rock.TypeId == Veldspar && rock.Quantity >= 100000)
            .GroupBy(rock => rock.SystemId)
            .Select(group => group.Min(rock => rock.Id))
            .ToArray();
        var rocks = world
            .Set<WorldAsteroid>()
            .Where(rock => rockIds.Contains(rock.Id))
            .OrderBy(rock => rock.SystemId)
            .AsEnumerable()
            .Where(rock => stationSystems.ContainsKey(rock.SystemId))
            .Take(Math.Min(10, (players + 9) / 10))
            .ToArray();
        if (rocks.Length == 0)
        {
            throw new InvalidDataException("The replay requires a populated world with Veldspar deposits and stations.");
        }
        List<ScalePilot> result = [];
        using var transaction = world.Database.BeginTransaction();
        // Keep the existing population, but put the measured mining grids away from
        // hostile belts so ship destruction cannot change the load halfway through.
        foreach (var rock in rocks)
        {
            rock.X += 1_000_000_000_000d;
            rock.Quantity = MiningReserve;
        }
        world.SaveChanges();
        for (int index = 0; index < players; index++)
        {
            long pilot = FirstPilot + index;
            long account = FirstAccount + index;
            long vessel = FirstShip + index;
            long module = FirstModule + index * ModulesPerShip;
            var rock = rocks[(index / 10) % rocks.Length];
            int station = stationSystems[rock.SystemId].Id;
            bool mining = index % 10 == 0;
            bool docked = index % 10 >= 5;
            string name = $"Scale pilot {index}";
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAccount(Id,UserName,CreatedAt,CloneGrade)
                SELECT {account},{name},CreatedAt,CloneGrade FROM WorldAccount WHERE Id={prototype.AccountId};
                INSERT INTO WorldInventoryOwner(Id) VALUES({pilot});
                INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name,StorageFlagId)
                SELECT {vessel},{pilot},TypeId,{station},{name},StorageFlagId FROM WorldShip WHERE Id={ship.Id};
                INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,CreatedAt,RaceId,BloodlineId,GenderId,
                    AncestryId,SchoolId,SafetyLevel)
                SELECT {pilot},{account},{name},CorporationId,{vessel},CreatedAt,RaceId,BloodlineId,GenderId,
                    AncestryId,SchoolId,SafetyLevel FROM WorldPilot WHERE Id={template};
                INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity,Singleton,Online)
                SELECT {module}+ROW_NUMBER() OVER(ORDER BY Id),{pilot},TypeId,{vessel},FlagId,Quantity,Singleton,Online
                FROM WorldItem WHERE LocationId={ship.Id};
                INSERT INTO WorldPilotSkill(PilotId,TypeId,Points)
                SELECT {pilot},TypeId,Points FROM WorldPilotSkill WHERE PilotId={template};
                INSERT INTO WorldFreeSkillPoints(PilotId,Points,Revision) VALUES({pilot},10000,0);
                """
            );
            if (!docked)
            {
                double x = rock.X + (mining ? 1000 : 2000000d * (index + 1));
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z) VALUES({vessel},{rock.SystemId},{x},{rock.Y},{rock.Z})
                    """
                );
            }
            result.Add(new(pilot, account, vessel, rock.SystemId, rock.Id, mining, docked));
        }
        transaction.Commit();
        return result.ToArray();
    }
}

// Simulation deadlines are repeatable, while elapsed job budgets still measure real CPU time.
internal sealed class ScaleReplayClock : TimeProvider
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _offset;
    private static readonly DateTimeOffset Epoch = new(2026, 11, 14, 1, 14, 55, TimeSpan.Zero);
    public override long TimestampFrequency => Stopwatch.Frequency;

    public override long GetTimestamp() => Stopwatch.GetTimestamp() + Interlocked.Read(ref _offset);

    public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromSeconds((GetTimestamp() - _started) / (double)TimestampFrequency);

    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _offset, (long)(elapsed.TotalSeconds * TimestampFrequency));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new QuietTimer();

    private sealed class QuietTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
