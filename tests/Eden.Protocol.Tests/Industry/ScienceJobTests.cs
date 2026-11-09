using System.Text.Json;
using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Contracts;
using Eden.Protocol.Docking;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Industry.IndustryTests.Fixture;

namespace Eden.Protocol.Tests.Industry;

public sealed class ScienceJobTests
{
    private sealed class FixedRandom(double value) : IIndustryRandom
    {
        public IReadOnlyList<double> Rolls(int count) => Enumerable.Repeat(value, count).ToArray();
    }

    private const int MinerBlueprint = 785;
    private const int MinerTwoBlueprint = 784;
    private const int Decryptor = 34201;
    private static readonly int[] s_inventionSkills = [11447, 11452, 23121];

    private static Fixture Create()
    {
        Fixture f = new();
        f.AddModules();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            UPDATE SourceRecord SET Data='{{"services":[7,14,15,17]}}' WHERE Dataset='client/stationoperations' AND Key='14';
            INSERT INTO ItemGroup VALUES (1304,'Generic Decryptor',4);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (784,'Miner II Blueprint',167),
                (20424,'Datacore - Mechanical Engineering',18),(20413,'Datacore - Laser Physics',18),
                (11447,'Laser Physics',257),(11452,'Mechanical Engineering',257),(23121,'Gallente Encryption Methods',257),
                (34201,'Accelerant Decryptor',1304),(3402,'Science',257),(3403,'Research',257),
                (3406,'Laboratory Operation',257),(3409,'Metallurgy',257),(24624,'Advanced Laboratory Operation',257);
            INSERT INTO DogmaAttribute(Id,Name) VALUES (452,'copySpeedBonus'),(453,'blueprintmanufactureTimeBonus'),
                (468,'mineralNeedResearchBonus'),(471,'laboratorySlotsBonus'),(1112,'inventionPropabilityMultiplier'),
                (1113,'inventionMEModifier'),(1114,'inventionTEModifier'),(1124,'inventionMaxRunModifier');
            INSERT INTO TypeAttribute VALUES (11447,275,1),(11452,275,1),(23121,275,1),
                (3402,275,1),(3402,452,-5),(3403,275,1),(3403,453,-5),(3409,275,3),(3409,468,-5),
                (3406,275,1),(3406,471,1),(24624,275,8),(24624,471,1),
                (34201,1112,1.2),(34201,1113,2),(34201,1114,10),(34201,1124,1);
            INSERT INTO SourceRecord VALUES ('client/typelist','799',
                '{{"name":"LowerInventionSkillProbabilityTypes","includedTypeIDs":[23087,21790,23121,21791,3408,52308,55025]}}'),
                ('client/blueprints','"784"','{{"blueprintTypeID":784,"activities":{{"manufacturing":{{"time":900,
                    "materials":[{{"typeID":34,"quantity":1}}],"products":[{{"typeID":482,"quantity":1}}]}}}}}}');
            """
        );
        foreach (int type in new[] { MinerTwoBlueprint, 20424, 20413, Decryptor })
        {
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/types",
                    Key = type.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Data = "{\"published\":1,\"marketGroupID\":1,\"volume\":0.01}",
                }
            );
        }
        var iron = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/blueprints" && r.Key == "\"1130\"");
        var data = JsonNode.Parse(iron.Data)!;
        data["activities"]!["research_material"] = JsonNode.Parse("{\"time\":105}");
        data["activities"]!["research_time"] = JsonNode.Parse("{\"time\":105}");
        data["activities"]!["copying"] = JsonNode.Parse("{\"time\":240}");
        iron.Data = data.ToJsonString();
        var miner = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/blueprints" && r.Key == "\"785\"");
        data = JsonNode.Parse(miner.Data)!;
        data["maxProductionLimit"] = 200;
        data["activities"]!["research_material"] = JsonNode.Parse("{\"time\":315}");
        data["activities"]!["research_time"] = JsonNode.Parse("{\"time\":315}");
        data["activities"]!["copying"] = JsonNode.Parse("{\"time\":720}");
        data["activities"]!["invention"] = JsonNode.Parse(
            """
            {"skills":[{"typeID":11447,"level":1},{"typeID":11452,"level":1},{"typeID":23121,"level":1}],
             "materials":[{"typeID":20424,"quantity":2},{"typeID":20413,"quantity":2}],
             "products":[{"typeID":784,"probability":0.34,"quantity":10}],"time":13800}
            """
        );
        miner.Data = data.ToJsonString();
        catalog.SaveChanges();
        using WorldDbContext world = new(f.Db.Path);
        foreach (int skill in s_inventionSkills)
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = skill,
                    Points = 250,
                }
            );
        }
        foreach (int type in new[] { 20424, 20413, Decryptor })
        {
            world.Add(
                new OwnedItem
                {
                    Id = Fixture.BlueprintId + 5000 + type,
                    OwnerId = f.Pilot.PilotId,
                    TypeId = type,
                    LocationId = f.Pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 20,
                }
            );
        }
        world.SaveChanges();
        return f;
    }

    private static IndustryRequest Request(Fixture f, IndustryActivity activity, int runs = 1, int licensed = 0) =>
        new(
            Fixture.BlueprintId,
            1130,
            f.Pilot.StationId,
            runs,
            null,
            activity == IndustryActivity.Copying ? 240d * runs * licensed : 105,
            new Dictionary<int, int>(),
            Activity: activity,
            LicensedRuns: licensed
        );

    private static IndustryResult Complete(Fixture f, IndustryJobDetails job)
    {
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        return f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId);
    }

    private static long MinerCopy(Fixture f, int licensed = 3)
    {
        var request = Request(f, IndustryActivity.Copying, licensed: licensed) with
        {
            BlueprintId = Fixture.BlueprintId + MinerBlueprint,
            BlueprintTypeId = MinerBlueprint,
            QuotedSeconds = 720d * licensed,
        };
        return Assert.Single(Complete(f, f.Store.Install(f.Pilot.PilotId, request).Job).Changes).Item.Id;
    }

    private static IndustryRequest Invention(Fixture f, long copy, int runs = 1, bool decryptor = false) =>
        new(
            copy,
            MinerBlueprint,
            f.Pilot.StationId,
            runs,
            MinerTwoBlueprint,
            13800d * runs,
            decryptor
                ? new Dictionary<int, int>
                {
                    [20424] = 2 * runs,
                    [20413] = 2 * runs,
                    [Decryptor] = runs,
                }
                : new Dictionary<int, int> { [20424] = 2 * runs, [20413] = 2 * runs },
            Activity: IndustryActivity.Invention
        );

    [Theory]
    [InlineData(IndustryActivity.ResearchMaterial, 1, 0)]
    [InlineData(IndustryActivity.ResearchTime, 0, 2)]
    public void ResearchSurvivesRestartChangesOriginalOnlyOnDeliveryAndCannotReplay(IndustryActivity activity, int me, int te)
    {
        using var f = Create();
        var job = f.Store.Install(f.Pilot.PilotId, Request(f, activity)).Job;
        Assert.Equal(activity, job.Activity);
        Assert.Equal(0, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).MaterialEfficiency);
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(job, f.Store.Job(f.Pilot.PilotId, job.Id));
        Complete(f, job);
        var blueprint = f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId);
        Assert.Equal(me, blueprint.MaterialEfficiency);
        Assert.Equal(te, blueprint.TimeEfficiency);
        Assert.Equal(-1, blueprint.RunsRemaining);
        Assert.Null(blueprint.JobId);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes);
        Assert.Equal(blueprint, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId));
    }

    [Fact]
    public void CopiesInheritResearchManufactureWithDiscountsAndExhaustExactlyOnce()
    {
        using var f = Create();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var source = world.Set<OwnedItem>().Find(Fixture.BlueprintId)!;
            source.Singleton = true;
            source.MaterialEfficiency = 10;
            source.TimeEfficiency = 20;
            world.SaveChanges();
        }
        var job = f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.Copying, 2, 3)).Job;
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var copies = Complete(f, job).Changes;
        Assert.Equal(2, copies.Count);
        Assert.All(copies, c => Assert.True(c.Item.BlueprintCopy));
        Assert.All(copies, c => Assert.Equal(-2, InventoryRow.From(c.Item).Quantity));
        Assert.Equal(2, copies.Select(c => c.Item.Id).Distinct().Count());
        var copy = f.Store.Blueprint(f.Pilot.PilotId, copies[0].Item.Id);
        Assert.Equal(10, copy.MaterialEfficiency);
        Assert.Equal(20, copy.TimeEfficiency);
        Assert.Equal(3, copy.RunsRemaining);
        Assert.Equal(-1, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).RunsRemaining);
        var manufacture = f.Request(3) with
        {
            BlueprintId = copy.Item.Id,
            QuotedSeconds = 691,
            QuotedMaterials = new Dictionary<int, int> { [34] = 73, [35] = 57 },
        };
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, manufacture with { Runs = 4 }));
        var manufacturing = f.Store.Install(f.Pilot.PilotId, manufacture).Job;
        Assert.True(manufacturing.BlueprintCopy);
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, manufacture));
        Complete(f, manufacturing);
        Assert.Throws<InvalidDataException>(() => f.Store.Blueprint(f.Pilot.PilotId, copy.Item.Id));
        Assert.Equal(300, f.Quantity(215));
        Assert.Equal(1, f.Store.Blueprint(f.Pilot.PilotId, copies[1].Item.Id).Item.Quantity);
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, manufacturing.Id, f.Pilot.SystemId).Changes);
    }

    [Fact]
    public void ScienceSlotsAreSharedWithEachOtherAndIndependentOfManufacturing()
    {
        using var f = Create();
        var manufacture = f.Store.Install(f.Pilot.PilotId, f.ModuleRequest(IndustryBlueprintType.MinerI)).Job;
        var research = f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.ResearchMaterial)).Job;
        Assert.Throws<InvalidDataException>(() =>
            f.Store.Install(
                f.Pilot.PilotId,
                Request(f, IndustryActivity.Copying, licensed: 1) with
                {
                    BlueprintId = Fixture.BlueprintId + 1032,
                    BlueprintTypeId = 1032,
                }
            )
        );
        f.Store.Cancel(f.Pilot.PilotId, research.Id, f.Pilot.SystemId);
        Assert.Equal(0, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).MaterialEfficiency);
        Assert.Equal(IndustryJobStatus.Installed, f.Store.Job(f.Pilot.PilotId, manufacture.Id).Status);
        f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.Copying, licensed: 1));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InventionSnapshotsChanceConsumesPaidInputsAndDeliversSavedOutcomesAfterRestart(bool decryptor, bool success)
    {
        using var f = Create();
        long copy = MinerCopy(f);
        f.Random = new FixedRandom(success ? 0.1 : 0.99);
        var job = f.Store.Install(f.Pilot.PilotId, Invention(f, copy, 2, decryptor)).Job;
        Assert.Equal(16, f.Quantity(20424));
        Assert.Equal(16, f.Quantity(20413));
        Assert.Equal(decryptor ? 18 : 20, f.Quantity(Decryptor));
        Assert.Equal(3, f.Store.Blueprint(f.Pilot.PilotId, copy).RunsRemaining);
        Assert.Equal(0.34 * (1 + 1 / 30d + 1 / 30d + 1 / 40d) * (decryptor ? 1.2 : 1), job.Probability, 10);
        Assert.Null(job.SuccessfulRuns);
        int successes;
        using (WorldDbContext world = new(f.Db.Path))
        {
            successes = world.Set<IndustryJob>().Find(job.Id)!.SuccessfulRuns!.Value;
        }
        Assert.Equal(success ? 2 : 0, successes);
        f.Random = new FixedRandom(success ? 0.99 : 0.1);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(job, f.Store.Job(f.Pilot.PilotId, job.Id));
        var delivered = Complete(f, job);
        Assert.Equal(successes, delivered.Job.SuccessfulRuns);
        Assert.Equal(1, f.Store.Blueprint(f.Pilot.PilotId, copy).RunsRemaining);
        var outputs = delivered.Changes.Where(c => c.Item.TypeId == MinerTwoBlueprint).ToArray();
        Assert.Equal(successes, outputs.Length);
        foreach (var output in outputs)
        {
            var bp = f.Store.Blueprint(f.Pilot.PilotId, output.Item.Id);
            Assert.Equal(decryptor ? 4 : 2, bp.MaterialEfficiency);
            Assert.Equal(decryptor ? 14 : 4, bp.TimeEfficiency);
            Assert.Equal(decryptor ? 11 : 10, bp.RunsRemaining);
            Assert.True(output.Item.Singleton);
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes);
        Assert.Equal(successes, f.Quantity(MinerTwoBlueprint));
    }

    [Fact]
    public void InventionCancellationBurnsRunsAndDatacoresWithoutOutputAndCannotBeDelivered()
    {
        using var f = Create();
        long copy = MinerCopy(f, 1);
        var job = f.Store.Install(f.Pilot.PilotId, Invention(f, copy, decryptor: true)).Job;
        f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId);
        Assert.Throws<InvalidDataException>(() => f.Store.Blueprint(f.Pilot.PilotId, copy));
        Assert.Equal(18, f.Quantity(20424));
        Assert.Equal(19, f.Quantity(Decryptor));
        Assert.Equal(0, f.Quantity(MinerTwoBlueprint));
        Assert.Empty(f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId));
    }

    [Fact]
    public void RejectedSkillsQuotesMaterialsOwnersAndStationsLeaveInputsUnchanged()
    {
        using var f = Create();
        long copy = MinerCopy(f);
        var request = Invention(f, copy);
        foreach (
            var invalid in new[]
            {
                request with
                {
                    BlueprintId = Fixture.BlueprintId + MinerBlueprint,
                },
                request with
                {
                    Runs = 4,
                },
                request with
                {
                    BlueprintTypeId = 1130,
                },
                request with
                {
                    ProductTypeId = 1130,
                },
                request with
                {
                    FacilityId = 1,
                },
                request with
                {
                    QuotedSeconds = 1,
                },
                request with
                {
                    QuotedMaterials = new Dictionary<int, int> { [34] = 1 },
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, invalid));
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(90000002, request));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, s_inventionSkills[0])!.Points = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, request));
        Assert.Equal(20, f.Quantity(20424));
        Assert.Equal(20, f.Quantity(20413));
        Assert.Equal(3, f.Store.Blueprint(f.Pilot.PilotId, copy).RunsRemaining);
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, false));
    }

    [Fact]
    public void LaboratoryAvailabilityIsAdvertisedAndEnforcedForEachInstall()
    {
        using var f = Create();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"services\":[7,15,17]}}' WHERE Dataset='client/stationoperations' AND Key='14'"
            );
        }
        var facility = f.Store.Facility(f.Pilot.PilotId, f.Pilot.StationId);
        Assert.True(facility.Laboratory);
        Assert.False(facility.Manufacturing);
        var wire = Assert.IsType<Dictionary<string, object?>>(IndustryWire.Facility(facility, [], f.Store.ScienceProductCategories()));
        var activities = Assert.IsType<Dictionary<long, object?>>(wire["activities"]);
        Assert.Equal(new long[] { 3, 4, 5, 8 }, activities.Keys.Order().ToArray());
        Assert.Contains((int)InventoryCategory.Module, Assert.IsType<List<object?>>(Assert.IsType<object?[]>(activities[8])[3]));
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        Assert.NotNull(f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.ResearchMaterial)));
    }

    [Fact]
    public void NativeScienceRequestsRepliesAndNotificationsRoundTripThroughBlue()
    {
        using var f = Create();
        var pilot = f.Pilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), []),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        IndustryHandler handler = new(f.Store);
        var location = new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = (long)pilot.StationId,
                ["ownerID"] = pilot.PilotId,
                ["flagID"] = (long)InventoryFlag.Hangar,
            }
        );
        var request = new Dictionary<string, object?>
        {
            ["blueprintID"] = Fixture.BlueprintId,
            ["blueprintTypeID"] = 1130L,
            ["facilityID"] = (long)pilot.StationId,
            ["characterID"] = pilot.PilotId,
            ["activityID"] = (long)IndustryActivity.Copying,
            ["account"] = new object?[] { pilot.PilotId, 1000L },
            ["cost"] = 0d,
            ["tax"] = 0d,
            ["time"] = 480d,
            ["runs"] = 1L,
            ["licensedRuns"] = 2L,
            ["productTypeID"] = null,
            ["inputLocation"] = location,
            ["outputLocation"] = location,
            ["materials"] = new Dictionary<long, object?>(),
        };
        var installed = handler.CallWire(context, "industryManager", "InstallJob", [BlueMarshal.Decode(BlueMarshal.Encode(request))]);
        Assert.False(installed is PilotCallResult.Failure, (installed as PilotCallResult.Failure)?.Message);
        long id = Assert.IsType<long>(installed);
        Assert.Contains(context.Notifications, n => n.Name == "OnIndustryJob");
        var counts = Assert.IsType<Dictionary<long, object?>>(
            handler.CallWire(context, "industryManager", "GetJobCounts", [pilot.PilotId])
        );
        Assert.Equal(1, counts[(long)IndustryActivity.Copying]);
        Assert.Equal(0, counts[(long)IndustryActivity.Manufacturing]);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(480));
        context.Notifications.Clear();
        Assert.Null(handler.CallWire(context, "industryManager", "CompleteJob", [id, (long)pilot.SystemId]));
        Assert.Contains(context.Notifications, n => n.Name == "OnBlueprintsUpdated");
        Assert.Contains(context.Notifications, n => n.Name == "OnItemChange");
        var job = Assert.IsType<BlueInstance>(
            BlueMarshal.Decode(BlueMarshal.Encode(handler.CallWire(context, "industryManager", "GetJob", [id])))
        );
        var fields = Assert.IsType<Dictionary<string, object?>>(job.State);
        Assert.Equal((long)IndustryActivity.Copying, fields["activityID"]);
        Assert.Equal(2L, fields["licensedRuns"]);
        Assert.Equal(1L, fields["successfulRuns"]);
    }

    [Fact]
    public void CopyMetadataSurvivesInventoryMovementAndCannotMergeIntoOriginals()
    {
        using var f = Create();
        long id = MinerCopy(f);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        inventory.Move(f.Pilot.PilotId, [id], f.Pilot.StationId, f.Pilot.ShipId, (int)InventoryFlag.Cargo, null);
        inventory.Move(f.Pilot.PilotId, [id], f.Pilot.ShipId, f.Pilot.StationId, (int)InventoryFlag.Hangar, null);
        f.Db.Services.GetRequiredService<IPilotInventoryStacks>().StackAll(f.Pilot.PilotId, f.Pilot.StationId, (int)InventoryFlag.Hangar);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(3, f.Store.Blueprint(f.Pilot.PilotId, id).RunsRemaining);
        Assert.Equal(-1, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId + MinerBlueprint).RunsRemaining);
    }

    [Fact]
    public void ScienceSkillsReduceMatchingTimesAndLaboratorySkillsIncreaseTheSharedLimit()
    {
        using var f = Create();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId)!.Quantity = 3;
            foreach (
                var skill in new[]
                {
                    IndustrySkill.Science,
                    IndustrySkill.Research,
                    IndustrySkill.Metallurgy,
                    IndustrySkill.AdvancedIndustry,
                    IndustrySkill.LaboratoryOperation,
                    IndustrySkill.AdvancedLaboratoryOperation,
                }
            )
            {
                long points =
                    skill is IndustrySkill.Metallurgy or IndustrySkill.AdvancedIndustry ? 750
                    : skill == IndustrySkill.AdvancedLaboratoryOperation ? 2000
                    : 250;
                world.Add(
                    new PilotSkill
                    {
                        PilotId = f.Pilot.PilotId,
                        TypeId = (int)skill,
                        Points = points,
                    }
                );
            }
            world.SaveChanges();
        }
        var me = f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.ResearchMaterial) with { QuotedSeconds = 97 }).Job;
        var te = f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.ResearchTime) with { QuotedSeconds = 97 }).Job;
        var copy = f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.Copying, licensed: 1) with { QuotedSeconds = 221 }).Job;
        Assert.Equal(3, new[] { me.BlueprintId, te.BlueprintId, copy.BlueprintId }.Distinct().Count());
        var next = Request(f, IndustryActivity.Copying, licensed: 1) with
        {
            BlueprintId = Fixture.BlueprintId + MinerBlueprint,
            BlueprintTypeId = MinerBlueprint,
            QuotedSeconds = 663,
        };
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, next));
        f.Store.Cancel(f.Pilot.PilotId, me.Id, f.Pilot.SystemId);
        Assert.Equal(663, f.Store.Install(f.Pilot.PilotId, next).Job.DurationSeconds);
    }

    [Theory]
    [InlineData(IndustryActivity.ResearchMaterial, 1, 0, 96000, 2400, 117600)]
    [InlineData(IndustryActivity.ResearchTime, 1, 0, 96000, 2400, 117600)]
    [InlineData(IndustryActivity.Copying, 2, 3, 576000, 14400, 820800)]
    public void PaidScienceValidatesFeesAndRollsBackInsufficientFundsWithoutReservingBlueprints(
        IndustryActivity activity,
        int runs,
        int licensed,
        long cost,
        long tax,
        long total
    )
    {
        using var f = Create();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
            world.Set<SavedEconomicPolicy>().Single().DefaultManufacturingIndex = 0.1m;
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            foreach (int type in new[] { 34, 35 })
            {
                world.Add(new SavedAdjustedPrice { TypeId = type, PriceIsk = 10000 });
            }
            world.Add(new PilotWallet { PilotId = f.Pilot.PilotId, Cents = total - 1 });
            world.SaveChanges();
        }
        var request = Request(f, activity, runs, licensed) with { QuotedCharge = new(cost, tax, 0) };
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, request));
        Assert.False(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).Item.Singleton);
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedServicePayment>());
            Assert.Equal(total - 1, world.Set<PilotWallet>().Find(f.Pilot.PilotId)!.Cents);
            world.Set<PilotWallet>().Find(f.Pilot.PilotId)!.Cents = total * 2;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Store.Install(f.Pilot.PilotId, request with { QuotedCharge = new(cost + 100, tax, 0) })
        );
        var job = f.Store.Install(f.Pilot.PilotId, request).Job;
        Assert.Equal(total, job.CostCents);
        f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(total, saved.Set<PilotWallet>().Find(f.Pilot.PilotId)!.Cents);
        var payment = Assert.Single(saved.Set<SavedServicePayment>());
        Assert.Equal(-total, payment.AmountCents);
        Assert.Equal(
            activity == IndustryActivity.Copying ? WalletEntryType.Copying
                : activity == IndustryActivity.ResearchMaterial ? WalletEntryType.ResearchMaterial
                : WalletEntryType.ResearchTime,
            payment.Kind
        );
    }

    [Fact]
    public void MissingDatacoresRollBackEarlierConsumptionAndDoNotBurnCopyRuns()
    {
        using var f = Create();
        long copy = MinerCopy(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Single(i => i.TypeId == 20413).Quantity = 1;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, Invention(f, copy)));
        Assert.Equal(20, f.Quantity(20424));
        Assert.Equal(1, f.Quantity(20413));
        Assert.Equal(3, f.Store.Blueprint(f.Pilot.PilotId, copy).RunsRemaining);
        Assert.Null(f.Store.Blueprint(f.Pilot.PilotId, copy).JobId);
    }

    [Fact]
    public async Task CompetingCopyInstallsAndDeliveriesReserveAndOutputOnlyOnce()
    {
        using var f = Create();
        var request = Request(f, IndustryActivity.Copying, 2, 1);
        async Task<IndustryResult?> Install() =>
            await Task.Run(() =>
            {
                try
                {
                    return f.Store.Install(f.Pilot.PilotId, request);
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            });
        var job = Assert.Single(await Task.WhenAll(Install(), Install()), r => r is not null)!.Job;
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId)))
        );
        Assert.Single(results, r => r.Changes.Count > 0);
        Assert.Equal(3, f.Quantity(1130));
        Assert.Single(f.Store.Jobs(f.Pilot.PilotId, true));
    }

    [Fact]
    public void Version128UpgradePreservesManufacturingJobsAndDefaultsExistingBlueprintsToOriginals()
    {
        using var f = Create();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job;
        string inventory;
        using (WorldDbContext world = new(f.Db.Path))
        {
            inventory = JsonSerializer.Serialize(world.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id).ToArray());
            foreach (
                string column in new[]
                {
                    "Activity",
                    "LicensedRuns",
                    "BlueprintCopy",
                    "MaterialEfficiency",
                    "TimeEfficiency",
                    "OutputRuns",
                    "Probability",
                    "SuccessfulRuns",
                    "OptionalTypeId",
                }
            )
            {
                string sql = $"ALTER TABLE WorldIndustryJob DROP COLUMN {column}";
                world.Database.ExecuteSqlRaw(sql);
            }
            foreach (string table in new[] { "WorldItem", "WorldContractItem" })
            {
                foreach (string column in new[] { "MaterialEfficiency", "TimeEfficiency", "BlueprintRuns" })
                {
                    string sql = $"ALTER TABLE {table} DROP COLUMN {column}";
                    world.Database.ExecuteSqlRaw(sql);
                }
            }
            world.Database.ExecuteSqlRaw("PRAGMA user_version=128");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(inventory, JsonSerializer.Serialize(saved.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id).ToArray()));
        Assert.Equal(job, f.Store.Job(f.Pilot.PilotId, job.Id));
        Assert.Equal(-1, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).RunsRemaining);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Complete(f, job);
        Assert.Equal(100, f.Quantity(215));
    }

    [Fact]
    public void ContractsDescribeAndTransferCopiesWithTheirResearchAndRemainingRuns()
    {
        using var f = Create();
        long copy = MinerCopy(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(copy)!;
            item.MaterialEfficiency = 8;
            item.TimeEfficiency = 16;
            foreach (long pilot in new[] { f.Pilot.PilotId, 90000002L })
            {
                world.Add(new PilotWallet { PilotId = pilot, Cents = 100_000_000 });
            }
            world.SaveChanges();
        }
        var contracts = f.Db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(
            f.Pilot.PilotId,
            new ContractDraft(
                90000002,
                f.Pilot.StationId,
                1440,
                0,
                0,
                "Researched blueprint copy",
                "Three licensed runs",
                [new(copy, 1)],
                []
            )
        );
        var details = contracts.Details(f.Pilot.PilotId, id);
        var offered = Assert.Single(details.Items);
        Assert.Equal(3, offered.BlueprintRuns);
        Assert.Equal(8, offered.MaterialEfficiency);
        Assert.Equal(16, offered.TimeEfficiency);
        var row = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new ContractItemRow(offered))));
        var fields = Assert.IsType<Dictionary<string, object?>>(row.State);
        Assert.Equal(true, fields["copy"]);
        Assert.Equal(3L, fields["licensedProductionRunsRemaining"]);
        Assert.Equal(8L, fields["materialLevel"]);
        Assert.Equal(16L, fields["productivityLevel"]);
        contracts.Accept(90000002, id);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var received = f.Store.Blueprint(90000002, copy);
        Assert.Equal(3, received.RunsRemaining);
        Assert.Equal(8, received.MaterialEfficiency);
        Assert.Equal(16, received.TimeEfficiency);
    }
}
