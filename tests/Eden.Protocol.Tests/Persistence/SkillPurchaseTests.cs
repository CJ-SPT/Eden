using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SkillPurchaseTests
{
    private const long Pilot = 90000001;
    private const int ShieldOperation = 3416;
    private const int Trade = 3443;
    private const long InitialBalance = 10_000_000;
    private const long ShieldCost = 130_000;
    private const long TradeCost = 3_900_000;

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public IPilotSkillPurchases Purchases => Db.Services.GetRequiredService<IPilotSkillPurchases>();

        public Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (3416,'Shield Operation',257),(3443,'Trade',257);
                    INSERT INTO TypeAttribute VALUES (3416,275,1),(3443,275,1);
                    INSERT INTO SourceRecord VALUES
                        ('client/types','3416','{{"published":1,"basePrice":1000}}'),
                        ('client/types','3443','{{"published":1,"basePrice":30000}}'),
                        ('client/requiredskillsfortypes','3416','{{"3327":4}}'),
                        ('client/typelist','93','{{"includedTypeIDs":[3416,3443,3327]}}');
                    """
                );
            }
            using WorldDbContext world = new(Db.Path);
            world.Add(new PilotWallet { PilotId = SkillPurchaseTests.Pilot, Cents = InitialBalance });
            world.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void DirectPurchaseInjectsAtZeroAndPersistsChargesWithoutCreatingBooks()
    {
        using Fixture f = new();
        int items;
        using (WorldDbContext before = new(f.Db.Path))
        {
            items = before.Set<OwnedItem>().Count();
        }
        var purchase = f.Purchases.Purchase(Pilot, [ShieldOperation, Trade]);
        Assert.Equal(new[] { ShieldOperation, Trade }, purchase.TypeIds);
        Assert.Equal(InitialBalance - ShieldCost - TradeCost, purchase.BalanceCents);
        Assert.Throws<InvalidTrainingQueue>(() =>
            f.Db.Services.GetRequiredService<IPilotTraining>().Save(Pilot, [new(ShieldOperation, 1)], true)
        );
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<PilotSkill>().Find(Pilot, ShieldOperation)!.Points);
        Assert.Equal(0, saved.Set<PilotSkill>().Find(Pilot, Trade)!.Points);
        Assert.Equal(items, saved.Set<OwnedItem>().Count());
        Assert.Equal(purchase.BalanceCents, saved.Set<PilotWallet>().Find(Pilot)!.Cents);
        var payments = saved.Set<SavedServicePayment>().OrderBy(p => p.Id).ToArray();
        Assert.Equal(new[] { -ShieldCost, -TradeCost }, payments.Select(p => p.AmountCents));
        Assert.All(payments, p => Assert.Equal(WalletEntryType.SkillPurchase, p.Kind));
        Assert.Equal(new long[] { ShieldOperation, Trade }, payments.Select(p => p.ContextId));
        Assert.Equal(new[] { InitialBalance - ShieldCost, purchase.BalanceCents }, payments.Select(p => p.BalanceCents));
        Assert.Equal(2, f.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(Pilot).ServicePayments!.Count);
        Assert.Equal(2, TaskTrackingTestData.Count(f.Db, TaskEvents.SkillInjected));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("known")]
    [InlineData("missing")]
    [InlineData("nonSkill")]
    [InlineData("unavailable")]
    [InlineData("unpublished")]
    [InlineData("price")]
    [InlineData("rank")]
    [InlineData("funds")]
    [InlineData("negative")]
    public void RejectedBatchLeavesWalletJournalAndSkillsUnchanged(string reason)
    {
        using Fixture f = new();
        int[] ids = [ShieldOperation, Trade];
        long balance = reason == "funds" ? ShieldCost + TradeCost - 1 : InitialBalance;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(Pilot)!.Cents = balance;
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            switch (reason)
            {
                case "duplicate":
                    ids = [ShieldOperation, ShieldOperation];
                    break;
                case "known":
                    ids = [ShieldOperation, 3327];
                    break;
                case "missing":
                    ids = [ShieldOperation, int.MaxValue];
                    break;
                case "nonSkill":
                    ids = [ShieldOperation, InventoryDefinitions.CivilianMinerType];
                    break;
                case "negative":
                    ids = [ShieldOperation, -1];
                    break;
                case "unavailable":
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data='{{\"includedTypeIDs\":[3416]}}' WHERE Dataset='client/typelist' AND Key='93'"
                    );
                    break;
                case "unpublished":
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data='{{\"published\":0,\"basePrice\":30000}}' "
                            + "WHERE Dataset='client/types' AND Key='3443'"
                    );
                    break;
                case "price":
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data='{{\"published\":1,\"basePrice\":0}}' "
                            + "WHERE Dataset='client/types' AND Key='3443'"
                    );
                    break;
                case "rank":
                    catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=3443 AND AttributeId=275");
                    break;
            }
        }
        Assert.Throws<InvalidDataException>(() => f.Purchases.Purchase(Pilot, ids));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(balance, saved.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Null(saved.Set<PilotSkill>().Find(Pilot, ShieldOperation));
        Assert.Null(saved.Set<PilotSkill>().Find(Pilot, Trade));
        Assert.Empty(saved.Set<SavedServicePayment>());
        Assert.Equal(0, TaskTrackingTestData.Count(f.Db, TaskEvents.SkillInjected));
    }

    [Fact]
    public void FailedWriteRollsBackChargesAndAllInjectedSkills()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectPurchasedSkill BEFORE INSERT ON WorldPilotSkill WHEN NEW.TypeId=3443 "
                    + "BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Purchases.Purchase(Pilot, [ShieldOperation, Trade]));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(InitialBalance, saved.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Null(saved.Set<PilotSkill>().Find(Pilot, ShieldOperation));
        Assert.Null(saved.Set<PilotSkill>().Find(Pilot, Trade));
        Assert.Empty(saved.Set<SavedServicePayment>());
        Assert.Equal(0, TaskTrackingTestData.Count(f.Db, TaskEvents.SkillInjected));
    }

    [Fact]
    public async Task CompetingPurchasesChargeAndInjectOnlyOnce()
    {
        using Fixture f = new();
        var attempts = Enumerable
            .Range(0, 2)
            .Select(_ =>
                Task.Run(() =>
                {
                    try
                    {
                        f.Purchases.Purchase(Pilot, [ShieldOperation]);
                        return true;
                    }
                    catch (InvalidDataException)
                    {
                        return false;
                    }
                })
            )
            .ToArray();
        Assert.Single(await Task.WhenAll(attempts), success => success);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(InitialBalance - ShieldCost, saved.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Single(saved.Set<SavedServicePayment>());
        Assert.Equal(0, saved.Set<PilotSkill>().Find(Pilot, ShieldOperation)!.Points);
    }

    [Theory]
    [InlineData("{\"includedCategoryIDs\":[16]}", true)]
    [InlineData("{\"includedGroupIDs\":[257]}", true)]
    [InlineData("{\"includedCategoryIDs\":[16],\"excludedTypeIDs\":[3443]}", false)]
    [InlineData("{\"includedCategoryIDs\":[16],\"excludedGroupIDs\":[257],\"includedTypeIDs\":[3416]}", false)]
    public void CatalogAvailabilityHonorsCategoryGroupAndTypeExclusions(string list, bool available)
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data={0} WHERE Dataset='client/typelist' AND Key='93'", list);
        }
        if (available)
        {
            Assert.Equal(new[] { ShieldOperation, Trade }, f.Purchases.Purchase(Pilot, [ShieldOperation, Trade]).TypeIds);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => f.Purchases.Purchase(Pilot, [ShieldOperation, Trade]));
            using WorldDbContext saved = new(f.Db.Path);
            Assert.Equal(InitialBalance, saved.Set<PilotWallet>().Find(Pilot)!.Cents);
            Assert.Null(saved.Set<PilotSkill>().Find(Pilot, ShieldOperation));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePurchaseReturnsListAndNotifiesSkillsAndWalletWhileDockedOrInSpace(bool inSpace)
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        if (inSpace)
        {
            f.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
            pilot = f.Db.Store.FindPilot(1000001)!;
        }
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, TimeProvider.System, new(), []),
            7,
            SignedSettings.Empty
        );
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object?[] args = [new List<object?> { (long)ShieldOperation, (long)Trade }];
        Assert.Same(PilotCallResult.Unsupported, router.CallWire(context, "@skills", "PurchaseSkills", args, _ => { }));
        context.Select(pilot);
        var reply = Assert.IsType<List<object?>>(router.CallWire(context, "@skills", "PurchaseSkills", args, _ => { }));
        Assert.Equal(new object?[] { ShieldOperation, Trade }, reply);
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        Assert.Equal(new[] { "OnServerSkillsChanged", "OnAccountChange" }, context.Notifications.Select(n => n.Name));
        var skills = Assert.IsType<Dictionary<long, object?>>(context.Notifications[0].Arguments[0]);
        Assert.Equal(new long[] { ShieldOperation, Trade }, skills.Keys.Order());
        Assert.NotEmpty(BlueMarshal.Encode(skills));
        var shield = Assert.IsType<BlueConstructor>(skills[ShieldOperation]);
        Assert.Equal(0, shield.Arguments[1]);
        Assert.Equal(0L, shield.Arguments[2]);
        Assert.Equal(
            (double)(InitialBalance - ShieldCost - TradeCost) / MarketDefinitions.CentsPerIsk,
            context.Notifications[1].Arguments[2]
        );
        var wallet = scope.ServiceProvider.GetRequiredService<WalletHandler>();
        var journal = Assert.IsType<PilotCallResult.Success>(
            wallet.Call(context, "account", "GetTransactions", [1000L, null, null, false])
        );
        Assert.Equal(2, Assert.IsType<BlueList<WalletJournalEntry>>(journal.Reply).Items.Count);
        Assert.NotEmpty(BlueMarshal.Encode(journal.Reply));
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(router.CallWire(context, "@skills", "PurchaseSkills", args, _ => { }));
        Assert.Empty(context.Notifications);
        var empty = Assert.IsType<List<object?>>(
            router.CallWire(context, "@skills", "PurchaseSkills", new object?[] { new List<object?>() }, _ => { })
        );
        Assert.Empty(empty);
        Assert.Empty(context.Notifications);
        Assert.IsType<PilotCallResult.Failure>(
            router.CallWire(context, "@skills", "PurchaseSkills", new object?[] { new List<object?> { long.MaxValue } }, _ => { })
        );
        Assert.IsType<PilotCallResult.Failure>(
            router.CallWire(context, "@skills", "PurchaseSkills", new object?[] { new object?[] { (long)Trade } }, _ => { })
        );
        Assert.Empty(context.Notifications);
    }
}
