using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Data.World.Redemption;
using Eden.Data.World.Skills;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Redemption;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Inventory;

public sealed class DeveloperToolsTests
{
    private const long Pilot = 90000001;
    private const long Account = 1000001;
    private const int SpaceshipCommand = 3327;
    private const int NewSkill = 3300;

    [Theory]
    [InlineData(PilotRole.Developer, "Available commands: ['create', 'createitem', 'giveskill', 'load', 'tr']")]
    [InlineData(PilotRole.WorldMod, "Available commands: ['create', 'createitem', 'tr']")]
    [InlineData(PilotRole.GameMasterHigh, "Available commands: ['giveskill', 'load']")]
    [InlineData(PilotRole.GameMasterLow, "Available commands: ['load', 'tr']")]
    [InlineData(PilotRole.QualityAssurance, "Available commands: []")]
    [InlineData(PilotRole.Player, "Available commands: []")]
    public void SlashDiscoveryReturnsNativeRoleFilteredCommandList(PilotRole roles, string reason)
    {
        using Fixture f = new(roles);
        string before = f.Saved();
        var result = Assert.IsType<PilotCallResult.NamedFailure>(f.Client.Call("SlashCmd", ["/"], "slash"));
        Assert.Equal("SlashError", result.Name);
        var values = Assert.IsType<Dictionary<string, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(result.Values)));
        Assert.Equal(reason, BlueMarshal.Text(values["reason"]));
        Assert.Equal(before, f.Saved());
        Assert.Empty(f.Redemption.Read(Account));
    }

    // Representative build-3662168 catalog rows, including every ordinary injector.
    public static TheoryData<int, int, int, double> PhysicalTypes =>
        new()
        {
            { 40520, 1739, 5, 1 },
            { 43630, 1739, 5, 1 },
            { 45635, 1739, 5, 1 },
            { 46375, 1739, 5, 1 },
            { 12235, 365, 23, 8000 },
            { 28320, 881, 24, 1 },
            { 21573, 731, 35, 1 },
            { 2254, 1027, 41, 1000 },
            { 19658, 976, 63, 1 },
            { 34599, 1950, 91, 0.01 },
            { 57203, 4091, 2100, 1 },
            { 72964, 4471, 2118, 0 },
            { 81143, 4729, 2143, 0.01 },
            { 23, 12, 2, 1000 },
        };

    [Theory]
    [MemberData(nameof(PhysicalTypes))]
    public void ExpandedBrowserTypesCreateRealInventoryStacks(int type, int group, int category, double volume)
    {
        using Fixture f = new();
        f.AddType(type, group, category, volume);
        long id = f.Create($"/create {type} 2");
        var item = Assert.Single(f.Inventory.List(Pilot, f.Client.Context.Pilot.StationId), i => i.Id == id);
        Assert.Equal(type, item.TypeId);
        Assert.Equal(category, item.CategoryId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal((int)InventoryFlag.Hangar, item.FlagId);
        Assert.Single(f.Client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.Empty(f.Redemption.Read(Account));
    }

    [Theory]
    [InlineData("/create", false)]
    [InlineData("/load me", true)]
    public void SkillPointTokensGoToRedemptionAndClaimExactlyOnce(string command, bool inSpace)
    {
        using Fixture f = new();
        if (inSpace)
        {
            f.Db.Store.SetPosition(f.Client.Context.Pilot.ShipId, Eden.Gameplay.Flight.SpacePosition.Zero);
            f.Client.Refresh();
        }
        f.Client.Context.Notifications.Clear();
        long id = f.Create($"{command} 63340 3");
        var token = Assert.Single(f.Redemption.Read(Account));
        Assert.Equal(id, token.Id);
        Assert.Equal(63340, token.TypeId);
        Assert.Equal(3, token.Quantity);
        Assert.Null(token.StationId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.DoesNotContain(world.Set<OwnedItem>(), i => i.TypeId == token.TypeId);
        }
        Assert.Single(f.Client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.DoesNotContain(f.Client.Context.Notifications, n => n.Name == "OnItemChange");
        var result = f.Redemption.Claim(Account, Pilot, [new(id, token.TypeId)], false);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Items);
        Assert.Equal(156_000, result.SkillPoints);
        Assert.Equal(156_000, f.Points.Balance(Pilot));
        Assert.Single(f.Redemption.Claim(Account, Pilot, [new(id, token.TypeId)], false).Errors);
        Assert.Equal(156_000, f.Points.Balance(Pilot));
    }

    [Theory]
    [InlineData(52996)]
    [InlineData(73277)]
    public void CurrencyTokensAlsoUseTheRedeemingQueue(int type)
    {
        using Fixture f = new();
        f.AddType(type, 526, 17, 0);
        f.Create($"/load me {type} 7");
        var token = Assert.Single(f.Redemption.Read(Account));
        Assert.Equal(type, token.TypeId);
        Assert.Equal(7, token.Quantity);
        Assert.Empty(f.Inventory.List(Pilot, f.Client.Context.Pilot.ShipId));
    }

    [Fact]
    public void OrdinaryInjectorsCanBeCreatedAndConsumedThroughTheNormalSkillService()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, Account, PilotRole.Developer);
        client.Refresh();
        long item = Assert.IsType<long>(WireAssertions.Materialize(client.Call("SlashCmd", ["/create 40520 2"], "slash")));
        Assert.Null(WireAssertions.Materialize(client.Call("InjectSkillpoints", [item, 1L], "@skills")));
        Assert.Equal(500_000, db.Services.GetRequiredService<IPilotFreeSkillPoints>().Balance(Pilot));
        Assert.Equal(
            1,
            Assert
                .Single(db.Services.GetRequiredService<IPilotInventory>().List(Pilot, client.Context.Pilot.StationId), i => i.Id == item)
                .Quantity
        );
    }

    [Fact]
    public void SkillToolGrantsAndLearnsSkillsWithoutSpendingThePoolOrDowngradingProgress()
    {
        using Fixture f = new();
        f.Call("/giveskill me 3300 4");
        Assert.Equal(4, Assert.Single(f.Reader.ReadSkills(Pilot), s => s.TypeId == NewSkill).Level);
        Assert.Equal(0, f.Points.Balance(Pilot));
        Assert.Contains(f.Client.Context.Notifications, n => n.Name == "OnServerSkillsChanged");
        Assert.Contains(f.Client.Context.Notifications, n => n.Name == "OnServerBrainUpdated");
        Assert.Contains(f.Client.Context.Notifications, n => n.Name == "OnNewSkillQueueSaved");
        f.Client.Context.Notifications.Clear();
        f.Call($"/GiVeSkIlL {Pilot} {NewSkill} 1");
        Assert.Equal(4, Assert.Single(f.Reader.ReadSkills(Pilot), s => s.TypeId == NewSkill).Level);
        Assert.DoesNotContain(f.Client.Context.Notifications, n => n.Name == "OnServerSkillsChanged");
        Assert.Single(f.Training.History(Pilot, 100), h => h.TypeId == NewSkill && h.EventId == 39);
    }

    [Fact]
    public void ZeroLevelProfilesInjectTheSkillAndRemoteRecipientsRefreshTheirBrain()
    {
        using Fixture f = new();
        using CourierServiceTests.Client other = new(f.Db, f.Clock, 1000002);
        other.Refresh();
        other.Context.Notifications.Clear();
        long recipient = other.Context.Pilot.PilotId;
        f.Call($"/giveskill {recipient} 3300 0");
        Assert.Equal(0, Assert.Single(f.Reader.ReadSkills(recipient), s => s.TypeId == NewSkill).Points);
        f.Call($"/giveskill {recipient} 3300 3");
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
        other.Advance();
        Assert.Equal(3, Assert.Single(f.Reader.ReadSkills(recipient), s => s.TypeId == NewSkill).Level);
        Assert.Contains(other.Context.Notifications, n => n.Name == "OnServerSkillsChanged");
        Assert.Contains(other.Context.Notifications, n => n.Name == "OnServerBrainUpdated");
        Assert.DoesNotContain(f.Reader.ReadSkills(Pilot), s => s.TypeId == NewSkill);
        Assert.Equal(0, f.Points.Balance(recipient));
    }

    [Fact]
    public void SkillGrantCommitFailureRollsBackElapsedProgressQueueHistoryAndNewSkills()
    {
        using Fixture f = new();
        f.Training.Save(Pilot, [new(SpaceshipCommand, 1), new(SpaceshipCommand, 2)], true);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        string before = f.Saved();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectDeveloperSkill BEFORE INSERT ON WorldPilotSkill
                WHEN NEW.TypeId = 3300 BEGIN SELECT RAISE(ABORT, 'grant test rollback'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Db.Services.GetRequiredService<IPilotSkillGrants>().Grant(Pilot, new(NewSkill, 3)));
        Assert.Equal(before, f.Saved());
    }

    [Fact]
    public void RewardCreationRequiresAuthorizationAndSelectedPilot()
    {
        using Fixture f = new(PilotRole.Player);
        Assert.IsType<PilotCallResult.Failure>(f.Client.Call("SlashCmd", ["/create 63340 1"], "slash"));
        Assert.Empty(f.Redemption.Read(Account));
        using CourierServiceTests.Client unselected = new(f.Db, f.Clock, Account, PilotRole.Developer);
        Assert.Equal(PilotCallResult.Unsupported, unselected.Call("SlashCmd", ["/giveskill me 3300 5"], "slash"));
        Assert.Equal(PilotCallResult.Unsupported, unselected.Call("SlashCmd", ["/load me 63340 1"], "slash"));
        Assert.Empty(f.Redemption.Read(Account));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillGrantsRemoveSatisfiedLevelsAndPreserveActiveOrPausedTraining(bool active)
    {
        using Fixture f = new();
        f.Training.Save(Pilot, [new(SpaceshipCommand, 1), new(SpaceshipCommand, 2), new(SpaceshipCommand, 3)], active);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        f.Call("/giveskill me 3327 2");
        var after = Assert.Single(f.Training.Advance(Pilot).Queue);
        Assert.Equal(3, after.Target.Level);
        Assert.Equal(TrainedSkill.PointsForLevel(1, 2), after.StartPoints);
        Assert.Equal(active, after.StartTime.HasValue);
        if (active)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
            Assert.Empty(f.Training.Advance(Pilot).Queue);
            Assert.Equal(3, Assert.Single(f.Reader.ReadSkills(Pilot), s => s.TypeId == SpaceshipCommand).Level);
            Assert.Empty(f.Training.Advance(Pilot).ChangedSkills);
        }
    }

    [Theory]
    [InlineData("/giveskill me 3300 6")]
    [InlineData("/giveskill me 3300 -1")]
    [InlineData("/giveskill me 3300 1 extra")]
    [InlineData("/giveskill other 3300 1")]
    [InlineData("/giveskill 99999999 3300 1")]
    [InlineData("/giveskill me 3651 1")]
    [InlineData("/giveskill me 2147483647 1")]
    public void InvalidSkillGrantsLeaveSavedStateUnchanged(string command)
    {
        using Fixture f = new();
        string before = f.Saved();
        Assert.IsType<PilotCallResult.Failure>(f.Client.Call("SlashCmd", [command], "slash"));
        Assert.Equal(before, f.Saved());
        Assert.DoesNotContain(f.Client.Context.Notifications, n => n.Name == "OnServerSkillsChanged");
    }

    [Theory]
    [InlineData(PilotRole.Player)]
    [InlineData(PilotRole.QualityAssurance)]
    [InlineData(PilotRole.GameMasterLow)]
    [InlineData(PilotRole.WorldMod)]
    public void SkillToolRequiresHighGameMasterAuthority(PilotRole roles)
    {
        using Fixture f = new(roles);
        string before = f.Saved();
        Assert.IsType<PilotCallResult.Failure>(f.Client.Call("SlashCmd", ["/giveskill me 3300 5"], "slash"));
        Assert.Equal(before, f.Saved());
    }

    [Fact]
    public void PendingRewardsAndGrantedSkillsSurviveRestartAndOldMisplacedRewardsRecoverOnce()
    {
        using Fixture f = new();
        f.Create("/create 63340 2");
        f.Call("/giveskill me 3300 4");
        long ship = f.Client.Context.Pilot.ShipId;
        long station = f.Client.Context.Pilot.StationId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = 2999999999990,
                    OwnerId = Pilot,
                    LocationId = station,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = 63340,
                    Quantity = 3,
                }
            );
            world.Add(
                new OwnedItem
                {
                    Id = 2999999999991,
                    OwnerId = Pilot,
                    LocationId = ship,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = 63340,
                    Quantity = 4,
                }
            );
            world.Add(
                new OwnedItem
                {
                    Id = 2999999999992,
                    OwnerId = Pilot,
                    LocationId = 1,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = 63340,
                    Quantity = 5,
                }
            );
            world.SaveChanges();
        }
        f.Client.Dispose();
        f.Db.Services.Dispose();
        WorldInitialization.Open(f.Db.Path);
        WorldInitialization.Open(f.Db.Path);
        using var reopened = ServerServices.Build(new(f.Db.Path));
        var tokens = reopened.GetRequiredService<IAccountRedemption>().Read(Account);
        Assert.Equal(new[] { 2, 3, 4 }, tokens.Select(t => t.Quantity).Order().ToArray());
        Assert.Equal(4, Assert.Single(reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(Pilot), s => s.TypeId == NewSkill).Level);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(5, Assert.Single(check.Set<OwnedItem>(), i => i.TypeId == 63340).Quantity);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 250)]
    [InlineData(5, 256000)]
    public void TypedGrantRulesUseRankAndNeverRemovePoints(int level, long threshold)
    {
        Assert.Equal(threshold * 2, SkillGrantRules.Points(new(NewSkill, level), 2, 0));
        Assert.Equal(600000, SkillGrantRules.Points(new(NewSkill, level), 2, 600000));
        Assert.Throws<InvalidDataException>(() => SkillGrantRules.Points(new(NewSkill, level), double.NaN, 0));
        Assert.Throws<InvalidDataException>(() => SkillGrantRules.Points(new(NewSkill, 6), 1, 0));
    }

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = CalendarTestData.Clock();
        public TestWorld Db { get; }
        public CourierServiceTests.Client Client { get; }
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        public IAccountRedemption Redemption => Db.Services.GetRequiredService<IAccountRedemption>();
        public IPilotFreeSkillPoints Points => Db.Services.GetRequiredService<IPilotFreeSkillPoints>();
        public IPilotTraining Training => Db.Services.GetRequiredService<IPilotTraining>();
        public IPilotSkillReader Reader => Db.Services.GetRequiredService<IPilotSkillReader>();

        public Fixture(PilotRole roles = PilotRole.Developer)
        {
            Db = FreePointTestData.Create(Clock);
            AddType(NewSkill, 257, 16, 0.01);
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data)
                    VALUES ('client/types','3327','{{"published":1,"volume":0.01}}');
                    INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value)
                    VALUES (3300,275,1),(3300,180,167),(3300,181,168);
                    """
                );
            }
            Client = new(Db, Clock, Account, roles);
            Client.Refresh();
            Client.Context.Notifications.Clear();
        }

        public void AddType(int type, int group, int category, double volume)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemCategory VALUES ({category},{category.ToString()})");
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemGroup VALUES ({group},{group.ToString()},{category})");
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES ({type},{type.ToString()},{group})");
            string data = JsonSerializer.Serialize(new { published = 1, volume });
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{type.ToString()},{data})");
        }

        public PilotCallResult Call(string command) => Assert.IsType<PilotCallResult.Success>(Client.Call("SlashCmd", [command], "slash"));

        public long Create(string command) => Assert.IsType<long>(WireAssertions.Materialize(Call(command)));

        public string Saved()
        {
            using WorldDbContext world = new(Db.Path);
            return JsonSerializer.Serialize(
                new
                {
                    Skills = world.Set<PilotSkill>().OrderBy(s => s.TypeId).ToArray(),
                    Queue = world.Set<QueuedLevel>().OrderBy(q => q.Position).ToArray(),
                    Points = world.Set<SavedFreeSkillPoints>().ToArray(),
                    History = world.Set<TrainingLog>().OrderBy(h => h.Id).ToArray(),
                }
            );
        }

        public void Dispose()
        {
            Client.Dispose();
            Db.Dispose();
        }
    }
}
