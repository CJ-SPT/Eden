using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Contracts;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class BulkContractTestData
{
    internal const long Issuer = ContractAssetTestData.Pilot;
    internal const long Buyer = ContractAssetTestData.Other;
    internal const long Funds = 1_000_000_000;
    internal const long Price = 123_456;

    internal static ContractDraft Draft(IReadOnlyList<long> hulls, bool isPrivate = true) =>
        new(
            isPrivate ? Buyer : null,
            ContractAssetTestData.Station,
            1440,
            Price,
            0,
            "Fitted hulls",
            "Price per ship",
            hulls.Select(id => new ContractOffer(id, 1)).ToArray(),
            [],
            MultiContract: true
        );

    internal static void Skills(TestWorld db, int level = 5, bool advanced = true)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            foreach (var skill in new[] { ContractSkill.Contracting, ContractSkill.AdvancedContracting })
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
                catalog.Add(
                    new TypeAttribute
                    {
                        ItemTypeId = (int)skill,
                        AttributeId = (int)DogmaAttributeId.SkillTimeConstant,
                        Value = 1,
                    }
                );
            }
            catalog.SaveChanges();
        }
        using WorldDbContext world = new(db.Path);
        world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
        world.Add(
            new PilotSkill
            {
                PilotId = Issuer,
                TypeId = (int)ContractSkill.Contracting,
                Points = level == 1 ? 250 : 256000,
            }
        );
        if (advanced)
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = Issuer,
                    TypeId = (int)ContractSkill.AdvancedContracting,
                    Points = 256000,
                }
            );
        }
        world.SaveChanges();
    }

    internal sealed class Fixture : IDisposable
    {
        internal TestClock Clock { get; } = CalendarTestData.Clock();
        internal TestWorld Db { get; }
        internal IPilotContracts Contracts { get; }
        internal long[] Hulls { get; }

        internal Fixture(int count = 2, bool skilled = true)
        {
            Db = new(clock: Clock);
            ContractTests.Fund(Db, Issuer, Funds);
            ContractTests.Fund(Db, Buyer, Funds);
            if (skilled)
            {
                Skills(Db);
            }
            Hulls = Enumerable.Range(0, count).Select(_ => ContractAssetTestData.Hull(Db)).ToArray();
            Contracts = Db.Services.GetRequiredService<IPilotContracts>();
        }

        internal long Balance(long pilot = Issuer) => Db.Services.GetRequiredService<IPilotWalletReader>().Balance(pilot);

        internal void Unchanged(long revision, long funds = Funds, int contracts = 0)
        {
            using WorldDbContext world = new(Db.Path);
            Assert.Equal(contracts, world.Set<SavedContract>().Count());
            Assert.Equal(contracts, world.Set<SavedContractEscrow>().Count());
            Assert.Equal(funds, Balance());
            Assert.Equal(revision, Contracts.Revision);
            foreach (long hull in Hulls)
            {
                Assert.NotNull(world.Ships.IgnoreQueryFilters().SingleOrDefault(s => s.Id == hull));
            }
        }

        public void Dispose() => Db.Dispose();
    }
}

