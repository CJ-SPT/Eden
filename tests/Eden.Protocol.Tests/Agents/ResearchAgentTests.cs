using System.Text.Json.Nodes;
using Eden.Data.World.Agents;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class ResearchAgentTests
{
    private const int Field = 11452;
    private const int Datacore = 20424;
    private const long SecondAgent = 3019998;
    private const long StartingCents = 100_000_000;

    private static MissionFixture Create(bool live = false, int level = 1, int field = Field, bool mapped = true)
    {
        MissionFixture f = new(
            live: live,
            level: level,
            configure: catalog =>
            {
                MissionFixture.Agent(catalog, MissionFixture.AgentId, AgentType.Research, level, 18);
                MissionFixture.Agent(catalog, SecondAgent, AgentType.Research, 1, 18);
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO ItemGroup VALUES (333,'Datacores',4);
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                        (11452,'Mechanical Engineering',257),(20424,'Datacore - Mechanical Engineering',333),
                        (11447,'Laser Physics',257),(20413,'Datacore - Laser Physics',333),
                        (3402,'Science',257),(12179,'Research Project Management',257);
                    INSERT OR REPLACE INTO TypeAttribute VALUES (11452,275,1),(11447,275,1),
                        (3402,275,1),(12179,275,1),(20424,182,11452),(20413,182,11447);
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','20424','{{"published":1,"volume":0.1}}'),
                        ('client/types','20413','{{"published":1,"volume":0.1}}');
                    """
                );
                foreach (long agent in new[] { MissionFixture.AgentId, SecondAgent })
                {
                    string key = agent.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string raw = catalog
                        .Database.SqlQuery<string>(
                            $"SELECT Data AS Value FROM SourceRecord WHERE Dataset='client/npccharacters' AND Key={key}"
                        )
                        .Single();
                    var data = JsonNode.Parse(raw)!;
                    data["skills"] = new JsonArray(new JsonObject { ["typeID"] = field });
                    catalog.Database.ExecuteSql(
                        $"UPDATE SourceRecord SET Data={data.ToJsonString()} WHERE Dataset='client/npccharacters' AND Key={key}"
                    );
                }
                if (!mapped)
                {
                    catalog.Database.ExecuteSqlRaw("DELETE FROM TypeAttribute WHERE ItemTypeId=20424 AND AttributeId=182");
                }
            }
        );
        using WorldDbContext world = new(f.Db.Path);
        foreach (int type in new[] { ResearchRules.Science, field })
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = MissionFixture.PilotId,
                    TypeId = type,
                    Points = 256_000,
                }
            );
        }
        var wallet =
            world.Set<PilotWallet>().Find(MissionFixture.PilotId) ?? world.Add(new PilotWallet { PilotId = MissionFixture.PilotId }).Entity;
        wallet.Cents = StartingCents;
        world.SaveChanges();
        return f;
    }

    private static IPilotResearch Store(MissionFixture f) => f.Services.GetRequiredService<IPilotResearch>();

    private static ResearchProject Start(MissionFixture f) => Store(f).StartResearch(MissionFixture.PilotId, MissionFixture.AgentId, Field);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccruesOfflineAndPurchasePersistsPointsWalletInventoryAndJournal(bool resident)
    {
        using var f = Create(live: resident);
        var started = Start(f);
        Assert.Equal(43.2, started.PointsPerDay, 8);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        var accrued = Assert.Single(Store(f).Research(MissionFixture.PilotId));
        Assert.Equal(216, accrued.Points, 8);
        var purchase = Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, started.Id, 2);
        Assert.Equal(StartingCents - 2 * ResearchRules.CentsPerDatacore, purchase.BalanceCents);
        Assert.Equal(Datacore, purchase.Item.Item.TypeId);
        Assert.Equal(2, purchase.Item.Item.Quantity);
        Assert.Equal((int)InventoryFlag.Hangar, purchase.Item.Item.FlagId);
        Assert.Equal(MissionFixture.StationId, purchase.Item.Item.LocationId);
        using (WorldDbContext reopened = new(f.Db.Path))
        {
            Assert.Equal(16, reopened.Set<SavedResearchProject>().Single().Points, 8);
            Assert.Equal(purchase.BalanceCents, reopened.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents);
            Assert.Equal(2, reopened.Set<OwnedItem>().Single(i => i.TypeId == Datacore).Quantity);
            var payment = Assert.Single(reopened.Set<SavedServicePayment>().Where(p => p.Kind == WalletEntryType.DatacorePurchase));
            Assert.Equal(-2 * ResearchRules.CentsPerDatacore, payment.AmountCents);
            Assert.Equal(MissionFixture.AgentId, payment.CounterpartyId);
        }
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        var research = Assert.IsType<List<object?>>(journal[1]);
        var row = Assert.IsType<object?[]>(Assert.Single(research));
        Assert.Equal(7, row.Length);
        Assert.Equal(MissionFixture.AgentId, row[0]);
        Assert.Equal((long)Field, row[1]);
        Assert.Equal(16, Assert.IsType<double>(row[3]), 8);
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            f.StopResident();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using var reopenedStore = Eden.Server.ServerServices.Build(new(f.Db.Path), f.Clock);
        Assert.Equal(59.2, Assert.Single(reopenedStore.GetRequiredService<IPilotResearch>().Research(MissionFixture.PilotId)).Points, 8);
    }

    [Fact]
    public void SkillStandingStationAndCapacityGatesLeaveProjectsUnchanged()
    {
        using var f = Create();
        var store = Store(f);
        Assert.Throws<InvalidDataException>(() => store.StartResearch(MissionFixture.PilotId, MissionFixture.AgentId, 3402));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(MissionFixture.PilotId, ResearchRules.Science)!.Points = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Start(f));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(MissionFixture.PilotId, ResearchRules.Science)!.Points = 256_000;
            world.SaveChanges();
        }
        f.Dock(MissionFixture.DestinationId);
        Assert.Throws<InvalidDataException>(() => Start(f));
        f.Dock(MissionFixture.StationId);
        var project = Start(f);
        Assert.Throws<InvalidDataException>(() => Start(f));
        Assert.Throws<InvalidDataException>(() => store.StartResearch(MissionFixture.PilotId, SecondAgent, Field));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = MissionFixture.PilotId,
                    TypeId = ResearchRules.ProjectManagement,
                    Points = 250,
                }
            );
            world.SaveChanges();
        }
        _ = store.StartResearch(MissionFixture.PilotId, SecondAgent, Field);
        Assert.Equal(2, store.Research(MissionFixture.PilotId).Count);
        Assert.Equal(project.Id, store.Research(MissionFixture.PilotId, MissionFixture.AgentId).Single().Id);
        Assert.False(ResearchRules.Access(4, 8, 2.9, 9, true).Allowed);
        Assert.True(ResearchRules.Access(4, 0, 3, 5, true).Allowed);
        Assert.False(ResearchRules.Access(1, -2, 0, 0, true).Allowed);
        Assert.False(ResearchRules.Access(1, 0, 0, 0, false).Allowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void InvalidPurchasesRollBackAllBalances(int quantity)
    {
        using var f = Create(live: true);
        var project = Start(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        Assert.Throws<InvalidDataException>(() =>
            Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, quantity)
        );
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(0, world.Set<SavedResearchProject>().Single().Points);
        Assert.Equal(StartingCents, world.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == Datacore));
        Assert.Empty(world.Set<SavedServicePayment>().Where(p => p.Kind == WalletEntryType.DatacorePurchase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsufficientFundsWrongStationAndStaleProjectsCannotDeliverDatacores(bool resident)
    {
        using var f = Create(live: resident);
        var project = Start(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, 1));
        f.Dock(MissionFixture.DestinationId);
        Assert.Throws<InvalidDataException>(() => Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, 1));
        f.Dock(MissionFixture.StationId);
        Store(f).CancelResearch(MissionFixture.PilotId, MissionFixture.AgentId, project.Id);
        var replacement = Start(f);
        Assert.NotEqual(project.Id, replacement.Id);
        Assert.Equal(0, replacement.Points);
        Assert.Throws<InvalidDataException>(() => Store(f).CancelResearch(MissionFixture.PilotId, MissionFixture.AgentId, project.Id));
        Assert.Throws<InvalidDataException>(() => Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, 1));
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            f.StopResident();
        }
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<OwnedItem>().Where(i => i.TypeId == Datacore));
        Assert.Equal(replacement.Id, check.Set<SavedResearchProject>().Single().Id);
    }

    [Fact]
    public void NativeDialogsSelectPurchaseCancelAndNotify()
    {
        using var f = Create();
        string service = $"@agent:{MissionFixture.AgentId}";
        var start = Assert.IsType<PilotCallResult.ClientCall>(f.Call(service, "DoAction", [12L]));
        Assert.Equal("SingleChoiceBox", start.Dialog.Method);
        _ = MissionFixture.Reply(start.Dialog.Complete(new object?[] { true, "radioboxOption1Selected" }));
        Assert.Single(Store(f).Research(MissionFixture.PilotId));
        Assert.Contains(f.Context.Notifications, n => n is ResearchChanged { Action: "research_started" });
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        var buy = Assert.IsType<PilotCallResult.ClientCall>(f.Call(service, "DoAction", [14L]));
        Assert.Equal("GetQuantity", buy.Dialog.Method);
        _ = MissionFixture.Reply(buy.Dialog.Complete(2L));
        Assert.Contains(f.Context.Notifications, n => n is ResearchChanged { Action: "research_update_ppd" });
        Assert.Equal(16, Store(f).Research(MissionFixture.PilotId).Single().Points, 8);
        var cancel = Assert.IsType<PilotCallResult.ClientCall>(f.Call(service, "DoAction", [13L]));
        _ = MissionFixture.Reply(cancel.Dialog.Complete(false));
        Assert.Single(Store(f).Research(MissionFixture.PilotId));
        _ = MissionFixture.Reply(cancel.Dialog.Complete(true));
        Assert.Empty(Store(f).Research(MissionFixture.PilotId));
        Assert.IsType<PilotCallResult.Failure>(buy.Dialog.Complete(1L));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSessionDialogResumesOriginalBoundCallOnceWithoutBlockingOtherCalls(bool wrapped)
    {
        using var f = Create();
        using var scope = f.Services.CreateScope();
        List<BlueInstance> packets = [];
        PilotSessionOutput output = new(
            f.Context,
            1000001,
            42,
            packet =>
            {
                packets.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionCalls calls = new(
            f.Context,
            output,
            scope.ServiceProvider.GetRequiredService<PilotCallRouter>(),
            scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SkillHandler>(),
            f.Clock,
            _ => { },
            1000001,
            42
        );
        var original = PilotServicesTests.Request(
            "agentMgr",
            "MachoBindObject",
            [MissionFixture.AgentId, new object?[] { "DoAction", new object?[] { 12L }, new Dictionary<string, object?>() }]
        );
        await calls.ProcessAsync(original);
        var prompt = Assert.Single(packets, p => p.Name.EndsWith(".CallReq", StringComparison.Ordinal));
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        var outbound = IncomingMachoPacket.Read(prompt);
        var request = PilotCallRequest.Read(outbound.CallBytes());
        Assert.Equal("SingleChoiceBox", request.Method);
        Assert.Empty(Store(f).Research(MissionFixture.PilotId));
        packets.Clear();
        await calls.ProcessAsync(PilotServicesTests.Request("agentMgr", "GetAgents", []));
        Assert.Single(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        packets.Clear();
        var response = outbound.CallSucceeded(new ChoiceAnswer());
        if (wrapped)
        {
            var fields = Assert.IsType<object?[]>(response.State);
            fields[4] = new object?[] { new object?[] { 0L, new BlueStream(BlueMarshal.Encode(new ChoiceAnswer())) } };
        }
        var wireResponse = BlueMarshal.Decode(BlueMarshal.Encode(response));
        await calls.ProcessAsync(wireResponse);
        Assert.Single(Store(f).Research(MissionFixture.PilotId));
        var completed = IncomingMachoPacket.Read(Assert.Single(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)));
        Assert.Equal(BlueMarshal.Encode(original.State is object?[] state ? state[1] : null), BlueMarshal.Encode(completed.Fields[2]));
        // Object references are outbound-only codec values; compare their encoded
        // binding envelope rather than using the inert request decoder on tag 0x19.
        var expected = new BoundPilotReply(
            f.Context.Reference($"@agent:{MissionFixture.AgentId}", PilotBindingLifetime.Pilot),
            new ResearchConversation(Assert.Single(Store(f).Research(MissionFixture.PilotId)), "Research has started.")
        );
        Assert.Equal(BlueMarshal.Encode(expected), Assert.IsType<byte[]>(Assert.IsType<object?[]>(completed.Fields[4])[0]));
        packets.Clear();
        await calls.ProcessAsync(wireResponse);
        Assert.Empty(packets);
        Assert.Single(Store(f).Research(MissionFixture.PilotId));
    }

    private sealed record ChoiceAnswer : BlueModel
    {
        public override object ToBlue() => new object?[] { true, "radioboxOption1Selected" };
    }

    [Fact]
    public void ShowInfoExposesOfferedFieldsAndAccruedResearchWithoutChangingSavedBalance()
    {
        using var f = Create();
        _ = Start(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        var details = Assert.IsType<BlueInstance>(
            MissionFixture.Reply(f.Call($"@agent:{MissionFixture.AgentId}", "GetInfoServiceDetails", []))
        );
        var fields = Assert.IsType<Dictionary<string, object?>>(details.State);
        Assert.Null(fields["incompatible"]);
        var service = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(fields["services"])));
        var data = Assert.IsType<Dictionary<string, object?>>(service.State);
        Assert.Equal("research", BlueMarshal.Text(data["agentServiceType"]));
        var skills = Assert.IsType<List<object?>>(data["skills"]);
        Assert.Equal((long)Field, Assert.IsType<object?[]>(Assert.Single(skills))[0]);
        var research = Assert.IsType<Dictionary<string, object?>>(data["researchData"]);
        Assert.Equal(43.2, Assert.IsType<double>(research["points"]), 8);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(0, world.Set<SavedResearchProject>().Single().Points);
    }

    [Fact]
    public void PreviousSchemaUpgradesIdempotentlyWithoutInventingProjects()
    {
        using var f = Create();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldResearchProject; PRAGMA user_version=139");
        }
        var database = new WorldDatabase(f.Db.Path);
        WorldInitialization.Initialize(database);
        WorldInitialization.Initialize(database);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(check.Set<SavedResearchProject>());
        Assert.Equal(StartingCents, check.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents);
    }

    [Fact]
    public async Task ConcurrentPurchasesCannotSpendTheSameResearchPointsTwice()
    {
        using var f = Create(live: true);
        var project = Start(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(index =>
                    Task.Run(() =>
                    {
                        try
                        {
                            _ = Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, 2);
                            return true;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(results, success => success);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(2, check.Set<OwnedItem>().Single(i => i.TypeId == Datacore).Quantity);
        Assert.Equal(16, check.Set<SavedResearchProject>().Single().Points, 8);
        Assert.Equal(StartingCents - 2 * ResearchRules.CentsPerDatacore, check.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents);
    }

    [Theory]
    [InlineData(11452, 20424)]
    [InlineData(11447, 20413)]
    public void DatacoreOutputComesFromTheSelectedCatalogField(int field, int type)
    {
        using var f = Create(field: field);
        var project = Store(f).StartResearch(MissionFixture.PilotId, MissionFixture.AgentId, field);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(3));
        var purchase = Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, 1);
        Assert.Equal(type, purchase.Item.Item.TypeId);
        Assert.Equal(field, Store(f).Research(MissionFixture.PilotId).Single().SkillId);
    }

    [Fact]
    public void MissingDatacoreMappingDoesNotInventOutputOrSpendCurrency()
    {
        using var f = Create(mapped: false);
        var project = Start(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(3));
        Assert.Throws<InvalidDataException>(() => Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, 1));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(StartingCents, world.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents);
        Assert.Equal(0, world.Set<SavedResearchProject>().Single().Points);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == Datacore));
    }

    [Fact]
    public void UpdatedRatesApplyProspectivelyAndClockRollbackCannotReplayAccrual()
    {
        using var f = Create();
        _ = Start(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var skill = world.Set<PilotSkill>().Find(MissionFixture.PilotId, (int)MissionSocialSkill.Negotiation);
            if (skill is null)
            {
                skill = world.Add(new PilotSkill { PilotId = MissionFixture.PilotId, TypeId = (int)MissionSocialSkill.Negotiation }).Entity;
            }
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
            skill.Points = TrainedSkill.PointsForLevel(catalog.FittingAttributes(skill.TypeId)[DogmaAttributeId.SkillTimeConstant], 5);
            world.SaveChanges();
        }
        var updated = Assert.Single(Store(f).Research(MissionFixture.PilotId));
        Assert.Equal(43.2, updated.Points, 8);
        Assert.Equal(52.2, updated.PointsPerDay, 8);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Equal(95.4, Store(f).Research(MissionFixture.PilotId).Single().Points, 8);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(-1));
        Assert.Equal(95.4, Store(f).Research(MissionFixture.PilotId).Single().Points, 8);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Equal(95.4, Store(f).Research(MissionFixture.PilotId).Single().Points, 8);
    }

    [Fact]
    public void OutputStackOverflowRollsBackPointsAndWallet()
    {
        using var f = Create();
        var project = Start(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(3));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = 80000999,
                    OwnerId = MissionFixture.PilotId,
                    LocationId = MissionFixture.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = Datacore,
                    Quantity = int.MaxValue,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<OverflowException>(() => Store(f).BuyDatacores(MissionFixture.PilotId, MissionFixture.AgentId, project.Id, 1));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(0, check.Set<SavedResearchProject>().Single().Points);
        Assert.Equal(StartingCents, check.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents);
        Assert.Equal(int.MaxValue, check.Set<OwnedItem>().Single(i => i.TypeId == Datacore).Quantity);
    }

    [Theory]
    [InlineData(1, 5, 0, 0, 43.2)]
    [InlineData(4, 5, 5, 5, 121.5)]
    public void RatesAndClockRollbackPreserveFractionalPoints(int agent, int skill, int negotiation, double standing, double expected)
    {
        double rate = ResearchRules.PointsPerDay(agent, skill, negotiation, standing);
        Assert.Equal(expected, rate, 8);
        Assert.Equal(3.5, ResearchRules.Accrue(3.5, rate, 100, 99));
        Assert.Equal(3.5 + rate / 2, ResearchRules.Accrue(3.5, rate, 100, 100 + TimeSpan.TicksPerDay / 2), 8);
        Assert.Equal(ResearchRules.CentsPerDatacore, ResearchRules.PurchaseCost(100, 1));
        Assert.Throws<InvalidDataException>(() => ResearchRules.PurchaseCost(99.999, 1));
        Assert.Throws<InvalidDataException>(() => ResearchRules.PointsPerDay(0, skill, negotiation, standing));
    }
}
