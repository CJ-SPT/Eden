using Eden.Data.World.Structures;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Industry.IndustryTests.Fixture;

namespace Eden.Protocol.Tests.Industry;

public sealed class IndustryFacilityLookupTests
{
    [Fact]
    public void RegionalFacilityBrowserWorksFromAnNpcStationWithoutIndustry()
    {
        using Fixture f = new();
        ContractAssetTestData.AddRemoteStation(f.Db);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7]}}'),
                    ('client/stationoperations','15','{{"services":[14]}}');
                UPDATE Station SET OperationId=15 WHERE Id=60003761;
                """
            );
        }
        using var host = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var pilot = f.Pilot;
        using var context = Context(f);
        var handler = new IndustryHandler(f.Store);

        List<string> commands = [];
        using (SessionPerformance.Begin("resident regional facility browsing", _ => { }, force: true, command: commands.Add))
        {
            var result = Success(handler.Call(context, "facilityManager", "GetFacilities", []));
            var facility = Assert.Single(Assert.IsType<BlueList<IndustryFacilityReply>>(result).Items);
            Assert.Equal(ContractAssetTestData.RemoteStation, facility.Facility.Id);
            Assert.Equal(pilot.SystemId, facility.Facility.SystemId);
            Assert.Contains(215, facility.Products);
            Assert.Single(Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(result))));
        }
        Assert.DoesNotContain(commands, sql => sql.Contains("World", StringComparison.Ordinal));
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "facilityManager", "GetFacilityLocations", [pilot.DockedLocationId, pilot.PilotId])
        );
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "facilityManager", "GetFacilityLocations", [(long)ContractAssetTestData.RemoteStation, pilot.PilotId])
        );
        Assert.Empty(context.Notifications);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("set")]
    [InlineData("tuple")]
    public void JobWindowBatchPrimesRequestedFacilitiesAndStillDeliversReadyJobs(string collection)
    {
        using Fixture f = new();
        ContractAssetTestData.AddRemoteStation(f.Db);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Region(Id,Name) VALUES(10000043,'Remote region');
                INSERT INTO Constellation(Id,RegionId,Name) VALUES(20000021,10000043,'Remote constellation');
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES(30000001,20000021,'Remote system');
                UPDATE Station SET SolarSystemId=30000001 WHERE Id=60003761;
                """
            );
        }
        using var host = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job;
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        Assert.Equal(IndustryJobStatus.Ready, f.Store.Job(f.Pilot.PilotId, job.Id).Status);
        Assert.DoesNotContain(f.Store.Facilities(f.Pilot.PilotId), facility => facility.Id == ContractAssetTestData.RemoteStation);
        using var context = Context(f);
        long[] ids = [job.Facility.Id, ContractAssetTestData.RemoteStation, long.MaxValue, job.Facility.Id];
        object selection = collection switch
        {
            "set" => new BlueConstructor("__builtin__.set", [ids.Cast<object?>().ToList()]),
            "tuple" => ids.Cast<object?>().ToArray(),
            _ => ids.Cast<object?>().ToList(),
        };
        var reply = Assert.IsType<BlueList<IndustryFacilityReply>>(Success(Call(f, context, [selection])));
        Assert.Equal(new[] { job.Facility.Id, (long)ContractAssetTestData.RemoteStation }, reply.Items.Select(item => item.Facility.Id));
        Assert.All(reply.Items, item => Assert.Contains(215, item.Products));
        var wire = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply)));
        foreach (var value in wire)
        {
            var fields = BlueRead.Fields(value);
            Assert.True(Assert.IsType<bool>(fields["online"]));
            Assert.IsType<long>(fields["facilityID"]);
            Assert.IsType<long>(fields["ownerID"]);
            Assert.IsType<long>(fields["solarSystemID"]);
            var activities = Assert.IsType<Dictionary<long, object?>>(fields["activities"]);
            var manufacturing = Assert.IsType<object?[]>(activities[(long)IndustryActivity.Manufacturing]);
            Assert.Equal(6, manufacturing.Length);
            Assert.Contains(215L, Assert.IsType<List<object?>>(manufacturing[5]));
        }
        Assert.Empty(context.Notifications);
        List<string> commands = [];
        using (SessionPerformance.Begin("resident industry job facility lookup", _ => { }, force: true, command: commands.Add))
        {
            Assert.Equal(2, Assert.IsType<BlueList<IndustryFacilityReply>>(Success(Call(f, context, [selection]))).Items.Count);
        }
        Assert.DoesNotContain(commands, sql => sql.Contains("World", StringComparison.Ordinal));
        Assert.Equal(IndustryJobStatus.Ready, f.Store.Job(f.Pilot.PilotId, job.Id).Status);
        Assert.IsType<PilotCallResult.Success>(
            new IndustryHandler(f.Store).Call(context, "industryManager", "CompleteJob", [job.Id, (long)job.Facility.SystemId])
        );
        Assert.Equal(100, f.Quantity(215));
        Assert.Equal(IndustryJobStatus.Delivered, Assert.Single(context.Notifications.OfType<IndustryJobChanged>()).Job.Status);
    }

    [Fact]
    public void BatchRejectsMalformedRequestsAndReturnsAnEmptyListForUnknownOrEmptySelections()
    {
        using Fixture f = new();
        using var host = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        using var context = Context(f);
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [null],
                [1L],
                [new List<object?> { "invalid" }],
                [new List<object?> { -1L }],
                [new List<object?>(), 1L],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Call(f, context, args));
        }
        Assert.IsType<PilotCallResult.Failure>(Call(f, context, [new List<object?>()], new() { ["ownerID"] = 90000002L }));
        Assert.Empty(Assert.IsType<BlueList<IndustryFacilityReply>>(Success(Call(f, context, [new List<object?>()]))).Items);
        Assert.Empty(
            Assert
                .IsType<BlueList<IndustryFacilityReply>>(
                    Success(Call(f, context, [new BlueConstructor("__builtin__.set", [new List<object?> { long.MaxValue }])]))
                )
                .Items
        );
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void FacilityBatchObservesCurrentResidentStructureServicesAccessAndDestruction()
    {
        using Eden.Protocol.Tests.Structures.StructureTests.Fixture f = new(liveWorld: true);
        var structure = f.Online(35825);
        long module = f.Item(35878);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 5000), 5000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        var industry = f.Db.Services.GetRequiredService<IPilotIndustry>();
        Assert.Equal(structure.Id, Assert.Single(industry.FacilitiesById(f.PilotId, [structure.Id])).Id);
        f.Structures.SetAccess(f.PilotId, structure.Id, false, [new(f.PilotId, false)]);
        Assert.Empty(industry.FacilitiesById(f.PilotId, [structure.Id]));
        f.Structures.SetAccess(f.PilotId, structure.Id, false, []);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, false);
        Assert.False(Assert.Single(industry.FacilitiesById(f.PilotId, [structure.Id])).Manufacturing);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        Assert.True(Assert.Single(industry.FacilitiesById(f.PilotId, [structure.Id])).Manufacturing);
        using (var action = f.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin())
        {
            action.Set<SavedStructure>().Find(structure.Id)!.State = StructureState.Destroyed;
            action.Commit();
        }
        Assert.Empty(industry.FacilitiesById(f.PilotId, [structure.Id]));
    }

    private static PilotCallContext Context(Fixture f)
    {
        var pilot = f.Pilot;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), []),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    private static PilotCallResult Call(
        Fixture f,
        PilotCallContext context,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) =>
        new IndustryHandler(f.Store).Call(
            context,
            "facilityManager",
            "GetFacilitiesByID",
            BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(args)), args.Length),
            keywords
        );

    private static BlueModel Success(PilotCallResult result) => Assert.IsType<PilotCallResult.Success>(result).Reply;
}
