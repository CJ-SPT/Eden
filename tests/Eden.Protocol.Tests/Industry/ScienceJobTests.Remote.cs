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

public sealed partial class ScienceJobTests
{
    [Theory]
    [InlineData(IndustryActivity.ResearchMaterial, false, AccountCloneGrade.Alpha, null, true)]
    [InlineData(IndustryActivity.ResearchTime, true, AccountCloneGrade.Omega, IndustrySkill.ScientificNetworking, true)]
    [InlineData(IndustryActivity.Copying, true, AccountCloneGrade.Omega, IndustrySkill.SupplyChainManagement, false)]
    [InlineData(IndustryActivity.Copying, true, AccountCloneGrade.Omega, IndustrySkill.ScientificNetworking, true)]
    [InlineData(IndustryActivity.Invention, true, AccountCloneGrade.Omega, IndustrySkill.ScientificNetworking, true)]
    [InlineData(IndustryActivity.ResearchMaterial, true, AccountCloneGrade.Alpha, IndustrySkill.ScientificNetworking, false)]
    public async Task NativeRemoteScienceUsesScientificNetworkingAndPreservesLaboratoryOutputs(
        IndustryActivity activity,
        bool acrossRegions,
        AccountCloneGrade grade,
        IndustrySkill? trained,
        bool allowed
    )
    {
        const long account = 1000001;
        const int neighbor = 30000144;
        const int engineeringDatacore = 20424;
        using var f = Create();
        var pilot = f.Pilot;
        long facility = pilot.StationId;
        WorldDatabase database = new(f.Db.Path);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Region(Id,Name) VALUES (10008000,'Laboratory test region');
                INSERT INTO Constellation(Id,RegionId,Name) VALUES (20008000,10008000,'Laboratory test constellation');
                UPDATE SolarSystem SET ConstellationId=20008000 WHERE Id={neighbor};
                INSERT INTO ItemType(Id,Name,GroupId) VALUES
                    ({(int)IndustrySkill.SupplyChainManagement},'Supply Chain Management',257),
                    ({(int)IndustrySkill.ScientificNetworking},'Scientific Networking',257);
                INSERT INTO TypeAttribute VALUES ({(int)IndustrySkill.SupplyChainManagement},275,3),
                    ({(int)IndustrySkill.ScientificNetworking},275,3);
                """
            );
            if (trained != IndustrySkill.SupplyChainManagement)
            {
                // Exercise location discovery at a station that offers only laboratory service.
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data='{{\"services\":[7,15,17]}}' WHERE Dataset='client/stationoperations' AND Key='14'"
                );
            }
        }
        long jobId;
        IndustryRequest request;
        using (var host = ServerServices.Build(database, f.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
            {
                action.Set<Account>().Find(account)!.CloneGrade = grade;
                if (trained is IndustrySkill skill)
                {
                    action.Add(
                        new PilotSkill
                        {
                            PilotId = pilot.PilotId,
                            TypeId = (int)skill,
                            Points = TrainedSkill.PointsForLevel(3, 1),
                        }
                    );
                }
                action.Commit();
            }
            request =
                activity == IndustryActivity.Invention
                    ? Invention(f, MinerCopy(f))
                    : Request(f, activity, licensed: activity == IndustryActivity.Copying ? 2 : 0);
            int system = acrossRegions ? neighbor : pilot.SystemId;
            f.Db.Store.SetPosition(pilot.ShipId, new(10, 20, 30), system);
            var remote = f.Pilot;
            using PilotCallContext context = new(
                account,
                remote.Account,
                [remote],
                active => new(f.Db.Store, active, f.Clock, new(), []),
                7,
                SignedSettings.Empty
            );
            context.Select(remote);
            Assert.NotNull(context.Pilot.Position);
            Assert.Equal(system, context.Pilot.SystemId);
            using var scope = host.CreateScope();
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            PilotCallResult Call(string service, string method, params object?[] args) =>
                router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

            var locations = Call("facilityManager", "GetFacilityLocations", facility, pilot.PilotId);
            var quote = RemoteScienceQuote(pilot.PilotId, request);
            var connected = Call("industryMonitor", "ConnectJob", quote);
            var installed = Call("industryManager", "InstallJob", quote);
            if (!allowed)
            {
                if (trained == IndustrySkill.SupplyChainManagement)
                {
                    Assert.IsType<PilotCallResult.Success>(locations);
                }
                Assert.Contains("Scientific Networking", Assert.IsType<PilotCallResult.Failure>(connected).Message);
                Assert.Contains("Scientific Networking", Assert.IsType<PilotCallResult.Failure>(installed).Message);
                Assert.Empty(context.Notifications);
                Assert.Empty(f.Store.Jobs(pilot.PilotId, false));
                return;
            }

            var place = Assert.Single(Assert.IsType<BlueList<IndustryInventoryLocationReply>>(RemoteReply(locations)).Items).Location;
            Assert.Equal(facility, place.Id);
            var monitor = Assert.IsType<IndustryMonitorReply>(RemoteReply(connected));
            Assert.Equal(20L, monitor.Materials[engineeringDatacore]);
            jobId = Assert.IsType<long>(BlueMarshal.Decode(BlueMarshal.Encode(RemoteReply(installed))));
            Assert.Equal(activity, Assert.Single(context.Notifications.OfType<IndustryJobChanged>()).Job.Activity);
            if (activity == IndustryActivity.Invention)
            {
                scope.ServiceProvider.GetRequiredService<IndustryHandler>().Advance(context);
                var change = Assert.Single(context.Notifications.OfType<IndustryMaterialsChanged>());
                Assert.Equal((monitor.MonitorId, 18L), (change.MonitorId, change.Materials[engineeringDatacore]));
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(database, f.Clock, liveWorld: true);
        var industry = reopened.GetRequiredService<IPilotIndustry>();
        var job = industry.Job(pilot.PilotId, jobId);
        Assert.Equal((facility, activity, IndustryJobStatus.Installed), (job.Facility.Id, job.Activity, job.Status));
        Assert.Equal(jobId, industry.Blueprint(pilot.PilotId, request.BlueprintId).JobId);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        var delivery = industry.Deliver(pilot.PilotId, jobId, pilot.SystemId);
        Assert.Equal(IndustryJobStatus.Delivered, delivery.Job.Status);
        var original = industry.Blueprint(pilot.PilotId, request.BlueprintId);
        Assert.Null(original.JobId);
        Assert.Equal(facility, original.Item.LocationId);
        switch (activity)
        {
            case IndustryActivity.ResearchMaterial:
                Assert.Equal(1, original.MaterialEfficiency);
                break;
            case IndustryActivity.ResearchTime:
                Assert.Equal(2, original.TimeEfficiency);
                break;
            case IndustryActivity.Copying:
                Assert.Equal(facility, Assert.Single(delivery.Changes).Item.LocationId);
                break;
            case IndustryActivity.Invention:
                Assert.Equal(2, original.RunsRemaining);
                Assert.Equal(delivery.Job.SuccessfulRuns!.Value, delivery.Changes.Count);
                Assert.All(delivery.Changes, change => Assert.Equal(facility, change.Item.LocationId));
                break;
        }
    }

    private static BlueModel RemoteReply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return ((PilotCallResult.Success)result).Reply;
    }

    private static Dictionary<string, object?> RemoteScienceQuote(long pilot, IndustryRequest request)
    {
        BlueNewObject location = new(
            BlueNewObject.IndustryLocationClass,
            new Dictionary<string, object?>
            {
                ["itemID"] = request.FacilityId,
                ["ownerID"] = pilot,
                ["flagID"] = (int)InventoryFlag.Hangar,
            }
        );
        return new()
        {
            ["blueprintID"] = request.BlueprintId,
            ["blueprintTypeID"] = request.BlueprintTypeId,
            ["facilityID"] = request.FacilityId,
            ["characterID"] = pilot,
            ["activityID"] = (int)request.Activity,
            ["account"] = new object?[] { pilot, 1000L },
            ["cost"] = 0d,
            ["tax"] = 0d,
            ["time"] = request.QuotedSeconds,
            ["runs"] = request.Runs,
            ["productTypeID"] = request.ProductTypeId,
            ["licensedRuns"] = request.LicensedRuns,
            ["inputLocation"] = location,
            ["outputLocation"] = location,
            ["materials"] = request.QuotedMaterials.ToDictionary(item => (long)item.Key, item => (object?)(long)item.Value),
        };
    }
}