public sealed class BulkContractTests
{
    [Fact]
    public void EffectiveAlphaSkillCapsApplyToTheWholeBatchWithoutLeavingAnAllowedPrefixCreated()
    {
        using BulkContractTestData.Fixture f = new(6);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            var alpha = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/clonegrades" && r.Key == "\"1\"");
            alpha.Data = "{\"skills\":[{\"typeID\":25235,\"level\":1},{\"typeID\":73912,\"level\":0}]}";
            catalog.SaveChanges();
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Alpha;
            world.SaveChanges();
        }
        long revision = f.Contracts.Revision;
        Assert.Throws<InvalidDataException>(() =>
            f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft(f.Hulls))
        );
        f.Unchanged(revision);
    }

    [Fact]
    public void SingleAndBatchEntryPointsCannotSilentlyChangeTheRequestedContractMode()
    {
        using BulkContractTestData.Fixture f = new();
        var draft = BulkContractTestData.Draft(f.Hulls);
        long revision = f.Contracts.Revision;
        Assert.Throws<InvalidDataException>(() => f.Contracts.Create(BulkContractTestData.Issuer, draft));
        Assert.Throws<InvalidDataException>(() =>
            f.Contracts.CreateBatch(BulkContractTestData.Issuer, draft with { MultiContract = false })
        );
        f.Unchanged(revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShipBatchCreatesIndependentEscrowContractsThatCanBeSoldOrCancelledAfterRestart(bool isPrivate)
    {
        using BulkContractTestData.Fixture f = new();
        long module = ContractAssetTestData.Stack(f.Db, 1, f.Hulls[0], flag: (int)InventoryFlag.HighSlot0);
        long charges = ContractAssetTestData.Stack(f.Db, 40, module, flag: (int)InventoryFlag.Cargo);
        long carried = ContractAssetTestData.Hull(f.Db, f.Hulls[1]);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Singleton = true;
            world.Set<OwnedItem>().Find(module)!.Online = true;
            world.Add(
                new ShipHealthState
                {
                    ShipId = f.Hulls[0],
                    StructureDamage = 3,
                    ArmorDamage = 5,
                }
            );
            world.SaveChanges();
        }
        var draft = BulkContractTestData.Draft(f.Hulls, isPrivate);
        var fees = ContractRules.Fees(draft, new(5, 5, 0, 0));
        long revision = f.Contracts.Revision;
        var ids = f.Contracts.CreateBatch(BulkContractTestData.Issuer, draft);
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(revision + 1, f.Contracts.Revision);
        Assert.Equal(BulkContractTestData.Funds - 2 * (fees.BrokerCents + fees.DepositCents), f.Balance());
        Assert.Equal(2, f.Contracts.Page(BulkContractTestData.Issuer).Outstanding);
        for (int i = 0; i < ids.Count; i++)
        {
            var details = f.Contracts.Details(BulkContractTestData.Issuer, ids[i]);
            Assert.Equal(f.Hulls[i], Assert.Single(details.Items, item => item.ParentId is null).ItemId);
            Assert.Equal(BulkContractTestData.Price, details.Contract.PriceCents);
            Assert.Equal(fees, details.Contract.Fees);
        }
        WorldInitialization.Open(f.Db.Path);
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        var contracts = reopened.GetRequiredService<IPilotContracts>();
        contracts.Accept(BulkContractTestData.Buyer, ids[0]);
        contracts.Delete(BulkContractTestData.Issuer, ids[1]);
        Assert.Equal(BulkContractTestData.Funds - BulkContractTestData.Price, f.Balance(BulkContractTestData.Buyer));
        Assert.Equal(
            BulkContractTestData.Funds - 2 * fees.BrokerCents - fees.DepositCents - fees.SalesTaxCents + BulkContractTestData.Price,
            f.Balance()
        );
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(BulkContractTestData.Buyer, verify.Ships.Find(f.Hulls[0])!.OwnerId);
        Assert.Equal(BulkContractTestData.Buyer, verify.Set<OwnedItem>().Find(module)!.OwnerId);
        Assert.Equal(module, verify.Set<OwnedItem>().Find(charges)!.LocationId);
        Assert.Equal(40, verify.Set<OwnedItem>().Find(charges)!.Quantity);
        Assert.True(verify.Set<OwnedItem>().Find(module)!.Online);
        Assert.Equal(3, verify.Set<ShipHealthState>().Find(f.Hulls[0])!.StructureDamage);
        Assert.Equal(BulkContractTestData.Issuer, verify.Ships.Find(f.Hulls[1])!.OwnerId);
        Assert.Equal(f.Hulls[1], verify.Ships.Find(carried)!.StoredInId);
        Assert.Equal(BulkContractTestData.Issuer, verify.Ships.Find(carried)!.OwnerId);
        Assert.Empty(verify.Set<SavedContractEscrow>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public void TheNativeBatchLimitIncludesBothOneAndThirtyShips(int count)
    {
        using BulkContractTestData.Fixture f = new(count);
        var ids = f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft(f.Hulls));
        Assert.Equal(count, ids.Count);
        Assert.Equal(count, f.Contracts.Page(BulkContractTestData.Issuer).Outstanding);
        Assert.Equal(BulkContractTestData.Funds - count * 1_000_000, f.Balance());
        Assert.Equal(count, f.Contracts.Page(BulkContractTestData.Buyer).Assigned.Count);
    }

    [Fact]
    public void ASecondChildExceedingAvailableSkillSlotsRollsBackTheEntireBatch()
    {
        using BulkContractTestData.Fixture f = new(skilled: false);
        BulkContractTestData.Skills(f.Db, 1, advanced: false);
        for (int i = 0; i < 4; i++)
        {
            long hull = ContractAssetTestData.Hull(f.Db);
            f.Contracts.Create(BulkContractTestData.Issuer, BulkContractTestData.Draft([hull]) with { MultiContract = false });
        }
        long revision = f.Contracts.Revision;
        Assert.Throws<InvalidDataException>(() =>
            f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft(f.Hulls))
        );
        f.Unchanged(revision, BulkContractTestData.Funds - 4_000_000, 4);
        Assert.Equal(4, f.Contracts.Page(BulkContractTestData.Issuer).Outstanding);
    }

    [Fact]
    public void ASecondChildWithInsufficientFundsRollsBackFeesGoodsAndAssignmentFeeds()
    {
        using BulkContractTestData.Fixture f = new();
        ContractTests.Fund(f.Db, BulkContractTestData.Issuer, 1_500_000);
        long revision = f.Contracts.Revision;
        long cursor = f.Contracts.Observe(BulkContractTestData.Buyer);
        Assert.Throws<InvalidDataException>(() =>
            f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft(f.Hulls))
        );
        f.Unchanged(revision, 1_500_000);
        Assert.Empty(f.Contracts.Changes(BulkContractTestData.Buyer, cursor).Items);
        Assert.Empty(f.Contracts.Page(BulkContractTestData.Buyer).Assigned);
    }

    [Fact]
    public void ASecondChildsForeignNestedContentsCannotLeaveTheFirstShipEscrowed()
    {
        using BulkContractTestData.Fixture f = new();
        long foreign = ContractAssetTestData.Stack(f.Db, 1, f.Hulls[1], BulkContractTestData.Buyer);
        long revision = f.Contracts.Revision;
        Assert.Throws<InvalidDataException>(() =>
            f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft(f.Hulls))
        );
        f.Unchanged(revision);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(BulkContractTestData.Buyer, world.Set<OwnedItem>().Find(foreign)!.OwnerId);
        Assert.Empty(world.Set<SavedContractItem>());
        Assert.Empty(world.Set<SavedContractPayment>());
    }

    [Fact]
    public void ASecondChildsLedgerFailureRollsBackEveryContractWalletAndFeedRow()
    {
        using BulkContractTestData.Fixture f = new();
        using var sql = new SqliteConnection($"Data Source={f.Db.Path};Pooling=false");
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_bulk BEFORE INSERT ON WorldContractPayment WHEN NEW.Type=72 "
            + "AND (SELECT COUNT(*) FROM WorldContract)>1 AND NEW.ContractId=(SELECT MAX(Id) FROM WorldContract) "
            + "BEGIN SELECT RAISE(ABORT,'second fee failed'); END";
        command.ExecuteNonQuery();
        long revision = f.Contracts.Revision;
        Assert.Throws<DbUpdateException>(() => f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft(f.Hulls)));
        f.Unchanged(revision);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedContractChange>());
        Assert.Empty(world.Set<SavedContractPayment>());
        Assert.Empty(world.Set<SavedContractItem>());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("active")]
    [InlineData("packaged")]
    [InlineData("capsule")]
    [InlineData("module")]
    public void InvalidBulkShipRootsRejectWithoutCreatingAnyContracts(string scenario)
    {
        using BulkContractTestData.Fixture f = new();
        long invalid = f.Hulls[1];
        if (scenario == "capsule")
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
            catalog.Add(
                new ItemGroup
                {
                    Id = FleetDefinitions.CapsuleGroup,
                    Name = "Capsule",
                    CategoryId = (int)InventoryCategory.Ship,
                }
            );
            catalog.Add(
                new ItemType
                {
                    Id = FleetDefinitions.CapsuleType,
                    Name = "Capsule",
                    GroupId = FleetDefinitions.CapsuleGroup,
                }
            );
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/types",
                    Key = FleetDefinitions.CapsuleType.ToString(),
                    Data = "{\"volume\":1000}",
                }
            );
            catalog.SaveChanges();
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (scenario == "foreign")
            {
                world
                    .Ships.Where(s => s.Id == invalid)
                    .ExecuteUpdate(update => update.SetProperty(s => s.OwnerId, BulkContractTestData.Buyer));
            }
            else if (scenario == "capsule")
            {
                world.Ships.Find(invalid)!.TypeId = FleetDefinitions.CapsuleType;
                world.SaveChanges();
            }
        }
        if (scenario == "active")
        {
            invalid = f.Db.Store.FindPilot(1000001)!.ShipId;
        }
        if (scenario is "packaged" or "module")
        {
            invalid = ContractAssetTestData.Stack(f.Db, 1);
            if (scenario == "packaged")
            {
                using WorldDbContext world = new(f.Db.Path);
                world.Set<OwnedItem>().Find(invalid)!.TypeId = 601;
                world.SaveChanges();
            }
        }
        long revision = f.Contracts.Revision;
        Assert.Throws<InvalidDataException>(() =>
            f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft([f.Hulls[0], invalid]))
        );
        f.Unchanged(revision);
    }

    [Fact]
    public async Task CompetingBatchesCannotOfferOrChargeTheSameShipsTwice()
    {
        using BulkContractTestData.Fixture f = new();
        Task<bool> Create() =>
            Task.Run(() =>
            {
                try
                {
                    f.Contracts.CreateBatch(BulkContractTestData.Issuer, BulkContractTestData.Draft(f.Hulls));
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            });
        bool[] result = await Task.WhenAll(Create(), Create());
        Assert.Single(result, success => success);
        Assert.Equal(BulkContractTestData.Funds - 2_000_000, f.Balance());
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(2, world.Set<SavedContract>().Count());
        Assert.Equal(2, world.Set<SavedContractEscrow>().Count());
    }
}
