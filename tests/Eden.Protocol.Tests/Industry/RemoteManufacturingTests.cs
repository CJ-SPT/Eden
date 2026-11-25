using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Industry.IndustryTests.Fixture;

namespace Eden.Protocol.Tests.Industry;

public sealed class RemoteManufacturingTests
{
    private const long AccountId = 1000001;
    private const int FirstRouteSystem = 30008001;
    private const int DisconnectedSystem = FirstRouteSystem + 6;
    private const int BlueprintType = 1130;
    private const int Tritanium = 34;
    private const int Pyerite = 35;

    [Theory]
    [InlineData(0, AccountCloneGrade.Alpha, 0, true)]
    [InlineData(1, AccountCloneGrade.Alpha, 1, false)]
    [InlineData(5, AccountCloneGrade.Omega, 1, true)]
    [InlineData(6, AccountCloneGrade.Omega, 1, false)]
    [InlineData(6, AccountCloneGrade.Omega, 2, true)]
    [InlineData(7, AccountCloneGrade.Omega, 5, false)]
    public async Task NativeRemoteManufacturingUsesEffectiveStargateRangeAndPersistsAtTheFacility(
        int distance,
        AccountCloneGrade grade,
        int skillLevel,
        bool allowed
    )
    {
        using Fixture f = new();
        WorldDatabase database = new(f.Db.Path);
        var pilot = f.Pilot;
        long station = pilot.StationId;
        AddRoute(database, pilot.SystemId);
        int source = distance == 0 ? pilot.SystemId : FirstRouteSystem + distance - 1;
        long jobId;
        using (var host = ServerServices.Build(database, f.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
            {
                action.Set<Account>().Find(AccountId)!.CloneGrade = grade;
                if (skillLevel > 0)
                {
                    action.Add(
                        new PilotSkill
                        {
                            PilotId = pilot.PilotId,
                            TypeId = (int)IndustrySkill.SupplyChainManagement,
                            Points = TrainedSkill.PointsForLevel(3, skillLevel),
                        }
                    );
                }
                action.Commit();
            }
            f.Db.Store.SetPosition(pilot.ShipId, new(10, 20, 30), source);
            var remote = f.Pilot;
            using PilotCallContext context = new(
                AccountId,
                remote.Account,
                [remote],
                active => new(f.Db.Store, active, f.Clock, new(), []),
                7,
                SignedSettings.Empty
            );
            context.Select(remote);
            Assert.NotNull(context.Pilot.Position);
            Assert.Equal(source, context.Pilot.SystemId);
            using var scope = host.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IndustryHandler>();
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            PilotCallResult Call(string service, string method, params object?[] args) =>
                router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

            var locations = Call("facilityManager", "GetFacilityLocations", station, pilot.PilotId);
            var request = Request(station, pilot.PilotId);
            var connection = Call("industryMonitor", "ConnectJob", request);
            var install = Call("industryManager", "InstallJob", request);
            if (!allowed)
            {
                foreach (var reply in new[] { locations, connection, install })
                {
                    Assert.Contains("Supply Chain Management", Assert.IsType<PilotCallResult.Failure>(reply).Message);
                }
                Assert.Empty(context.Notifications);
                Assert.Empty(f.Store.Jobs(pilot.PilotId, true));
                var inventory = host.GetRequiredService<IPilotInventory>();
                Assert.Equal(81, inventory.List(pilot.PilotId, station).Where(item => item.TypeId == Tritanium).Sum(item => item.Quantity));
                return;
            }

            var place = Assert.Single(Assert.IsType<BlueList<IndustryInventoryLocationReply>>(Reply(locations)).Items).Location;
            Assert.Equal(station, place.Id);
            var monitor = Assert.IsType<IndustryMonitorReply>(Reply(connection));
            Assert.Equal(81, monitor.Materials[Tritanium]);
            jobId = Assert.IsType<long>(BlueMarshal.Decode(BlueMarshal.Encode(Reply(install))));
            Assert.Equal(station, Assert.Single(context.Notifications.OfType<IndustryJobChanged>()).Job.Facility.Id);
            handler.Advance(context);
            var changed = Assert.Single(context.Notifications.OfType<IndustryMaterialsChanged>());
            Assert.Equal((monitor.MonitorId, 54L, 42L), (changed.MonitorId, changed.Materials[Tritanium], changed.Materials[Pyerite]));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(database, f.Clock, liveWorld: true);
        var industry = reopened.GetRequiredService<IPilotIndustry>();
        var job = industry.Job(pilot.PilotId, jobId);
        Assert.Equal((station, IndustryJobStatus.Installed), (job.Facility.Id, job.Status));
        Assert.Equal(jobId, industry.Blueprint(pilot.PilotId, Fixture.BlueprintId).JobId);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        var product = Assert.Single(industry.Deliver(pilot.PilotId, jobId, job.Facility.SystemId).Changes).Item;
        Assert.Equal((station, 215, 100), (product.LocationId, product.TypeId, product.Quantity));
    }

    private static BlueModel Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return ((PilotCallResult.Success)result).Reply;
    }

    private static Dictionary<string, object?> Request(long station, long pilot)
    {
        BlueNewObject location = new(
            BlueNewObject.IndustryLocationClass,
            new Dictionary<string, object?>
            {
                ["itemID"] = station,
                ["ownerID"] = pilot,
                ["flagID"] = (long)InventoryFlag.Hangar,
            }
        );
        return new()
        {
            ["blueprintID"] = Fixture.BlueprintId,
            ["blueprintTypeID"] = BlueprintType,
            ["facilityID"] = station,
            ["characterID"] = pilot,
            ["activityID"] = (int)IndustryActivity.Manufacturing,
            ["account"] = new object?[] { pilot, 1000L },
            ["cost"] = 0d,
            ["tax"] = 0d,
            ["time"] = 288d,
            ["runs"] = 1,
            ["productTypeID"] = null,
            ["inputLocation"] = location,
            ["outputLocation"] = location,
            ["materials"] = new Dictionary<long, object?> { [Tritanium] = 27L, [Pyerite] = 21L },
        };
    }

    private static void AddRoute(WorldDatabase database, int origin)
    {
        const int remoteConstellation = 20008000;
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        using var transaction = catalog.Database.BeginTransaction();
        catalog.Database.ExecuteSql(
            $"""
            INSERT INTO Region(Id,Name) VALUES (10008000,'Remote region');
            INSERT INTO Constellation(Id,RegionId,Name) VALUES ({remoteConstellation},10008000,'Remote constellation');
            INSERT INTO ItemType(Id,Name,GroupId) VALUES ({(int)IndustrySkill.SupplyChainManagement},'Supply Chain Management',257);
            INSERT INTO TypeAttribute VALUES ({(int)IndustrySkill.SupplyChainManagement},275,3);
            """
        );
        for (int system = FirstRouteSystem; system <= DisconnectedSystem; system++)
        {
            catalog.Database.ExecuteSql(
                $"INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({system},{remoteConstellation},'Remote')"
            );
        }
        for (int system = FirstRouteSystem; system < DisconnectedSystem; system++)
        {
            int previous = system == FirstRouteSystem ? origin : system - 1;
            int gate = 50009000 + 2 * (system - FirstRouteSystem);
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                    ({gate},{previous},16,10,1,2,3),({gate + 1},{system},16,10,1,2,3);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES ({gate},{previous},{gate + 1}),({gate + 1},{system},{gate});
                """
            );
        }
        transaction.Commit();
    }
}
