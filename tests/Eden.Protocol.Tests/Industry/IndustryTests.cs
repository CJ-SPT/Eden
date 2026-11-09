using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Industry;

public sealed class IndustryTests
{
    private sealed class Fixture : IDisposable
    {
        public const long BlueprintId = InventoryDefinitions.FirstOwnedItemId + 100;
        public TestWorld Db { get; } = new();
        public Clock Clock { get; } = new(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public PilotIndustryStore Store => new(new(Db.Path), Clock);

        public Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemCategory VALUES (9,'Blueprint'),(8,'Charge'),(4,'Material');
                    INSERT INTO ItemGroup VALUES (167,'Hybrid Charge Blueprint',9),(85,'Hybrid Charge',8),(18,'Mineral',4);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (1130,'Iron Charge S Blueprint',167),(215,'Iron Charge S',85),
                        (34,'Tritanium',18),(35,'Pyerite',18),(3380,'Industry',257),(3387,'Mass Production',257),
                        (3388,'Advanced Industry',257),(24625,'Advanced Mass Production',257);
                    INSERT INTO DogmaAttribute(Id,Name) VALUES (128,'chargeSize'),(422,'techLevel'),(440,'manufacturingTimeBonus'),
                        (450,'manufacturingSlotBonus'),(1961,'advancedIndustrySkillIndustryJobTimeBonus');
                    INSERT INTO TypeAttribute VALUES (215,128,1),(215,422,1),(3380,275,1),(3380,440,-4),(3387,275,2),(3387,450,1),
                        (3388,275,3),(3388,1961,-3),(24625,275,8),(24625,450,1);
                    INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7,14,17]}}'),
                        ('client/types','34','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                            ('client/types','35','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                        ('client/types','215','{{"published":1,"marketGroupID":1,"volume":0.0025}}'),
                            ('client/types','1130','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                        ('client/blueprints','"1130"','{{"activities":{{"manufacturing":{{"skills":[{{"typeID":3380,"level":1}}],' ||
                            '"materials":[{{"typeID":34,"quantity":27}},{{"typeID":35,"quantity":21}}],' ||
                            '"products":[{{"typeID":215,"quantity":100}}],"time":300}}}},"blueprintTypeID":1130,' ||
                            '"maxProductionLimit":600}}');
                    """
                );
            }
            using WorldDbContext world = new(Db.Path);
            world.Add(
                new PilotSkill
                {
                    PilotId = Pilot.PilotId,
                    TypeId = (int)IndustrySkill.Industry,
                    Points = 250,
                }
            );
            foreach (
                var (id, type, quantity) in new[]
                {
                    (BlueprintId, 1130, 1),
                    (BlueprintId + 1, 34, 27),
                    (BlueprintId + 2, 34, 54),
                    (BlueprintId + 3, 35, 63),
                }
            )
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = Pilot.PilotId,
                        TypeId = type,
                        LocationId = Pilot.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = quantity,
                    }
                );
            }
            world.SaveChanges();
        }

        public ManufacturingRequest Request(int runs = 1) =>
            new(
                BlueprintId,
                1130,
                Pilot.StationId,
                runs,
                215,
                288d * runs,
                new Dictionary<int, int> { [34] = 27 * runs, [35] = 21 * runs }
            );

        public ManufacturingRequest ModuleRequest(IndustryBlueprintType blueprint, int runs = 1)
        {
            var (product, tritanium, pyerite, mexallon, nocxium) = blueprint switch
            {
                IndustryBlueprintType.MinerI => ((int)MiningLaserType.MinerI, 1324, 481, 119, 0),
                IndustryBlueprintType.SmallShieldBoosterI => ((int)ShieldBoosterType.SmallI, 1327, 481, 113, 0),
                IndustryBlueprintType.OneMnAfterburnerI => ((int)PropulsionModuleType.OneMnAfterburnerI, 708, 643, 1, 1),
                IndustryBlueprintType.GatlingRail75MmI => ((int)CombatWeaponType.GatlingRailgun75mm, 231, 220, 0, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(blueprint)),
            };
            Dictionary<int, int> materials = new() { [34] = tritanium * runs, [35] = pyerite * runs };
            if (mexallon > 0)
            {
                materials.Add(36, mexallon * runs);
            }
            if (nocxium > 0)
            {
                materials.Add(38, nocxium * runs);
            }
            return new(BlueprintId + (int)blueprint, (int)blueprint, Pilot.StationId, runs, product, 864d * runs, materials);
        }

        public void AddModules()
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemGroup VALUES (40,'Shield Booster',7),(46,'Afterburner',7),(74,'Hybrid Weapon',7);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (483,'Miner I',54),(399,'Small Shield Booster I',40),
                    (439,'1MN Afterburner I',46),(561,'75mm Gatling Rail I',74),(482,'Miner II',54),(564,'Heavy Hybrid',74),
                    (785,'Miner I Blueprint',167),(1032,'Small Shield Booster I Blueprint',167),
                    (1072,'1MN Afterburner I Blueprint',167),(1112,'75mm Gatling Rail I Blueprint',167),
                    (36,'Mexallon',18),(38,'Nocxium',18);
                INSERT INTO DogmaAttribute(Id,Name) VALUES (13,'medSlots'),(14,'hiSlots');
                INSERT INTO TypeAttribute VALUES (483,422,1),(399,422,1),(439,422,1),(561,422,1),(561,128,1),
                    (482,422,2),(564,422,1),(564,128,2),(601,13,2),(601,14,2);
                INSERT INTO SourceRecord VALUES ('client/types','483','{{"published":1,"marketGroupID":1,"volume":5.0}}'),
                    ('client/types','399','{{"published":1,"marketGroupID":1,"volume":5.0}}'),
                    ('client/types','439','{{"published":1,"marketGroupID":1,"volume":5.0}}'),
                        ('client/types','561','{{"published":1,"marketGroupID":1,"volume":5.0}}'),
                    ('client/types','785','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                        ('client/types','1032','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                    ('client/types','1072','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                        ('client/types','1112','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                    ('client/types','36','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                        ('client/types','38','{{"published":1,"marketGroupID":1,"volume":0.01}}');
                """
            );
            PassiveCatalogFixture.LoadActiveMetadata(
                new WorldDatabase(Db.Path).CatalogPath,
                new HashSet<int> { (int)ShieldBoosterType.SmallI, (int)PropulsionModuleType.OneMnAfterburnerI }
            );
            using WorldDbContext world = new(Db.Path);
            foreach (
                var blueprint in new[]
                {
                    IndustryBlueprintType.MinerI,
                    IndustryBlueprintType.SmallShieldBoosterI,
                    IndustryBlueprintType.OneMnAfterburnerI,
                    IndustryBlueprintType.GatlingRail75MmI,
                }
            )
            {
                var request = ModuleRequest(blueprint);
                catalog.Add(
                    new SourceRecord
                    {
                        Dataset = "client/blueprints",
                        Key = JsonSerializer.Serialize(((int)blueprint).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Data = JsonSerializer.Serialize(
                            new
                            {
                                blueprintTypeID = (int)blueprint,
                                activities = new
                                {
                                    manufacturing = new
                                    {
                                        time = 900,
                                        skills = new[] { new { typeID = (int)IndustrySkill.Industry, level = 1 } },
                                        materials = request.QuotedMaterials.Select(m => new { typeID = m.Key, quantity = m.Value }),
                                        products = new[] { new { typeID = request.ProductTypeId, quantity = 1 } },
                                    },
                                },
                            }
                        ),
                    }
                );
                world.Add(
                    new OwnedItem
                    {
                        Id = request.BlueprintId,
                        OwnerId = Pilot.PilotId,
                        TypeId = (int)blueprint,
                        LocationId = Pilot.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 1,
                    }
                );
            }
            foreach (int type in new[] { 34, 35, 36, 38 })
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = BlueprintId + 2000 + type,
                        OwnerId = Pilot.PilotId,
                        TypeId = type,
                        LocationId = Pilot.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 10_000,
                    }
                );
            }
            catalog.SaveChanges();
            world.SaveChanges();
        }

        public long Quantity(int type)
        {
            using WorldDbContext world = new(Db.Path);
            return world
                .Set<OwnedItem>()
                .Where(i => i.OwnerId == Pilot.PilotId && i.TypeId == type)
                .AsEnumerable()
                .Sum(i => (long)i.Quantity);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void ClaimedProductionUnlocksSnapshotSupplyForOtherPlayersAndSurvivesMigration()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":1.0}}')"
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            world.SaveChanges();
        }
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request());
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(288));
        Assert.Empty(market.Orders(f.Pilot.PilotId, 215));
        f.Store.Deliver(f.Pilot.PilotId, job.Job.Id, f.Pilot.SystemId);
        var orders = market.Orders(f.Db.Store.FindPilot(1000002)!.PilotId, 215);
        Assert.Contains(orders, o => !o.Bid && o.PriceCents == MarketSnapshotCatalog.Quotes[215].SellCents);
        Assert.Contains(orders, o => o.Bid && o.PriceCents == MarketSnapshotCatalog.Quotes[215].BuyCents);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldMarketUnlock; PRAGMA user_version=121;");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext reopened = new(f.Db.Path);
        var unlock = Assert.Single(reopened.Set<SavedMarketUnlock>());
        Assert.Equal(job.Job.Id, unlock.JobId);
        Assert.Equal(f.Pilot.PilotId, unlock.PilotId);
        Assert.Contains(market.Orders(f.Pilot.PilotId, 215), o => !o.Bid && o.Remaining > 0);
    }

    [Fact]
    public void FailedClaimRollsBackDeliveryAndRetryUnlocksExactlyOnce()
    {
        using Fixture f = new();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request());
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(288));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectMarketUnlock BEFORE INSERT ON WorldMarketUnlock
                BEGIN SELECT RAISE(ABORT,'unlock failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Job.Id, f.Pilot.SystemId));
        Assert.Equal(0, f.Quantity(215));
        Assert.Equal(IndustryJobStatus.Ready, f.Store.Job(f.Pilot.PilotId, job.Job.Id).Status);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedMarketUnlock>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectMarketUnlock");
        }
        f.Store.Deliver(f.Pilot.PilotId, job.Job.Id, f.Pilot.SystemId);
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, job.Job.Id, f.Pilot.SystemId).Changes);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<SavedMarketUnlock>());
        Assert.Equal(100, f.Quantity(215));
    }

    [Fact]
    public void CancelledProductionDoesNotUnlockSupply()
    {
        using Fixture f = new();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request());
        f.Store.Cancel(f.Pilot.PilotId, job.Job.Id, f.Pilot.SystemId);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedMarketUnlock>());
        Assert.Equal(0, f.Quantity(215));
    }

    [Fact]
    public void ManufacturedAmmunitionCanBeListedByAnOfflinePilotAndPurchasedAfterReopen()
    {
        using Fixture f = new();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request());
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(288));
        var delivered = f.Store.Deliver(f.Pilot.PilotId, job.Job.Id, f.Pilot.SystemId);
        var ammunition = Assert.Single(delivered.Changes).Item;
        var wing = f.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            world.Set<SavedWorldEconomy>().Single().TestingGrants = false;
            world.Ships.Single(s => s.Id == wing.ShipId).StationId = f.Pilot.StationId;
            world.Add(new PilotWallet { PilotId = f.Pilot.PilotId, Cents = 1_000_000 });
            world.Add(new PilotWallet { PilotId = wing.PilotId, Cents = 1_000_000 });
            world.SaveChanges();
        }
        var personal = f.Db.Services.GetRequiredService<Eden.Gameplay.Market.IPersonalMarket>();
        var market = f.Db.Services.GetRequiredService<Eden.Gameplay.Market.IPilotMarket>();
        personal.PlaceSell(f.Pilot.PilotId, [new(ammunition.Id, f.Pilot.StationId, 215, 1000, 100)], 1);
        Assert.Equal(0, f.Quantity(215));
        // No client/session is constructed for the seller. Reopen retains its listing.
        WorldInitialization.Initialize(new(f.Db.Path));
        market.Buy(wing.PilotId, f.Pilot.StationId, [new(215, 1000, 40)]);
        WorldInitialization.Initialize(new(f.Db.Path));
        Assert.Equal(60, Assert.Single(personal.PersonalOrders(f.Pilot.PilotId)).Remaining);
        Assert.Equal(1_027_000, market.Balance(f.Pilot.PilotId));
        Assert.Equal(960_000, market.Balance(wing.PilotId));
        Assert.Equal(40, Assert.Single(market.History(wing.PilotId, [215])).Volume);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(40, saved.Set<OwnedItem>().Single(i => i.OwnerId == wing.PilotId && i.TypeId == 215).Quantity);
        Assert.Single(saved.Set<IndustryJob>());
        Assert.Equal(IndustryJobStatus.Delivered, saved.Set<IndustryJob>().Single().Status);
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, job.Job.Id, f.Pilot.SystemId).Changes);
    }

    [Fact]
    public void MaterialsBecomeAReadyJobThenDeliverRealAmmunitionExactlyOnceAcrossRestart()
    {
        using Fixture f = new();
        var result = f.Store.Install(f.Pilot.PilotId, f.Request());
        Assert.Equal(288, result.Job.DurationSeconds);
        Assert.Equal(100, result.Job.OutputQuantity);
        Assert.Equal(54, f.Quantity(34));
        Assert.Equal(42, f.Quantity(35));
        Assert.Equal(0, f.Quantity(215));
        Assert.Equal(result.Job.Id, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).JobId);
        Assert.True(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).Item.Singleton);
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, result.Job.Id, f.Pilot.SystemId));
        using (WorldDbContext unclaimed = new(f.Db.Path))
        {
            Assert.Empty(unclaimed.Set<SavedMarketUnlock>());
        }
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(result.Job, f.Store.Job(f.Pilot.PilotId, result.Job.Id));
        f.Clock.UtcNow = f.Clock.UtcNow.AddSeconds(288);
        Assert.Equal(IndustryJobStatus.Ready, f.Store.Job(f.Pilot.PilotId, result.Job.Id).Status);
        var delivered = f.Store.Deliver(f.Pilot.PilotId, result.Job.Id, f.Pilot.SystemId);
        Assert.Equal(IndustryJobStatus.Delivered, delivered.Job.Status);
        Assert.Equal(100, f.Quantity(215));
        using (WorldDbContext claimed = new(f.Db.Path))
        {
            var unlock = Assert.Single(claimed.Set<SavedMarketUnlock>());
            Assert.Equal(215, unlock.TypeId);
            Assert.Equal(result.Job.Id, unlock.JobId);
            Assert.Equal(f.Pilot.PilotId, unlock.PilotId);
        }
        Assert.Equal(100, Assert.Single(delivered.Changes).Item.Quantity);
        Assert.Null(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).JobId);
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, result.Job.Id, f.Pilot.SystemId).Changes);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, false));
        Assert.Single(f.Store.Jobs(f.Pilot.PilotId, true));
        Assert.Equal(1, TaskTrackingTestData.Count(f.Db, TaskEvents.IndustryStarted));
        Assert.Equal(1, TaskTrackingTestData.Count(f.Db, TaskEvents.IndustryDelivered));
        Assert.Equal(100, f.Quantity(215));
    }

    [Fact]
    public void MissingMaterialsRollBackEarlierStackConsumptionAndBlueprintChanges()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId + 3)!.Quantity = 20;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        Assert.Equal(81, f.Quantity(34));
        Assert.Equal(20, f.Quantity(35));
        Assert.False(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).Item.Singleton);
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
    }

    [Fact]
    public void BlueprintAndSlotStayReservedUntilDeliveryOrCancellationWithoutMaterialRefund()
    {
        using Fixture f = new();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job;
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        Assert.Throws<InvalidDataException>(() =>
            inventory.Move(f.Pilot.PilotId, [Fixture.BlueprintId], f.Pilot.StationId, f.Pilot.ShipId, (int)InventoryFlag.Cargo, null)
        );
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        var cancelled = f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId);
        Assert.Equal(IndustryJobStatus.Cancelled, cancelled.Job.Status);
        Assert.Equal(54, f.Quantity(34));
        Assert.Equal(0, f.Quantity(215));
        Assert.Empty(f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes);
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId));
        Assert.NotNull(f.Store.Install(f.Pilot.PilotId, f.Request()).Job);
    }

    [Fact]
    public void InvalidOwnerLocationSkillsQuoteAndRunCountCannotInstallOrConsumeMaterials()
    {
        using Fixture f = new();
        foreach (
            var request in new[]
            {
                f.Request() with
                {
                    BlueprintTypeId = 1137,
                },
                f.Request() with
                {
                    FacilityId = 1,
                },
                f.Request() with
                {
                    Runs = 0,
                },
                f.Request() with
                {
                    Runs = int.MaxValue,
                },
                f.Request() with
                {
                    QuotedSeconds = double.NaN,
                },
                f.Request() with
                {
                    QuotedSeconds = 300,
                },
                f.Request() with
                {
                    ProductTypeId = 222,
                },
                f.Request() with
                {
                    QuotedMaterials = new Dictionary<int, int> { [34] = 1 },
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, request));
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(90000002, f.Request()));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, (int)IndustrySkill.Industry)!.Points = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        Assert.Equal(81, f.Quantity(34));
        Assert.Equal(63, f.Quantity(35));
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
    }

    [Fact]
    public async Task CompetingInstallsAndDeliveriesConsumeAndProduceOneJobOnly()
    {
        using Fixture f = new();
        async Task<IndustryResult?> Install() =>
            await Task.Run(() =>
            {
                try
                {
                    return f.Store.Install(f.Pilot.PilotId, f.Request());
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            });
        var installed = Assert.Single(await Task.WhenAll(Install(), Install()), r => r is not null)!;
        Assert.Equal(54, f.Quantity(34));
        f.Clock.UtcNow = f.Clock.UtcNow.AddDays(1);
        var deliveries = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Store.Deliver(f.Pilot.PilotId, installed.Job.Id, f.Pilot.SystemId)))
        );
        Assert.Single(deliveries, d => d.Changes.Count > 0);
        Assert.Equal(100, f.Quantity(215));
    }

    [Fact]
    public void StackedOriginalsSplitOneForTheJobAndKeepOtherBlueprintsUsable()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId)!.Quantity = 2;
            world.SaveChanges();
        }
        var job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job;
        Assert.NotEqual(Fixture.BlueprintId, job.BlueprintId);
        Assert.Equal(2, f.Quantity(1130));
        Assert.Null(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).JobId);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        Assert.NotEmpty(
            inventory.Move(f.Pilot.PilotId, [Fixture.BlueprintId], f.Pilot.StationId, f.Pilot.ShipId, (int)InventoryFlag.Cargo, null)
        );
        Assert.Equal(job.Id, f.Store.Blueprint(f.Pilot.PilotId, job.BlueprintId).JobId);
    }

    [Fact]
    public void IndustryAndAdvancedIndustryUseCatalogBonusesInBothTheJobAndNativeCharacterBrain()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, (int)IndustrySkill.Industry)!.Points = 8000;
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)IndustrySkill.AdvancedIndustry,
                    Points = 750,
                }
            );
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)IndustrySkill.MassProduction,
                    Points = 500,
                }
            );
            world.SaveChanges();
        }
        var bonuses = f.Store.CharacterBonuses(f.Pilot.PilotId);
        Assert.Contains(bonuses, b => b.Value == -12 && b.Attribute == DogmaAttributeId.ManufactureTimeMultiplier);
        Assert.Contains(bonuses, b => b.Value == -3 && !b.Additive);
        Assert.Contains(bonuses, b => b.Value == 1 && b.Additive);
        var job = f.Store.Install(f.Pilot.PilotId, f.Request() with { QuotedSeconds = 256 }).Job;
        Assert.Equal(256, job.DurationSeconds);
        var brain = new PilotBrain(
            f.Db.Services.GetRequiredService<IPilotSkillReader>(),
            f.Clock,
            f.Store,
            f.Db.Services.GetRequiredService<Eden.Gameplay.Characters.IPilotJumpCloneSkills>(),
            f.Db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotNavigationSkills>()
        ).Read(f.Pilot.PilotId, f.Pilot.ShipId);
        var effects = Assert
            .IsType<List<object?>>(WireAssertions.Materialize(new BlueList<BrainEffect>(brain.CharacterEffects)))
            .Cast<BlueNewObject>()
            .Select(e => Assert.IsType<Dictionary<string, object?>>(e.State))
            .ToArray();
        Assert.Equal(3, effects.Length);
        Assert.All(
            effects,
            e =>
            {
                Assert.Equal("M", e["modifierType"]);
                Assert.Equal(f.Pilot.PilotId, e["toItemID"]);
            }
        );
        Assert.Contains(effects, e => Equals(e["operation"], 2) && Equals(e["value"], 1d));
        Assert.NotEmpty(BlueMarshal.Encode(brain));
    }

    [Fact]
    public void NativeJobRequestsValidateLocationsAndRefreshMonitoredMaterials()
    {
        using Fixture f = new();
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
        BlueNewObject location = new(
            BlueNewObject.IndustryLocationClass,
            new Dictionary<string, object?>
            {
                ["itemID"] = pilot.StationId,
                ["ownerID"] = pilot.PilotId,
                ["flagID"] = (int)InventoryFlag.Hangar,
            }
        );
        var request = Assert.IsType<Dictionary<string, object?>>(
            BlueMarshal.Decode(
                BlueMarshal.Encode(
                    new Dictionary<string, object?>
                    {
                        ["blueprintID"] = Fixture.BlueprintId,
                        ["blueprintTypeID"] = 1130,
                        ["facilityID"] = pilot.StationId,
                        ["characterID"] = pilot.PilotId,
                        ["activityID"] = (int)IndustryActivity.Manufacturing,
                        ["account"] = new object?[] { pilot.PilotId, 1000L },
                        ["cost"] = 0d,
                        ["tax"] = 0d,
                        ["time"] = 288d,
                        ["runs"] = 1,
                        ["productTypeID"] = null,
                        ["inputLocation"] = location,
                        ["outputLocation"] = location,
                        ["materials"] = new Dictionary<long, object?> { [34] = 27L, [35] = 21L },
                    }
                )
            )
        );
        var connected = Assert.IsType<object?[]>(handler.CallWire(context, "industryMonitor", "ConnectJob", [request]));
        Assert.Equal(81L, Assert.IsType<Dictionary<long, object?>>(connected[1])[34]);
        request["outputLocation"] = new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = (long)pilot.StationId,
                ["ownerID"] = 90000002L,
                ["flagID"] = (long)InventoryFlag.Hangar,
            }
        );
        Assert.IsType<PilotCallResult.Failure>(handler.CallWire(context, "industryManager", "InstallJob", [request]));
        Assert.Equal(81, f.Quantity(34));
        request["outputLocation"] = request["inputLocation"];
        long jobId = Assert.IsType<long>(handler.CallWire(context, "industryManager", "InstallJob", [request]));
        Assert.Contains(context.Notifications, n => n.Name == "OnIndustryJob");
        handler.Advance(context);
        var materials = Assert.Single(context.Notifications, n => n.Name == "OnIndustryMaterials");
        Assert.Equal(54L, Assert.IsType<Dictionary<long, object?>>(materials.Arguments[1])[34]);
        Assert.NotEmpty(BlueMarshal.Encode(handler.CallWire(context, "industryManager", "GetJobsByOwner", [pilot.PilotId, false])));
        Assert.IsType<PilotCallResult.Failure>(handler.CallWire(context, "industryManager", "GetJob", [jobId + 1]));
        Assert.Null(handler.CallWire(context, "industryMonitor", "DisconnectJob", [connected[0]]));
        Assert.False(handler.IsMonitoring);
    }

    [Fact]
    public void ManufacturingSlotsApplyAcrossDifferentOriginalsAndCatalogMassProductionBonuses()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId)!.Quantity = 2;
            world.SaveChanges();
        }
        f.Store.Install(f.Pilot.PilotId, f.Request());
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)IndustrySkill.MassProduction,
                    Points = 500,
                }
            );
            world.SaveChanges();
        }
        f.Store.Install(f.Pilot.PilotId, f.Request());
        Assert.Equal(2, f.Store.Jobs(f.Pilot.PilotId, false).Count);
        Assert.Equal(27, f.Quantity(34));
        Assert.Equal(21, f.Quantity(35));
    }

    [Fact]
    public void ExactThirtyDayMaximumDoesNotPermitAnAdditionalRun()
    {
        using Fixture f = new();
        const int runs = 9000;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId + 1)!.Quantity = 27 * runs;
            world.Set<OwnedItem>().Find(Fixture.BlueprintId + 3)!.Quantity = 21 * runs;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request(runs + 1)));
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
        var job = f.Store.Install(f.Pilot.PilotId, f.Request(runs)).Job;
        Assert.Equal(IndustryDefinitions.MaximumJobSeconds, job.DurationSeconds);
        Assert.Equal(900_000, job.OutputQuantity);
    }

    [Fact]
    public void BlueprintInventoryChangesInvalidateMemoizedListsOncePerNotificationFlush()
    {
        using Fixture f = new();
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
        var item = f.Store.Blueprint(pilot.PilotId, Fixture.BlueprintId).Item;
        InventoryChangesWire.NotifyItemChange(context, new(item, null));
        InventoryChangesWire.NotifyItemChange(context, new(item with { Quantity = 2 }, item));
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnItemChange"));
        Assert.Equal(pilot.PilotId, Assert.Single(context.Notifications, n => n.Name == "OnBlueprintsUpdated").Arguments[0]);
        context.Notifications.Clear();
        InventoryChangesWire.NotifyItemChange(context, new(item, item with { LocationId = pilot.ShipId }));
        Assert.Single(context.Notifications, n => n.Name == "OnBlueprintsUpdated");
    }

    [Fact]
    public void SharedIndustryEntryChecksBothCatalogServicesWithoutEnablingResearch()
    {
        using Fixture f = new();
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
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object? Check(StationService service, int station) =>
            router.CallWire(
                context,
                "corpStationMgr",
                "MachoBindObject",
                new object?[] { (long)station, new object?[] { "DoStandingCheckForStationService", new object?[] { (long)service } } },
                _ => { }
            );
        Assert.IsType<object?[]>(Check(StationService.Manufacturing, pilot.StationId));
        Assert.IsType<object?[]>(Check(StationService.Laboratory, pilot.StationId));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"services\":[7,15,17]}}' WHERE Dataset='client/stationoperations' AND Key='14'"
            );
        }
        Assert.IsType<object?[]>(Check(StationService.Laboratory, pilot.StationId));
        Assert.Throws<InvalidDataException>(() => f.Store.Install(pilot.PilotId, f.Request()));
        Assert.Same(PilotCallResult.Unsupported, Check(StationService.Laboratory, pilot.StationId + 1));
        Assert.Same(
            PilotCallResult.Unsupported,
            router.CallWire(context, "industryManager", "ResearchJob", Array.Empty<object?>(), _ => { })
        );
    }

    [Theory]
    [InlineData(IndustryBlueprintType.MinerI)]
    [InlineData(IndustryBlueprintType.SmallShieldBoosterI)]
    [InlineData(IndustryBlueprintType.OneMnAfterburnerI)]
    [InlineData(IndustryBlueprintType.GatlingRail75MmI)]
    public void CatalogModuleRecipesDeliverPackagedEquipmentThatCanBeFitted(IndustryBlueprintType blueprint)
    {
        using Fixture f = new();
        f.AddModules();
        var request = f.ModuleRequest(blueprint, runs: 2);
        var before = request.QuotedMaterials.Keys.ToDictionary(t => t, f.Quantity);
        var job = f.Store.Install(f.Pilot.PilotId, request).Job;
        Assert.Equal(1728, job.DurationSeconds);
        Assert.Equal(2, job.OutputQuantity);
        foreach (var material in request.QuotedMaterials)
        {
            Assert.Equal(before[material.Key] - material.Value, f.Quantity(material.Key));
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId));
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(job, f.Store.Job(f.Pilot.PilotId, job.Id));
        f.Clock.UtcNow = f.Clock.UtcNow.AddSeconds(job.DurationSeconds);
        var delivered = Assert.Single(f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes).Item;
        Assert.False(delivered.Singleton);
        Assert.False(delivered.Online);
        Assert.Equal((int)InventoryFlag.Hangar, delivered.FlagId);
        var fitting = f.Db.Services.GetRequiredService<IPilotFitting>();
        var changes = fitting.Fit(f.Pilot.PilotId, [delivered.Id], f.Pilot.StationId, (int)InventoryFlag.AutoFit);
        var fitted = Assert.Single(changes, c => c.Item.LocationId == f.Pilot.ShipId).Item;
        Assert.True(fitted.Singleton);
        Assert.Equal(1, fitted.Quantity);
        Assert.Equal(request.ProductTypeId, fitted.TypeId);
        Assert.Equal(2, f.Quantity(fitted.TypeId));
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes);
        Assert.Equal(2, f.Quantity(fitted.TypeId));
    }

    [Fact]
    public void FacilityProductListAdmitsUsableTechOneModulesAndRejectsUnsupportedTiersAndWeaponSizes()
    {
        using Fixture f = new();
        f.AddModules();
        var products = f.Store.ProductTypes();
        foreach (
            var blueprint in new[]
            {
                IndustryBlueprintType.MinerI,
                IndustryBlueprintType.SmallShieldBoosterI,
                IndustryBlueprintType.OneMnAfterburnerI,
                IndustryBlueprintType.GatlingRail75MmI,
            }
        )
        {
            Assert.Contains(f.ModuleRequest(blueprint).ProductTypeId!.Value, products);
        }
        Assert.DoesNotContain((int)MiningLaserType.MinerII, products);
        Assert.DoesNotContain(564, products);
        Assert.DoesNotContain((int)CombatWeaponType.CivilianGatlingRailgun, products);
        var request = f.ModuleRequest(IndustryBlueprintType.MinerI);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog
                .Set<TypeAttribute>()
                .AsTracking()
                .Single(a => a.ItemTypeId == (int)MiningLaserType.MinerI && a.AttributeId == (int)DogmaAttributeId.TechLevel)
                .Value = 2;
            catalog.SaveChanges();
        }
        long tritanium = f.Quantity(34);
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, request));
        Assert.Equal(tritanium, f.Quantity(34));
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
    }

    [Fact]
    public void SchemaTwentySixMigrationRetainsEveryItemAndCreatesNoJobsOrGrants()
    {
        using Fixture f = new();
        string items;
        using (WorldDbContext world = new(f.Db.Path))
        {
            items = JsonSerializer.Serialize(world.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id).ToArray());
            world.Database.ExecuteSqlRaw(TestWorld.DropTestingMarketSchema + "DROP TABLE WorldIndustryJob; PRAGMA user_version=26");
        }
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(saved.Set<IndustryJob>());
        Assert.Equal(items, JsonSerializer.Serialize(saved.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id).ToArray()));
    }
}
