using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ContractTests
{
    [Theory]
    [InlineData(AccountCloneGrade.Alpha, 360000, 1000000)]
    [InlineData(AccountCloneGrade.Omega, 300000, 500000)]
    public void ContractLimitsAndFeesUseEffectiveCloneGradeSkillLevels(AccountCloneGrade grade, long brokerIsk, long taxIsk)
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot, 100_000_000_000);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            var alpha = catalog.Set<SourceRecord>().AsTracking().Single(s => s.Dataset == "client/clonegrades" && s.Key == "\"1\"");
            alpha.Data = "{\"skills\":[{\"typeID\":3446,\"level\":2}]}";
            foreach (var skill in Enum.GetValues<ContractSkill>())
            {
                if (catalog.Set<ItemType>().Find((int)skill) is null)
                {
                    catalog.Add(
                        new ItemType
                        {
                            Id = (int)skill,
                            Name = skill.ToString(),
                            GroupId = 257,
                        }
                    );
                }
            }
            catalog.SaveChanges();
        }
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = grade;
            world.Pilots.Find(ContractAssetTestData.Pilot)!.RaceId = 1;
            foreach (var skill in Enum.GetValues<ContractSkill>())
            {
                world.Add(
                    new PilotSkill
                    {
                        PilotId = ContractAssetTestData.Pilot,
                        TypeId = (int)skill,
                        Points = 256000,
                    }
                );
            }
            world.SaveChanges();
        }
        long item = ContractAssetTestData.Stack(db, 100);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        var draft = Draft(item, null) with { PriceCents = 10_000_000_000, Items = [new(item, 1)] };
        long id = contracts.Create(ContractAssetTestData.Pilot, draft);
        Assert.Equal(
            new ContractFees(brokerIsk * 100, 100_000_000, taxIsk * 100),
            contracts.Details(ContractAssetTestData.Pilot, id).Contract.Fees
        );
        if (grade == AccountCloneGrade.Alpha)
        {
            Assert.Throws<InvalidDataException>(() => contracts.Create(ContractAssetTestData.Pilot, draft));
        }
        else
        {
            Assert.True(contracts.Create(ContractAssetTestData.Pilot, draft) > id);
        }
    }

    [Theory]
    [InlineData(601, 237, 6, 15000d, 0d, false)]
    [InlineData(3651, 54, 7, 5d, 0d, false)]
    [InlineData(1130, 167, 9, 0.01d, 20000d, false)]
    [InlineData(3293, 12, 2, 325d, 12000d, true)]
    [InlineData(3465, 340, 2, 650d, 31000d, true)]
    public void CatalogVariantsRetainIdentityTypeQuantityAndContainerContentsAndValueEscrowOnce(
        int typeId,
        int groupId,
        int categoryId,
        double volume,
        double basePrice,
        bool singleton
    )
    {
        using TestWorld db = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            if (catalog.Set<ItemCategory>().Find(categoryId) is null)
            {
                catalog.Add(new ItemCategory { Id = categoryId, Name = "Contract fixture category" });
            }
            if (catalog.Set<ItemGroup>().Find(groupId) is null)
            {
                catalog.Add(
                    new ItemGroup
                    {
                        Id = groupId,
                        Name = "Contract fixture group",
                        CategoryId = categoryId,
                    }
                );
            }
            if (catalog.Set<ItemType>().Find(typeId) is null)
            {
                catalog.Add(
                    new ItemType
                    {
                        Id = typeId,
                        Name = "Contract fixture type",
                        GroupId = groupId,
                    }
                );
            }
            var source =
                catalog.Set<SourceRecord>().AsTracking().SingleOrDefault(s => s.Dataset == "client/types" && s.Key == typeId.ToString())
                ?? catalog.Add(new SourceRecord { Dataset = "client/types", Key = typeId.ToString() }).Entity;
            source.Data = System.Text.Json.JsonSerializer.Serialize(new { volume, basePrice });
            catalog.SaveChanges();
        }
        Fund(db, ContractAssetTestData.Pilot);
        Fund(db, ContractAssetTestData.Other);
        long item = ContractAssetTestData.Stack(db, singleton ? 1 : 3);
        using (WorldDbContext world = new(db.Path))
        {
            var root = world.Set<OwnedItem>().Find(item)!;
            root.TypeId = typeId;
            root.Singleton = singleton;
            world.SaveChanges();
        }
        long? child = singleton ? ContractAssetTestData.Stack(db, 1, item) : null;
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        var draft = Draft(item) with { PriceCents = 0, Items = [new(item, singleton ? 1 : 3)] };
        long id = contracts.Create(ContractAssetTestData.Pilot, draft);
        Assert.Equal(basePrice * (singleton ? 1 : 3), contracts.Escrow(ContractAssetTestData.Pilot).ItemsValue);
        contracts.Accept(ContractAssetTestData.Other, id);
        var returned = draft with { AssigneeId = ContractAssetTestData.Pilot };
        long resale = contracts.Create(ContractAssetTestData.Other, returned);
        Assert.Equal(basePrice * (singleton ? 1 : 3), contracts.Escrow(ContractAssetTestData.Other).ItemsValue);
        Assert.Equal(0, contracts.Escrow(ContractAssetTestData.Pilot).ItemsValue);
        contracts.Delete(ContractAssetTestData.Other, resale);
        WorldInitialization.Open(db.Path);
        using WorldDbContext verify = new(db.Path);
        var transferred = verify.Set<OwnedItem>().Find(item)!;
        Assert.Equal(ContractAssetTestData.Other, transferred.OwnerId);
        Assert.Equal(typeId, transferred.TypeId);
        Assert.Equal(singleton ? 1 : 3, transferred.Quantity);
        Assert.Equal(singleton, transferred.Singleton);
        if (child is { } contained)
        {
            Assert.Equal(item, verify.Set<OwnedItem>().Find(contained)!.LocationId);
            Assert.Equal(ContractAssetTestData.Other, verify.Set<OwnedItem>().Find(contained)!.OwnerId);
        }
    }

    [Fact]
    public void FittedHullEscrowRetainsHealthLoadedChargesCarriedHullsAndBlocksOtherInventoryOperations()
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot);
        Fund(db, ContractAssetTestData.Other);
        long hull = ContractAssetTestData.Hull(db);
        long carried = ContractAssetTestData.Hull(db, hull);
        long module = ContractAssetTestData.Stack(db, 1, hull, flag: (int)InventoryFlag.HighSlot0);
        long charges = ContractAssetTestData.Stack(db, 40, module, flag: (int)InventoryFlag.Cargo);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Singleton = true;
            world.Set<OwnedItem>().Find(module)!.Online = true;
            world.Add(
                new ShipHealthState
                {
                    ShipId = hull,
                    ArmorDamage = 15,
                    StructureDamage = 5,
                    ShieldCharge = 50,
                }
            );
            world.SaveChanges();
        }
        InsuranceTestData.Prepare(db);
        var insurance = db.Services.GetRequiredService<IPilotInsurance>();
        insurance.Insure(ContractAssetTestData.Pilot, ContractAssetTestData.Station, hull, 5000, false);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        var draft = Draft(hull) with { Items = [new(hull, 1)] };
        long id = contracts.Create(ContractAssetTestData.Pilot, draft);
        Assert.Equal(4, contracts.Details(ContractAssetTestData.Pilot, id).Items.Count);
        Assert.Equal(5, contracts.Details(ContractAssetTestData.Pilot, id).Items.Single(i => i.ItemId == hull).Damage);
        var fleet = db.Services.GetRequiredService<IPilotFleet>();
        Assert.Null(fleet.Find(ContractAssetTestData.Pilot, hull));
        Assert.Empty(db.Services.GetRequiredService<IPilotInventory>().List(ContractAssetTestData.Pilot, hull));
        Assert.Throws<InvalidDataException>(() => fleet.Board(ContractAssetTestData.Pilot, hull, db.Store.FindPilot(1000001)!.ShipId));
        Assert.Throws<InvalidDataException>(() =>
            db
                .Services.GetRequiredService<IPilotInventory>()
                .Move(ContractAssetTestData.Pilot, [charges], module, db.Store.FindPilot(1000001)!.ShipId, (int)InventoryFlag.Cargo, null)
        );
        Assert.Null(insurance.Contract(ContractAssetTestData.Pilot, hull));
        WorldInitialization.Open(db.Path);
        contracts.Accept(ContractAssetTestData.Other, id);
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(ContractAssetTestData.Other, verify.Ships.Find(hull)!.OwnerId);
        Assert.Equal(hull, verify.Ships.Find(carried)!.StoredInId);
        Assert.Equal(ContractAssetTestData.Other, verify.Ships.Find(carried)!.OwnerId);
        Assert.Equal(module, verify.Set<OwnedItem>().Find(charges)!.LocationId);
        Assert.True(verify.Set<OwnedItem>().Find(module)!.Online);
        Assert.Equal(40, verify.Set<OwnedItem>().Find(charges)!.Quantity);
        Assert.Equal(5, verify.Set<ShipHealthState>().Find(hull)!.StructureDamage);
        Assert.Equal(InsurancePolicyStatus.HullRemoved, verify.Set<SavedInsurancePolicy>().Single().Status);
    }

    [Fact]
    public void BuyingRequestedItemsConsumesMultipleStacksAndPaysOnlyTheReservedReward()
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot);
        long first = ContractAssetTestData.Stack(db, 3, owner: ContractAssetTestData.Other);
        long second = ContractAssetTestData.Stack(db, 4, owner: ContractAssetTestData.Other);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        var draft = Draft(1) with
        {
            PriceCents = 0,
            RewardCents = 25000,
            Items = [],
            RequestedItems = [new(InventoryDefinitions.CivilianMinerType, 5)],
        };
        long id = contracts.Create(ContractAssetTestData.Pilot, draft);
        Assert.Equal(25000, contracts.Escrow(ContractAssetTestData.Pilot).IskCents);
        contracts.Accept(ContractAssetTestData.Other, id);
        using WorldDbContext world = new(db.Path);
        Assert.Equal(ContractAssetTestData.Pilot, world.Set<OwnedItem>().Find(first)!.OwnerId);
        Assert.Equal(3, world.Set<OwnedItem>().Find(second)!.Quantity);
        Assert.Equal(ContractAssetTestData.Other, world.Set<OwnedItem>().Find(second)!.OwnerId);
        Assert.Equal(
            6,
            world
                .Set<OwnedItem>()
                .Where(i =>
                    i.OwnerId == ContractAssetTestData.Pilot
                    && i.TypeId == InventoryDefinitions.CivilianMinerType
                    && i.Id != first
                    && i.Id != second
                )
                .Sum(i => i.Quantity) + world.Set<OwnedItem>().Find(first)!.Quantity
        );
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(25000, wallet.Balance(ContractAssetTestData.Other));
        Assert.Equal(100_000_000 - 1_000_000 - 25000, wallet.Balance(ContractAssetTestData.Pilot));
        Assert.Equal(0, contracts.Escrow(ContractAssetTestData.Pilot).IskCents);
    }

    [Fact]
    public void PublicSettlementRefundsDepositAndChargesQuotedTaxWhileCancellationForfeitsDeposit()
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot);
        Fund(db, ContractAssetTestData.Other);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        var draft = Draft(item, null) with { PriceCents = 10_000_000 };
        long id = contracts.Create(ContractAssetTestData.Pilot, draft);
        Assert.Equal(new ContractFees(1_000_000, 10_000_000, 1_000_000), contracts.Details(ContractAssetTestData.Other, id).Contract.Fees);
        Assert.Equal(10_000_000, contracts.Escrow(ContractAssetTestData.Pilot).IskCents);
        contracts.Accept(ContractAssetTestData.Other, id);
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(108_000_000, wallet.Balance(ContractAssetTestData.Pilot));
        Assert.Equal(90_000_000, wallet.Balance(ContractAssetTestData.Other));
        Assert.Equal(5, wallet.Journal(ContractAssetTestData.Pilot).Contracts.Count);
        long cancelled = contracts.Create(ContractAssetTestData.Pilot, draft);
        contracts.Delete(ContractAssetTestData.Pilot, cancelled);
        contracts.Delete(ContractAssetTestData.Pilot, cancelled);
        Assert.Equal(97_000_000, wallet.Balance(ContractAssetTestData.Pilot));
        Assert.Equal(ContractStatus.Cancelled, contracts.Details(ContractAssetTestData.Pilot, cancelled).Contract.Status);
        Assert.Equal(0, contracts.Escrow(ContractAssetTestData.Pilot).IskCents);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ExpiryUsesTheExactDeadlineAndReclaimReturnsEscrowOnlyOnce(int ticks, bool available)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        Fund(db, ContractAssetTestData.Pilot);
        Fund(db, ContractAssetTestData.Other);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(ContractAssetTestData.Pilot, Draft(item) with { PriceCents = 0, RewardCents = 12345 });
        clock.AdvanceWallTime(TimeSpan.FromDays(1) + TimeSpan.FromTicks(ticks));
        Assert.Equal(available ? 1 : 0, contracts.Available(ContractAssetTestData.Other).Count);
        if (available)
        {
            contracts.Accept(ContractAssetTestData.Other, id);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => contracts.Accept(ContractAssetTestData.Other, id));
            Assert.Equal(new[] { id }, contracts.Page(ContractAssetTestData.Pilot).Attention);
            contracts.Delete(ContractAssetTestData.Pilot, id);
            contracts.Delete(ContractAssetTestData.Pilot, id);
            Assert.Equal(ContractStatus.Deleted, contracts.Details(ContractAssetTestData.Pilot, id).Contract.Status);
            Assert.Equal(99_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(ContractAssetTestData.Pilot));
        }
    }

    [Fact]
    public void RejectLeavesEscrowForIssuerCollectionAndDismissDoesNotMutateTheContract()
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(ContractAssetTestData.Pilot, Draft(item) with { PriceCents = 0, RewardCents = 12345 });
        Assert.Throws<InvalidDataException>(() => contracts.Reject(ContractAssetTestData.Pilot, id));
        contracts.Reject(ContractAssetTestData.Other, id);
        Assert.Equal(ContractStatus.Rejected, contracts.Details(ContractAssetTestData.Pilot, id).Contract.Status);
        Assert.Equal(12345, contracts.Escrow(ContractAssetTestData.Pilot).IskCents);
        Assert.Throws<InvalidDataException>(() => contracts.Dismiss(ContractAssetTestData.Pilot, id));
        Assert.Throws<InvalidDataException>(() => contracts.Delete(ContractAssetTestData.Other, id));
        contracts.Delete(ContractAssetTestData.Pilot, id);
        contracts.Dismiss(ContractAssetTestData.Pilot, id);
        Assert.Empty(contracts.Page(ContractAssetTestData.Pilot).Attention);
        Assert.Equal(99_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(ContractAssetTestData.Pilot));
        Assert.Throws<InvalidDataException>(() => contracts.Accept(ContractAssetTestData.Other, id));
    }

    [Fact]
    public void FailedCreationAndAcceptanceRollBackEveryAssetWalletLedgerAndFeedChange()
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot);
        long item = ContractAssetTestData.Stack(db);
        long foreign = ContractAssetTestData.Stack(db, owner: ContractAssetTestData.Other);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long revision = contracts.Revision;
        Assert.Throws<InvalidDataException>(() =>
            contracts.Create(ContractAssetTestData.Pilot, Draft(item) with { Items = [new(item, 7), new(foreign, 1)] })
        );
        Assert.Equal(revision, contracts.Revision);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Empty(world.Set<SavedContract>());
            Assert.Empty(world.Set<SavedContractEscrow>());
            Assert.Empty(world.Set<SavedContractPayment>());
            Assert.Empty(world.Set<SavedContractChange>());
            Assert.Equal(20, world.Set<OwnedItem>().Find(item)!.Quantity);
        }
        long id = contracts.Create(ContractAssetTestData.Pilot, Draft(item) with { RequestedItems = [new(3651, 30)] });
        var before = contracts.Details(ContractAssetTestData.Pilot, id);
        revision = contracts.Revision;
        Assert.Throws<InvalidDataException>(() => contracts.Accept(ContractAssetTestData.Other, id));
        Assert.Equal(revision, contracts.Revision);
        Assert.Equal(before.Contract, contracts.Details(ContractAssetTestData.Pilot, id).Contract);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(20, world.Set<OwnedItem>().Find(foreign)!.Quantity);
            Assert.Single(world.Set<SavedContractEscrow>());
            Assert.Single(world.Set<SavedContractPayment>());
        }
        contracts.Delete(ContractAssetTestData.Pilot, id);
        id = contracts.Create(ContractAssetTestData.Pilot, Draft(item));
        Assert.Throws<InvalidDataException>(() => contracts.Accept(ContractAssetTestData.Other, id));
        using WorldDbContext verify = new(db.Path);
        Assert.Single(verify.Set<SavedContractEscrow>());
        Assert.Equal(0, db.Services.GetRequiredService<IPilotWalletReader>().Balance(ContractAssetTestData.Other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LedgerWriteFailureRollsBackTransfersAndKeepsEscrowAvailableForRetry(bool assembled)
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot);
        Fund(db, ContractAssetTestData.Other);
        long item = assembled ? ContractAssetTestData.Hull(db) : ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(ContractAssetTestData.Pilot, Draft(item) with { Items = [new(item, assembled ? 1 : 7)] });
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER FailContractLedger BEFORE INSERT ON WorldContractPayment
                BEGIN SELECT RAISE(ABORT,'Injected contract ledger failure'); END;
                """
            );
        }
        long revision = contracts.Revision;
        Assert.Throws<DbUpdateException>(() => contracts.Accept(ContractAssetTestData.Other, id));
        Assert.Equal(revision, contracts.Revision);
        Assert.Equal(ContractStatus.Outstanding, contracts.Details(ContractAssetTestData.Pilot, id).Contract.Status);
        Assert.Equal(100_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(ContractAssetTestData.Other));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Single(world.Set<SavedContractEscrow>());
            if (assembled)
            {
                Assert.Equal(ContractAssetTestData.Pilot, world.Ships.IgnoreQueryFilters().Single(s => s.Id == item).OwnerId);
            }
            world.Database.ExecuteSqlRaw("DROP TRIGGER FailContractLedger");
        }
        contracts.Accept(ContractAssetTestData.Other, id);
    }

    [Fact]
    public void PrivateContractsHideFromUnrelatedPilotsAndCreationSlotsReleaseAfterCancellation()
    {
        using TestWorld db = new();
        long other = Assert.Single(CalendarTestData.AddPilots(db, 1));
        Fund(db, ContractAssetTestData.Pilot);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(ContractAssetTestData.Pilot, Draft(item));
        Assert.Throws<InvalidDataException>(() => contracts.Details(other, id));
        Assert.Throws<InvalidDataException>(() => contracts.Accept(other, id));
        Assert.Empty(contracts.Available(other));
        Assert.Empty(contracts.List(other, new(ContractAssetTestData.Pilot, null, 0, false, 100, null)));
        Assert.Throws<InvalidDataException>(() => contracts.Create(ContractAssetTestData.Pilot, Draft(item)));
        contracts.Delete(ContractAssetTestData.Pilot, id);
        Assert.True(contracts.Create(ContractAssetTestData.Pilot, Draft(item)) > id);
    }

    [Fact]
    public async Task ConcurrentAcceptorsCannotReceiveOrPayForTheSamePublicContractTwice()
    {
        using TestWorld db = new();
        long other = Assert.Single(CalendarTestData.AddPilots(db, 1));
        Fund(db, ContractAssetTestData.Pilot);
        Fund(db, ContractAssetTestData.Other);
        Fund(db, other);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(ContractAssetTestData.Pilot, Draft(item, null));
        async Task<bool> Accept(long pilot) =>
            await Task.Run(() =>
            {
                try
                {
                    contracts.Accept(pilot, id);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            });
        bool[] results = await Task.WhenAll(Accept(ContractAssetTestData.Other), Accept(other));
        Assert.Single(results, result => result);
        using WorldDbContext world = new(db.Path);
        Assert.Single(world.Set<SavedContractPayment>(), p => p.Type == WalletEntryType.ContractPrice && p.AmountCents < 0);
        Assert.Empty(world.Set<SavedContractEscrow>());
    }

    [Fact]
    public void Version49MigrationCreatesContractTablesAndPreservesExistingInventoryAndWallet()
    {
        using TestWorld db = new();
        Fund(db, ContractAssetTestData.Pilot);
        long item = ContractAssetTestData.Stack(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldContractChange; DROP TABLE WorldContractPayment; DROP TABLE WorldContractEscrow;
                DROP TABLE WorldContractItem; DROP TABLE WorldContract; PRAGMA user_version=49;
                """
            );
        }
        WorldInitialization.Open(db.Path);
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(50, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(20, verify.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Equal(100_000_000, verify.Set<PilotWallet>().Find(ContractAssetTestData.Pilot)!.Cents);
        Assert.Empty(verify.Set<SavedContractEscrow>());
    }

    internal static void Fund(TestWorld db, long pilot, long cents = 100_000_000)
    {
        using WorldDbContext world = new(db.Path);
        var wallet = world.Set<PilotWallet>().Find(pilot) ?? world.Add(new PilotWallet { PilotId = pilot }).Entity;
        wallet.Cents = cents;
        world.SaveChanges();
    }

    internal static ContractDraft Draft(long item, long? assignee = ContractAssetTestData.Other) =>
        new(assignee, ContractAssetTestData.Station, 1440, 12345, 0, "Mining equipment", "Private sale", [new(item, 7)], []);

    [Fact]
    public void PrivateExchangeEscrowsAndTransfersPartialStacksAndIsDurableAndSingleUse()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        Fund(db, ContractAssetTestData.Pilot);
        Fund(db, ContractAssetTestData.Other);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(ContractAssetTestData.Pilot, Draft(item));
        var before = contracts.Details(ContractAssetTestData.Pilot, id);
        var offered = Assert.Single(before.Items);
        Assert.Equal(7, offered.Quantity);
        Assert.NotEqual(item, offered.ItemId);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Null(world.Set<OwnedItem>().Find(offered.ItemId));
            Assert.Equal(7, world.Set<OwnedItem>().IgnoreQueryFilters().Single(i => i.Id == offered.ItemId).Quantity);
            Assert.Equal(13, world.Set<OwnedItem>().Find(item)!.Quantity);
        }
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        contracts = reopened.GetRequiredService<IPilotContracts>();
        Assert.Equal(before.Contract, contracts.Details(ContractAssetTestData.Other, id).Contract);
        var accepted = contracts.Accept(ContractAssetTestData.Other, id);
        Assert.Equal(ContractStatus.Finished, accepted.Status);
        Assert.Equal(ContractAssetTestData.Other, accepted.AcceptorId);
        Assert.Equal(clock.Now, accepted.Completed);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(ContractAssetTestData.Other, world.Set<OwnedItem>().Find(offered.ItemId)!.OwnerId);
            Assert.Empty(world.Set<SavedContractEscrow>());
        }
        var wallet = reopened.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(100_000_000 - 1_000_000 + 12345, wallet.Balance(ContractAssetTestData.Pilot));
        Assert.Equal(100_000_000 - 12345, wallet.Balance(ContractAssetTestData.Other));
        Assert.Equal(2, wallet.Journal(ContractAssetTestData.Pilot).Contracts.Count);
        Assert.Throws<InvalidDataException>(() => contracts.Accept(ContractAssetTestData.Other, id));
        Assert.Equal(100_000_000 - 12345, wallet.Balance(ContractAssetTestData.Other));
    }
}
