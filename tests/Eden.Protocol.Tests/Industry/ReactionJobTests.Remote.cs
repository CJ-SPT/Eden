using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Structures;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Structures;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class ReactionJobTests
{
    private const int RemoteFormula = 46169;
    private const int RemoteProduct = 16655;
    private const int RemoteSourceSystem = 30000144;
    private const int Cobalt = 16640;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRemoteReactionUsesRefineryAccessAndDeliversAfterCheckpoint(bool crossSystem)
    {
        const long account = 1000001;
        using StructureTests.Fixture f = new();
        WorldDatabase database = new(f.Db.Path);
        AddRemoteReactionCatalog(database);
        long structureId;
        long formulaId;
        long jobId;
        int facilitySystem = StructureTests.Fixture.SystemId;
        int origin = crossSystem ? RemoteSourceSystem : facilitySystem;
        using (var host = ServerServices.Build(database, f.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var structures = host.GetRequiredService<IPilotStructures>();
            using (var action = live.Economy.Begin(new(pilots: [f.PilotId])))
            {
                action.Set<Account>().Find(account)!.CloneGrade = AccountCloneGrade.Omega;
                action.Add(
                    new PilotSkill
                    {
                        PilotId = f.PilotId,
                        TypeId = (int)IndustrySkill.Reactions,
                        Points = TrainedSkill.PointsForLevel(1, 4),
                    }
                );
                action.Commit();
            }
            long Item(int type, int quantity = 1) => RemoteItem(live, f.PilotId, f.ShipId, type, quantity, InventoryFlag.Cargo);

            var deployed = structures.Deploy(f.PilotId, new(Item(Athanor), StructureTests.Fixture.Point, 0, "Range refinery")).Structure;
            structureId = deployed.Id;
            f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
            var refinery = structures.Read(f.PilotId, structureId)!;
            if (refinery.Profile.CoreTypeId is int core)
            {
                structures.InstallCore(f.PilotId, structureId, Item(core));
            }
            f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(15));
            structures.Read(f.PilotId, structureId);
            long reactor = Item(CompositeReactor);
            structures.FitService(f.PilotId, structureId, reactor, StructureRules.FirstServiceSlot);
            structures.DepositFuel(f.PilotId, structureId, Item(Fuel, 2000), 2000);
            structures.SetServiceOnline(f.PilotId, structureId, reactor, true);
            formulaId = RemoteItem(live, f.PilotId, structureId, RemoteFormula, 1, InventoryFlag.Hangar);
            RemoteItem(live, f.PilotId, structureId, Cobalt, 200, InventoryFlag.Hangar);
            RemoteItem(live, f.PilotId, structureId, Input, 200, InventoryFlag.Hangar);
            RemoteItem(live, f.PilotId, structureId, 4247, 10, InventoryFlag.Hangar);

            f.Db.Store.SetPosition(f.ShipId, new(1, 2, 3), origin);
            var pilot = f.Db.Store.FindPilot(account)!;
            using PilotCallContext context = new(
                account,
                pilot.Account,
                [pilot],
                active => new(f.Db.Store, active, f.Clock, new(), []),
                7,
                SignedSettings.Empty
            );
            context.Select(pilot);
            Assert.Equal(origin, context.Pilot.SystemId);
            Assert.NotNull(context.Pilot.Position);
            using var scope = host.CreateScope();
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            PilotCallResult Call(string service, string method, params object?[] args) =>
                router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

            var quote = RemoteReactionQuote(f.PilotId, structureId, formulaId);
            if (crossSystem)
            {
                Assert.Contains(
                    "Remote Reactions",
                    Assert.IsType<PilotCallResult.Failure>(Call("industryManager", "InstallJob", quote)).Message
                );
                Assert.Empty(context.Notifications);
                using (var action = live.Economy.Begin(new(pilots: [f.PilotId])))
                {
                    action.Add(
                        new PilotSkill
                        {
                            PilotId = f.PilotId,
                            TypeId = (int)IndustrySkill.RemoteReactions,
                            Points = TrainedSkill.PointsForLevel(3, 1),
                        }
                    );
                    action.Set<Account>().Find(account)!.CloneGrade = AccountCloneGrade.Alpha;
                    action.Commit();
                }
                Assert.Contains(
                    "Remote Reactions",
                    Assert.IsType<PilotCallResult.Failure>(Call("industryManager", "InstallJob", quote)).Message
                );
                using var omega = live.Economy.Begin(new(pilots: [f.PilotId]));
                omega.Set<Account>().Find(account)!.CloneGrade = AccountCloneGrade.Omega;
                omega.Commit();
            }
            var places = Assert.IsType<BlueList<IndustryInventoryLocationReply>>(
                ReactionReply(Call("facilityManager", "GetFacilityLocations", structureId, f.PilotId))
            );
            Assert.Equal(structureId, Assert.Single(places.Items).Location.Id);
            var monitor = Assert.IsType<IndustryMonitorReply>(ReactionReply(Call("industryMonitor", "ConnectJob", quote)));
            Assert.Equal(200, monitor.Materials[Cobalt]);
            jobId = Assert.IsType<long>(
                BlueMarshal.Decode(BlueMarshal.Encode(ReactionReply(Call("industryManager", "InstallJob", quote))))
            );
            var job = Assert.Single(context.Notifications.OfType<IndustryJobChanged>()).Job;
            Assert.Equal((structureId, IndustryActivity.Reaction), (job.Facility.Id, job.Activity));
            scope.ServiceProvider.GetRequiredService<IndustryHandler>().Advance(context);
            Assert.Equal(100, Assert.Single(context.Notifications.OfType<IndustryMaterialsChanged>()).Materials[Cobalt]);
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
            structures.SetAccess(f.PilotId, structureId, false, [new(f.PilotId, false)]);
            Assert.Contains(
                "access",
                Assert.IsType<PilotCallResult.Failure>(Call("industryManager", "CompleteJob", jobId, (long)facilitySystem)).Message
            );
            structures.SetAccess(f.PilotId, structureId, false, []);
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(database, f.Clock, liveWorld: true);
        var industry = reopened.GetRequiredService<IPilotIndustry>();
        Assert.Equal(IndustryJobStatus.Ready, industry.Job(f.PilotId, jobId).Status);
        Assert.Equal(jobId, industry.Blueprint(f.PilotId, formulaId).JobId);
        var output = Assert.Single(industry.Deliver(f.PilotId, jobId, facilitySystem).Changes).Item;
        Assert.Equal((structureId, RemoteProduct, 200), (output.LocationId, output.TypeId, output.Quantity));
        Assert.Empty(industry.Deliver(f.PilotId, jobId, facilitySystem).Changes);
        var request = new IndustryRequest(
            formulaId,
            RemoteFormula,
            structureId,
            1,
            RemoteProduct,
            9072,
            new Dictionary<int, int>
            {
                [4247] = 5,
                [Cobalt] = 100,
                [Input] = 100,
            },
            Activity: IndustryActivity.Reaction
        );
        var cancelled = industry.Install(f.PilotId, request).Job;
        Assert.Equal(IndustryJobStatus.Cancelled, industry.Cancel(f.PilotId, cancelled.Id, facilitySystem).Job.Status);
        Assert.Null(industry.Blueprint(f.PilotId, formulaId).JobId);
    }

    private static long RemoteItem(LiveWorld live, long owner, long location, int type, int quantity, InventoryFlag flag)
    {
        using var action = live.Economy.Begin(new(pilots: [owner]));
        long id = action.NextItemId();
        action.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = owner,
                LocationId = location,
                FlagId = (int)flag,
                TypeId = type,
                Quantity = quantity,
            }
        );
        action.Commit();
        return id;
    }

    private static BlueModel ReactionReply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return ((PilotCallResult.Success)result).Reply;
    }

    private static Dictionary<string, object?> RemoteReactionQuote(long pilot, long structure, long formula)
    {
        BlueNewObject location = new(
            BlueNewObject.IndustryLocationClass,
            new Dictionary<string, object?>
            {
                ["itemID"] = structure,
                ["ownerID"] = pilot,
                ["flagID"] = (int)InventoryFlag.Hangar,
            }
        );
        return new()
        {
            ["blueprintID"] = formula,
            ["blueprintTypeID"] = RemoteFormula,
            ["facilityID"] = structure,
            ["characterID"] = pilot,
            ["activityID"] = (int)IndustryActivity.Reaction,
            ["account"] = new object?[] { pilot, 1000L },
            ["cost"] = 0d,
            ["tax"] = 0d,
            ["time"] = 9072d,
            ["runs"] = 1,
            ["productTypeID"] = RemoteProduct,
            ["inputLocation"] = location,
            ["outputLocation"] = location,
            ["materials"] = new Dictionary<long, object?>
            {
                [4247] = 5L,
                [Cobalt] = 100L,
                [Input] = 100L,
            },
        };
    }

    private static void AddRemoteReactionCatalog(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (9,'Blueprint'),(4,'Material');
            INSERT OR IGNORE INTO ItemGroup VALUES (1888,'Composite Formulas',9),(427,'Moon Materials',4),(428,'Intermediate Materials',4);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (46169,'Crystallite Alloy Reaction Formula',1888),
                (16640,'Cobalt',427),(16643,'Cadmium',427),(16655,'Crystallite Alloy',428),
                (45746,'Reactions',257),(45750,'Remote Reactions',257);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (2660,'reactionTimePerLevel');
            INSERT OR IGNORE INTO TypeAttribute VALUES (45746,275,1),(45746,2660,-4),(45750,275,3);
            INSERT OR REPLACE INTO SourceRecord VALUES
                ('client/systems','30001392','{{"securityStatus":0.4}}'),
                ('client/types','46169','{{"published":1,"volume":0.01}}'),
                ('client/types','16640','{{"published":1,"volume":0.01}}'),
                ('client/types','16643','{{"published":1,"volume":0.01}}'),
                ('client/types','16655','{{"published":1,"volume":0.01}}'),
                ('client/blueprints','"46169"','{{"blueprintTypeID":46169,"maxProductionLimit":1000,"activities":{{"reaction":{{
                    "time":10800,"skills":[{{"typeID":45746,"level":2}}],
                    "materials":[{{"typeID":4247,"quantity":5}},{{"typeID":16640,"quantity":100}},{{"typeID":16643,"quantity":100}}],
                    "products":[{{"typeID":16655,"quantity":200}}]}}}}}}');
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                (50008990,30001392,16,10,1,2,3),(50008991,30000144,16,10,1,2,3);
            INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                (50008990,30001392,50008991),(50008991,30000144,50008990);
            """
        );
    }
}
