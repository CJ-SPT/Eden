using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Repair;

public sealed class RepairTests
{
    private static readonly long s_now = DateTime.UtcNow.ToFileTimeUtc();
    private const long SpareShip = 2_000_000_000_500;

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public IPilotRepair Store => Db.Services.GetRequiredService<IPilotRepair>();
        public Fixture()
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[13,17]}}')");
            using WorldDbContext world = new(Db.Path);
            world.Add(new PilotWallet { PilotId = Pilot.PilotId, Cents = 100000 });
            world.Add(new ShipHealthState { ShipId = Pilot.ShipId, ShieldCharge = 77, ArmorDamage = 10, StructureDamage = 20, UpdatedAt = s_now });
            world.Add(new ShipEnergy { ShipId = Pilot.ShipId, Charge = 9, UpdatedAt = s_now });
            world.SaveChanges();
        }
        public RepairResult Run(long payment = 100000, params long[] ids) => Store.Repair(Pilot.PilotId, Pilot.StationId, ids.Length == 0 ? [Pilot.ShipId] : ids, payment, s_now);
        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void QuoteUsesBothDamageLayersAndDoesNotChangeHealthOrMoney()
    {
        using Fixture f = new();
        var quote = Assert.Single(f.Store.Quotes(f.Pilot.PilotId, f.Pilot.StationId, [f.Pilot.ShipId])[f.Pilot.ShipId]);
        Assert.Equal(30, quote.Damage);
        Assert.Equal(300, quote.MaxHealth);
        Assert.Equal(601, quote.TypeId);
        Assert.Empty(f.Store.Payments(f.Pilot.PilotId));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(100000, world.Set<PilotWallet>().Single().Cents);
        Assert.Equal(10, world.Set<ShipHealthState>().Single().ArmorDamage);
    }

    [Fact]
    public void FullRepairAndReceiptSurviveRestartWithoutRefillingShieldOrCapacitor()
    {
        using Fixture f = new();
        var before = f.Db.Services.GetRequiredService<IPilotInventory>().List(f.Pilot.PilotId, f.Pilot.StationId);
        var repaired = f.Run();
        Assert.Equal(97000, repaired.BalanceCents);
        Assert.Equal(new ShipRepair(f.Pilot.ShipId, 10, 20), Assert.Single(repaired.Ships));
        _ = new SqliteWorld(f.Db.Path);
        var paid = Assert.Single(f.Store.Payments(f.Pilot.PilotId));
        Assert.Equal(3000, paid.AmountCents);
        Assert.Equal(97000, paid.BalanceCents);
        Assert.Equal(before, f.Db.Services.GetRequiredService<IPilotInventory>().List(f.Pilot.PilotId, f.Pilot.StationId));
        using WorldDbContext world = new(f.Db.Path);
        var saved = world.Set<ShipHealthState>().Single();
        Assert.Equal(0, saved.ArmorDamage);
        Assert.Equal(0, saved.StructureDamage);
        Assert.Equal(77, saved.ShieldCharge);
        Assert.Equal(s_now, saved.UpdatedAt);
        Assert.Equal(9, world.Set<ShipEnergy>().Single().Charge);
        Assert.Contains("ArmorRepaired", world.Set<StationRepairPayment>().Single().RepairsJson, StringComparison.Ordinal);
        Assert.Empty(f.Run().Ships);
        Assert.Single(f.Store.Payments(f.Pilot.PilotId));
    }

    [Fact]
    public void PartialBudgetRepairsTheSameFractionOfEachLayerAndRoundsTheNativeQuote()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var saved = world.Set<ShipHealthState>().Single();
            saved.ArmorDamage = 10.2;
            saved.StructureDamage = 20.1;
            world.SaveChanges();
        }
        var half = Assert.Single(f.Run(1550).Ships);
        Assert.Equal(5.1, half.ArmorRepaired, 8);
        Assert.Equal(10.05, half.StructureRepaired, 8);
        var quote = Assert.Single(f.Store.Quotes(f.Pilot.PilotId, f.Pilot.StationId, [f.Pilot.ShipId])[f.Pilot.ShipId]);
        Assert.Equal(15.15, quote.Damage, 8);
        Assert.Equal(96850, f.Run().BalanceCents);
        Assert.Equal(new long[] { 1600, 1550 }, f.Store.Payments(f.Pilot.PilotId).Select(p => p.AmountCents));
    }

    [Fact]
    public void ZeroBudgetAndInsufficientFundsDoNotHealOrCreateReceipts()
    {
        using Fixture f = new();
        Assert.Empty(f.Run(0).Ships);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Single().Cents = 100;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Run(3000));
        Assert.Empty(f.Store.Payments(f.Pilot.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(10, saved.Set<ShipHealthState>().Single().ArmorDamage);
        Assert.Equal(20, saved.Set<ShipHealthState>().Single().StructureDamage);
        Assert.Equal(100, saved.Set<PilotWallet>().Single().Cents);
    }

    [Fact]
    public async Task CompetingStaleFullPaymentsRepairAndChargeOnlyOnce()
    {
        using Fixture f = new();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Run())));
        Assert.Single(results, r => r.Ships.Count > 0);
        Assert.All(results, r => Assert.Equal(97000, r.BalanceCents));
        Assert.Single(f.Store.Payments(f.Pilot.PilotId));
    }

    [Fact]
    public void MultipleOwnedHullsUseTheirTrainedHealthWithoutChangingTheActiveShip()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO ItemType(Id,Name,GroupId) VALUES (3392,'Mechanics',257); INSERT INTO TypeAttribute VALUES (3392,275,1)");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new PilotSkill { PilotId = f.Pilot.PilotId, TypeId = (int)DefenseSkill.Mechanics, Points = 8000 });
            world.Ships.Add(new() { Id = SpareShip, OwnerId = f.Pilot.PilotId, TypeId = 601, StationId = f.Pilot.StationId, Name = "Spare" });
            world.Add(new ShipHealthState { ShipId = SpareShip, ShieldCharge = 1, ArmorDamage = 40, StructureDamage = 30, UpdatedAt = s_now });
            world.SaveChanges();
        }
        long active = f.Pilot.ShipId;
        var quote = Assert.Single(f.Store.Quotes(f.Pilot.PilotId, f.Pilot.StationId, [SpareShip])[SpareShip]);
        Assert.Equal(70, quote.Damage);
        Assert.Equal(326.25, quote.MaxHealth, 8);
        var result = f.Run(5000, active, SpareShip);
        Assert.Equal(2, result.Ships.Count);
        Assert.Equal(active, f.Pilot.ShipId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(5, saved.Set<ShipHealthState>().Find(active)!.ArmorDamage);
        Assert.Equal(15, saved.Set<ShipHealthState>().Find(SpareShip)!.StructureDamage);
    }

    [Fact]
    public void UndamagedAssembledModulesHaveZeroQuotesAndCannotConsumePayment()
    {
        using Fixture f = new();
        long module;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Single(i => i.OwnerId == f.Pilot.PilotId && i.TypeId == InventoryDefinitions.CivilianMinerType);
            module = item.Id;
            Assert.Throws<InvalidDataException>(() => f.Store.Quotes(f.Pilot.PilotId, f.Pilot.StationId, [module]));
            item.Singleton = true;
            world.SaveChanges();
        }
        Assert.Equal(0, Assert.Single(f.Store.Quotes(f.Pilot.PilotId, f.Pilot.StationId, [module])[module]).Damage);
        Assert.Empty(f.Run(100000, module).Ships);
        Assert.Empty(f.Store.Payments(f.Pilot.PilotId));
    }

    [Fact]
    public void OwnershipLocationAndServiceFailuresRejectTheWholeBatch()
    {
        using Fixture f = new();
        var wing = f.Db.Store.FindPilot(1000002)!;
        Assert.Throws<InvalidDataException>(() => f.Run(100000, f.Pilot.ShipId, wing.ShipId));
        Assert.Throws<InvalidDataException>(() => f.Run(100000, f.Pilot.ShipId, f.Pilot.ShipId));
        Assert.Throws<InvalidDataException>(() => f.Store.Repair(f.Pilot.PilotId, f.Pilot.StationId + 1, [f.Pilot.ShipId], 100000, s_now));
        f.Db.Store.SetPosition(f.Pilot.ShipId, new(1, 2, 3));
        Assert.Throws<InvalidDataException>(() => f.Run());
        f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data='{{\"services\":[17]}}' WHERE Dataset='client/stationoperations'");
        }
        Assert.Throws<InvalidDataException>(() => f.Run());
        Assert.Empty(f.Store.Payments(f.Pilot.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(100000, saved.Set<PilotWallet>().Single().Cents);
        Assert.Equal(10, saved.Set<ShipHealthState>().Single().ArmorDamage);
    }

    [Fact]
    public void VersionTwentyOneUpgradePreservesDamageInventoryAndWalletWithoutRepairing()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldRepairPayment; PRAGMA user_version=21");
        }
        _ = new SqliteWorld(f.Db.Path);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Empty(f.Store.Payments(f.Pilot.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(22, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(100000, saved.Set<PilotWallet>().Single().Cents);
        Assert.Equal(10, saved.Set<ShipHealthState>().Single().ArmorDamage);
        Assert.Equal(20, saved.Set<ShipHealthState>().Single().StructureDamage);
    }

    [Fact]
    public void NativeQuotesPartialPaymentsHealthNotificationsAndRepairJournalUseSelectedPilot()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], p => new(f.Db.Store, p, TimeProvider.System, new(), f.Db.Objects), 2, []);
        object? Call(string service, string method, params object?[] args) => router.Call(context, service, method, args, _ => { });
        List<object?> ids = [pilot.ShipId];
        Assert.Same(PilotCallRouter.Unsupported, Call("repairSvc", "GetRepairQuotes", ids));
        context.Select(pilot);
        object?[] bind = [(long)pilot.StationId, (long)pilot.StationId, (long)RepairDefinitions.StationGroup];
        var binding = Assert.IsType<object?[]>(Call("repairSvc", "MachoBindObject", bind, null));
        var reference = Assert.IsType<BlueObjectReference>(binding[0]);
        Assert.Equal("repairSvc", context.ResolveReference(reference.Id));
        var quotes = Assert.IsType<Dictionary<long, object?>>(Call("repairSvc", "GetRepairQuotes", ids));
        var quote = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(quotes[pilot.ShipId])));
        var fields = Assert.IsType<Dictionary<string, object?>>(quote.State);
        Assert.Equal(30d, fields["damage"]);
        Assert.Equal(1d, fields["costToRepairOneUnitOfDamage"]);
        Assert.NotEmpty(BlueMarshal.Encode(quotes));
        Assert.Null(Call("repairSvc", "RepairItemsInStation", ids, 15d));
        Assert.Contains(context.Notifications, n => n.Name == "OnAccountChange" && Equals(n.Arguments[2], 985d));
        var attributes = Assert.IsType<List<object?>>(Assert.Single(context.Notifications, n => n.Name == "OnModuleAttributeChanges").Arguments[0]);
        Assert.Equal(new[] { 5d, 10d }, attributes.Cast<object?[]>().Select(a => (double)a[5]!));
        var journal = Assert.IsType<List<object?>>(Call("account", "GetTransactions", (long)WalletAccountKey.Cash, null, null, false));
        var row = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(Assert.Single(journal)).State);
        Assert.Equal((int)WalletEntryType.RepairBill, row["entryTypeID"]);
        Assert.Equal(-15d, row["amount"]);
        Assert.Equal(985d, row["balance"]);
        context.ReleaseLocationReferences();
        Assert.Null(context.ResolveReference(reference.Id));
        Assert.Same(PilotCallRouter.Unsupported, Call("repairSvc", "MachoBindObject", new object?[] { (long)pilot.StationId + 1, (long)pilot.StationId + 1, (long)RepairDefinitions.StationGroup }, null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public void InvalidPaymentsCannotReachTheWallet(double payment) => Assert.Throws<InvalidDataException>(() => RepairDefinitions.PaymentCents(payment));
}
