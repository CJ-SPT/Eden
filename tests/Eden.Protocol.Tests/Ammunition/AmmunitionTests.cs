using System.Text.Json;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Ammunition;

public sealed class AmmunitionTests
{
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Milliseconds;

        public override DateTimeOffset GetUtcNow() => _start.AddMilliseconds(Milliseconds);

        public long Now => GetUtcNow().UtcDateTime.ToFileTimeUtc();
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public Clock Clock { get; } = new();
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        public IPilotFitting Fitting => Db.Services.GetRequiredService<IPilotFitting>();
        public IPilotAmmunition Ammo => Db.Services.GetRequiredService<IPilotAmmunition>();
        public IPilotCombat Combat => Db.Services.GetRequiredService<IPilotCombat>();
        public IPilotMarket Market => Db.Services.GetRequiredService<IPilotMarket>();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public NpcContact Npc { get; }
        public long Gun { get; }

        public Fixture()
        {
            NpcEncounterTests.Prepare(Db);
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemCategory VALUES (8,'Charge');
                    INSERT INTO ItemGroup VALUES (74,'Hybrid Weapon',7),(85,'Hybrid Charge',8);
                    INSERT INTO ItemType(Id,Name,GroupId,Capacity) VALUES (561,'75mm Gatling Rail I',74,0.5),(215,'Iron Charge S',85,0),
                        (222,'Antimatter Charge S',85,0);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (3300,'Gunnery',257),(3301,'Small Hybrid Turret',257),
                        (3316,'Controlled Bursts',257);
                    INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (6,'capacitorNeed'),(11,'powerOutput'),(14,'hiSlots'),(30,'power'),
                        (48,'cpuOutput'),(50,'cpu'),(56,'chargeRate'),(120,'weaponRangeMultiplier'),(128,'chargeSize'),
                            (182,'requiredSkill1'),
                        (277,'requiredSkill1Level'),(317,'capNeedBonus'),(422,'techLevel'),(604,'chargeGroup1'),(805,'quantity'),
                            (1795,'reloadTime'),(1826,'rookieSHTOptimalBonus');
                    INSERT INTO TypeAttribute VALUES (601,11,23),(601,14,2),(601,48,135),(601,1826,20),
                        (561,6,1.169),(561,30,2),(561,50,5),(561,51,2600),(561,54,6000),(561,56,1),(561,64,1.65),(561,128,1),
                        (561,158,3000),(561,160,136.5),(561,182,3301),(561,277,1),(561,422,1),(561,604,85),(561,620,40000),(561,1795,5000),
                        (215,117,3),(215,118,2),(215,120,1.6),(215,128,1),(215,317,-30),
                        (222,117,7),(222,118,5),(222,120,0.5),(222,128,1),(222,317,0),
                        (3300,275,1),(3301,275,1),(3316,275,1);
                    UPDATE TypeAttribute SET Value=10000 WHERE ItemTypeId=30380 AND AttributeId=263;
                    INSERT INTO SourceRecord VALUES ('client/types','561','{{"volume":5}}'),('client/types','215','{{"volume":0.0025}}'),
                        ('client/types','222','{{"volume":0.0025}}'),('client/stationoperations','14','{{"services":[7,17]}}');
                    """
                );
            }
            using (WorldDbContext world = new(Db.Path))
            {
                world.Database.ExecuteSqlRaw(
                    "INSERT INTO WorldPilotSkill VALUES (90000001,3300,250),(90000001,3301,1415),(90000001,3316,250)"
                );
                world.Add(new PilotWallet { PilotId = Pilot.PilotId, Cents = 1000000 });
                world.SaveChanges();
            }
            var purchase = Market.Buy(Pilot.PilotId, Pilot.StationId, [new(561, 100000, 1)]);
            Gun = Assert.Single(purchase.Changes).Item.Id;
            Fitting.Fit(Pilot.PilotId, [Gun], Pilot.StationId, (int)InventoryFlag.AutoFit);
            Fitting.SetOnline(Pilot.PilotId, Pilot.ShipId, Gun, true);
            Npc = Db.Services.GetRequiredService<INpcEncounters>().List(30000144, Clock.Now)[0];
        }

        public long Buy(int type, int quantity) =>
            Assert.Single(Market.Buy(Pilot.PilotId, Pilot.StationId, [new(type, 100, quantity)]).Changes).Item.Id;

        public AmmunitionTransfer Load(int type = 222, int quantity = 250)
        {
            long stack = Buy(type, quantity);
            return Ammo.Load(Pilot.PilotId, Pilot.ShipId, [Gun], [stack], Pilot.StationId, Clock.Now);
        }

        public void Space() => Db.Store.SetPosition(Pilot.ShipId, Npc.Position + new SpacePosition(1000, 0, 0), Npc.SystemId);

        public CombatShot Fire(double roll = 0.5) =>
            Combat.Fire(Pilot.PilotId, Gun, Npc.Id, SpacePosition.Zero, Clock.Now, roll, WeaponActivation.Hybrid);

        public int Total(int type)
        {
            using WorldDbContext world = new(Db.Path);
            return world.Set<OwnedItem>().Where(i => i.OwnerId == Pilot.PilotId && i.TypeId == type).Sum(i => i.Quantity);
        }

        public PilotCallContext Context()
        {
            var pilot = Pilot;
            PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, Clock, new(), Db.Objects),
                7,
                SignedSettings.Empty
            );
            context.Select(pilot);
            return context;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void MarketMagazineReloadAndUnloadingPreserveRealRoundsAndRestartDeadlines()
    {
        using Fixture f = new();
        var transfer = f.Load();
        var magazine = Assert.Single(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Equal(200, magazine.Quantity);
        Assert.Equal(5000, transfer.ReloadMilliseconds);
        Assert.Equal(f.Clock.Now + 5000 * TimeSpan.TicksPerMillisecond, magazine.ReadyAt);
        Assert.Equal(50, Assert.Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.TypeId == 222).Quantity);
        Assert.Equal(250, f.Total(222));
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(magazine, Assert.Single(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId)));
        Assert.Throws<InvalidDataException>(() =>
            f.Ammo.Unload(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], f.Pilot.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now)
        );
        f.Clock.Milliseconds = 5000;
        f.Ammo.Unload(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], f.Pilot.ShipId, (int)InventoryFlag.Cargo, 17, f.Clock.Now);
        Assert.Equal(183, Assert.Single(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId)).Quantity);
        Assert.Equal(17, Assert.Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId), i => i.TypeId == 222).Quantity);
        f.Ammo.Unload(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], f.Pilot.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        Assert.Empty(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Empty(
            f.Ammo.Unload(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], f.Pilot.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now).Items
        );
        Assert.Equal(250, f.Total(222));
        Assert.Equal(875000, f.Market.Balance(f.Pilot.PilotId));
    }

    [Fact]
    public void AmmoChangesRangeCapacitorAndDamageWhileEachShotConsumesOneRealRoundEvenOnAMiss()
    {
        using Fixture f = new();
        f.Load(quantity: 2);
        f.Space();
        Assert.Throws<InvalidDataException>(() => f.Fire());
        f.Clock.Milliseconds = 5000;
        var shot = f.Fire();
        Assert.Equal(222, shot.Weapon.ChargeTypeId);
        Assert.Equal((int)DogmaEffect.ProjectileFired, shot.Weapon.EffectId);
        Assert.Equal(3600, shot.Weapon.Optimal, 8);
        Assert.Equal(2548, shot.Weapon.CycleMilliseconds, 8);
        Assert.Equal(7 * 1.65 * 1.1, shot.Weapon.Damage.Kinetic, 8);
        Assert.Equal(1.169 * 0.95, shot.Weapon.CapacitorNeed, 8);
        Assert.Equal(1, shot.Magazine!.Quantity);
        Assert.Equal(1, f.Total(222));
        Assert.Throws<InvalidDataException>(() => f.Fire());
        f.Clock.Milliseconds += 2548;
        f.Db.Store.SetPosition(f.Pilot.ShipId, f.Npc.Position + new SpacePosition(10000, 0, 0), f.Npc.SystemId);
        var miss = f.Fire(0.999999);
        Assert.Equal(0, miss.Applied);
        Assert.Equal(0, miss.Magazine!.Quantity);
        Assert.Equal(0, f.Total(222));
        Assert.Empty(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        f.Clock.Milliseconds += 2548;
        Assert.Throws<InvalidDataException>(() => f.Fire());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RearrangingLoadedGunsPreservesRoundsAndDeadlinesAndMovesNativeSublocations(bool swap)
    {
        using Fixture f = new();
        f.Load((int)HybridChargeType.IronSmall, 10);
        long? other = null;
        if (swap)
        {
            other = Assert
                .Single(
                    f.Market.Buy(f.Pilot.PilotId, f.Pilot.StationId, [new((int)CombatWeaponType.GatlingRailgun75mm, 100000, 1)]).Changes
                )
                .Item.Id;
            f.Fitting.Fit(f.Pilot.PilotId, [other.Value], f.Pilot.StationId, (int)InventoryFlag.HighSlot1);
            long rounds = f.Buy((int)HybridChargeType.IronSmall, 7);
            f.Ammo.Load(f.Pilot.PilotId, f.Pilot.ShipId, [other.Value], [rounds], f.Pilot.StationId, f.Clock.Now);
        }
        var before = f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId);
        var magazines = f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId);
        var attributes = f.Fitting.ShipAttributes(f.Pilot.PilotId);
        long balance = f.Market.Balance(f.Pilot.PilotId);
        using var context = f.Context();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Null(
            router.CallWire(
                context,
                "@ship",
                "Add",
                new object?[] { f.Gun, f.Pilot.ShipId },
                _ => { },
                new() { ["qty"] = 1L, ["flag"] = (long)InventoryFlag.HighSlot1 }
            )
        );
        Assert.DoesNotContain(context.Notifications, n => n.Name == "OnGodmaPrimeItem");
        Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "ItemGetInfo", new object?[] { f.Gun }, _ => { }));
        if (other.HasValue)
        {
            Assert.DoesNotContain(context.Notifications, n => n.Name == "OnGodmaPrimeItem");
            Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "ItemGetInfo", new object?[] { other.Value }, _ => { }));
        }
        var after = f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId);
        Assert.Equal(
            before.Select(i => i with { FlagId = i.Id == f.Gun ? (int)InventoryFlag.HighSlot1 : (int)InventoryFlag.HighSlot0 }),
            after
        );
        Assert.Equal(attributes.OrderBy(a => a.Key), f.Fitting.ShipAttributes(f.Pilot.PilotId).OrderBy(a => a.Key));
        Assert.Equal(balance, f.Market.Balance(f.Pilot.PilotId));
        Assert.Equal(swap ? 17 : 10, f.Total((int)HybridChargeType.IronSmall));
        var expected = magazines
            .Select(m => m with { FlagId = m.ModuleId == f.Gun ? (int)InventoryFlag.HighSlot1 : (int)InventoryFlag.HighSlot0 })
            .OrderBy(m => m.FlagId)
            .ToArray();
        Assert.Equal(expected, f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        var quantities = context
            .Notifications.SelectMany(
                (n, index) =>
                    n.Name == "OnModuleAttributeChanges"
                        ? ((List<object?>)n.Arguments[0]!)
                            .Cast<object?[]>()
                            .Where(a => a[2] is object?[])
                            .Select(a => (Index: index, Change: a))
                        : []
            )
            .ToArray();
        int firstItem = context.Notifications.FindIndex(n => n.Name == "OnItemChange");
        int lastItem = context.Notifications.FindLastIndex(n => n.Name == "OnItemChange");
        var itemRows = context
            .Notifications.Where(n => n.Name == "OnItemChange")
            .Select(n =>
                (Row: (object?[])((BlueConstructor)n.Arguments[0]!).Arguments[1]!, Old: (Dictionary<long, object?>)n.Arguments[1]!)
            )
            .ToArray();
        int movedCount = swap ? 2 : 1;
        Assert.Equal(movedCount * 2, itemRows.Length);
        Assert.All(itemRows.Take(movedCount), i => Assert.Equal((int)InventoryFlag.Cargo, i.Row[(int)InventoryColumn.Flag]));
        Assert.All(itemRows.Skip(movedCount), i => Assert.Equal((int)InventoryFlag.Cargo, i.Old[(long)InventoryColumn.Flag]));
        var removed = quantities.Where(q => Equals(q.Change[5], 0d)).ToArray();
        Assert.Equal(magazines.Count, removed.Length);
        Assert.All(removed, q => Assert.True(q.Index < firstItem));
        Assert.All(
            magazines,
            m => Assert.Contains(removed, q => ((object?[])q.Change[2]!).SequenceEqual((object?[])MagazineWire.Key(m).ToBlue()))
        );
        var primes = context
            .Notifications.Select((n, index) => (Notification: n, Index: index))
            .Where(n => n.Notification.Name == "OnGodmaPrimeItem")
            .ToArray();
        Assert.Equal(expected.Length, primes.Length);
        Assert.All(primes, n => Assert.True(n.Index > lastItem));
        Assert.All(
            expected,
            m =>
                Assert.Contains(
                    quantities,
                    q =>
                        q.Index > lastItem
                        && ((object?[])q.Change[2]!).SequenceEqual((object?[])MagazineWire.Key(m).ToBlue())
                        && Equals(q.Change[5], (double)m.Quantity)
                )
        );
        Assert.DoesNotContain(context.Notifications, n => n.Name == "OnChargeBeingLoadedToModule");
        Assert.All(context.Notifications, n => Assert.NotEmpty(BlueMarshal.Encode(n.Arguments)));
        Assert.Empty(f.Fitting.Fit(f.Pilot.PilotId, [f.Gun], f.Pilot.ShipId, (int)InventoryFlag.HighSlot1));
        Assert.Empty(f.Fitting.Fit(f.Pilot.PilotId, [f.Gun], f.Pilot.ShipId, (int)InventoryFlag.AutoFit));
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(after, f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Equal(expected, f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        f.Space();
        Assert.Throws<InvalidDataException>(() => f.Fire());
        f.Clock.Milliseconds = 5000;
        Assert.Equal(9, f.Fire().Magazine!.Quantity);
        Assert.Equal(swap ? 16 : 9, f.Total((int)HybridChargeType.IronSmall));
    }

    [Theory]
    [InlineData("space")]
    [InlineData("foreign")]
    [InlineData("wrong rack")]
    [InlineData("outside rack")]
    [InlineData("malformed")]
    public void RejectedSlotMovesRetainEveryItemAndPaidReload(string reason)
    {
        using Fixture f = new();
        f.Load((int)HybridChargeType.IronSmall, 10);
        if (reason == "space")
        {
            f.Space();
        }
        using WorldDbContext world = new(f.Db.Path);
        if (reason == "foreign")
        {
            world.Set<OwnedItem>().Find(f.Gun)!.OwnerId = 90000002;
        }
        if (reason == "malformed")
        {
            world.Set<OwnedItem>().Find(f.Gun)!.Singleton = false;
        }
        world.SaveChanges();
        string items = JsonSerializer.Serialize(world.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id));
        string cooldowns = JsonSerializer.Serialize(world.Set<ModuleCooldown>().AsNoTracking().OrderBy(c => c.ModuleId));
        int destination =
            reason == "wrong rack" ? (int)InventoryFlag.MediumSlot0
            : reason == "outside rack" ? (int)InventoryFlag.HighSlot7
            : (int)InventoryFlag.HighSlot1;
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot.PilotId, [f.Gun], f.Pilot.ShipId, destination));
        Assert.Equal(items, JsonSerializer.Serialize(world.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id)));
        Assert.Equal(cooldowns, JsonSerializer.Serialize(world.Set<ModuleCooldown>().AsNoTracking().OrderBy(c => c.ModuleId)));
    }

    [Fact]
    public void ChangingAmmoReturnsOldRoundsAndUsesCatalogRangeAndCapacitorModifiers()
    {
        using Fixture f = new();
        f.Load(quantity: 10);
        f.Clock.Milliseconds = 5000;
        f.Load((int)HybridChargeType.IronSmall, 250);
        Assert.Equal(10, f.Total(222));
        Assert.Equal(10, Assert.Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.TypeId == 222).Quantity);
        var module = Assert.Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId), i => i.Id == f.Gun);
        var attributes = f.Combat.ModuleAttributes(f.Pilot.PilotId, module);
        Assert.Equal(11520, attributes[DogmaAttributeId.MaxRange], 8);
        Assert.Equal(1.169 * 0.95 * 0.7, attributes[DogmaAttributeId.CapacitorNeed], 8);
        f.Clock.Milliseconds = 10000;
        long reserve = Assert.Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.TypeId == 215).Id;
        Assert.Throws<InvalidDataException>(() =>
            f.Ammo.Load(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], [reserve], f.Pilot.StationId, f.Clock.Now)
        );
        Assert.Equal(250, f.Total(215));
    }

    [Fact]
    public async Task CompetingReloadsCannotDuplicateAmmunitionOrBypassTheReloadDeadline()
    {
        using Fixture f = new();
        long stack = f.Buy(222, 250);
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            f.Ammo.Load(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], [stack], f.Pilot.StationId, f.Clock.Now);
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
        Assert.Equal(250, f.Total(222));
        Assert.Equal(200, Assert.Single(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId)).Quantity);
    }

    [Fact]
    public async Task AShotAndAReloadCompeteForTheSamePaidDeadline()
    {
        using Fixture f = new();
        f.Load(quantity: 1);
        long reserve = f.Buy(222, 20);
        f.Inventory.Move(f.Pilot.PilotId, [reserve], f.Pilot.StationId, f.Pilot.ShipId, (int)InventoryFlag.Cargo, null);
        f.Clock.Milliseconds = 5000;
        f.Space();
        bool fired = false;
        var outcomes = await Task.WhenAll(
            Task.Run(() =>
            {
                try
                {
                    f.Fire();
                    fired = true;
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }),
            Task.Run(() =>
            {
                try
                {
                    f.Ammo.Load(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], [reserve], f.Pilot.ShipId, f.Clock.Now);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            })
        );
        Assert.Single(outcomes, success => success);
        Assert.Equal(fired ? 20 : 21, f.Total(222));
    }

    [Fact]
    public void VersionTwentyFourMigrationRetainsExistingCycleDeadlinesAndDoesNotGrantItems()
    {
        using Fixture f = new();
        int items;
        long deadline = f.Clock.Now + TimeSpan.TicksPerMinute;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new ModuleCooldown { ModuleId = f.Gun, ReadyAt = deadline });
            world.SaveChanges();
            items = world.Set<OwnedItem>().Count();
            world.Database.ExecuteSqlRaw(
                TestWorld.DropNpcNavigationColumns + "ALTER TABLE WorldWeaponCooldown DROP COLUMN Kind; PRAGMA user_version=24"
            );
        }
        _ = new SqliteWorld(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(27, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(ModuleCooldownKind.Cycle, saved.Set<ModuleCooldown>().Single().Kind);
        Assert.Equal(deadline, saved.Set<ModuleCooldown>().Single().ReadyAt);
        Assert.Equal(items, saved.Set<OwnedItem>().Count());
        Assert.Equal(900000, f.Market.Balance(f.Pilot.PilotId));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("remote")]
    [InlineData("wrong size")]
    [InlineData("duplicate")]
    [InlineData("skills")]
    public void InvalidLoadingRollsBackEveryStackAndTheMagazine(string reason)
    {
        using Fixture f = new();
        long stack = f.Buy(222, 10);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(stack)!;
            if (reason == "foreign")
            {
                item.OwnerId = 90000002;
            }
            if (reason == "remote")
            {
                item.LocationId = 60000001;
            }
            if (reason == "skills")
            {
                world.Set<PilotSkill>().Remove(world.Set<PilotSkill>().Find(f.Pilot.PilotId, 3301)!);
            }
            world.SaveChanges();
        }
        if (reason == "wrong size")
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=2 WHERE ItemTypeId=222 AND AttributeId=128");
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Ammo.Load(
                f.Pilot.PilotId,
                f.Pilot.ShipId,
                [f.Gun],
                reason == "duplicate" ? [stack, stack] : [stack],
                f.Pilot.StationId,
                f.Clock.Now
            )
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(10, saved.Set<OwnedItem>().Find(stack)!.Quantity);
        Assert.Null(saved.Set<ModuleCooldown>().Find(f.Gun));
        Assert.Empty(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
    }

    [Fact]
    public void CargoCapacityFailureRetainsLoadedRoundsAndUnfittingReturnsThemAtomically()
    {
        using Fixture f = new();
        f.Load(quantity: 200);
        f.Clock.Milliseconds = 5000;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = 2000000099999,
                    OwnerId = f.Pilot.PilotId,
                    TypeId = 34,
                    LocationId = f.Pilot.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = 12499,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<CargoCapacityException>(() =>
            f.Ammo.Unload(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], f.Pilot.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now)
        );
        Assert.Equal(200, Assert.Single(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId)).Quantity);
        Assert.Throws<CargoCapacityException>(() =>
            f.Inventory.Move(f.Pilot.PilotId, [f.Gun], f.Pilot.ShipId, f.Pilot.ShipId, (int)InventoryFlag.Cargo, null)
        );
        f.Inventory.Move(f.Pilot.PilotId, [f.Gun], f.Pilot.ShipId, f.Pilot.StationId, (int)InventoryFlag.Hangar, null);
        Assert.Empty(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Equal(200, f.Total(222));
        Assert.Equal(200, Assert.Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.TypeId == 222).Quantity);
    }

    [Fact]
    public void NativeLoadingPrimesTheTupleSublocationAndReloadStateForReconnect()
    {
        using Fixture f = new();
        long stack = f.Buy(222, 20);
        using var context = f.Context();
        AmmunitionHandler handler = new(f.Clock, f.Ammo, new(f.Clock, f.Fitting, f.Combat, f.Inventory));
        Assert.Null(
            handler.CallWire(
                context,
                "dogmaIM",
                "LoadAmmo",
                [f.Pilot.ShipId, new List<object?> { f.Gun }, new List<object?> { stack }, (long)f.Pilot.StationId]
            )
        );
        var prime = Assert.Single(context.Notifications.OfType<GodmaItemPrimed>()).Item;
        Assert.Equal(new DogmaItemKey.Charge(f.Pilot.ShipId, (int)InventoryFlag.HighSlot0, 222), prime.Key);
        Assert.Null(prime.Item);
        Assert.Equal(20d, prime.Attributes[DogmaAttributeId.Quantity]);
        Assert.Equal("OnGodmaPrimeItem", context.Notifications[0].Name);
        var liveQuantity = Assert.Single(Assert.IsType<List<object?>>(context.Notifications[1].Arguments[0]));
        var quantityChange = Assert.IsType<object?[]>(liveQuantity);
        Assert.Equal(prime.Key.ToBlue(), quantityChange[2]);
        Assert.Equal((int)DogmaAttributeId.Quantity, quantityChange[3]);
        Assert.Equal(20d, quantityChange[5]);
        Assert.Contains(context.Notifications, n => n.Name == "OnChargeBeingLoadedToModule" && Equals(n.Arguments[2], 5000d));
        using var scope = f.Db.Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryHandler>();
        var all = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(WireAssertions.Materialize(inventory.AllInfo(f.Pilot))).State
        );
        var character = Assert.IsType<object?[]>(all["charInfo"]);
        var brain = Assert.IsType<object?[]>(character[1]);
        Assert.Equal(3, Assert.IsType<List<object?>>(brain[2]).Count);
        var info = Assert.IsType<BlueDictionary>(all["shipInfo"]);
        Assert.Single(info.Entries, entry => entry.Key is object?[]);
        var state = Assert.IsType<object?[]>(all["shipState"]);
        var locations = Assert.IsType<Dictionary<long, object?>>(state[1]);
        var quantities = Assert.IsType<Dictionary<long, object?>>(locations[f.Pilot.ShipId]);
        var row = Assert.IsType<BlueConstructor>(quantities[(long)InventoryFlag.HighSlot0]);
        Assert.Equal(new object?[] { f.Pilot.ShipId, (int)InventoryFlag.HighSlot0, 222, 20 }, row.Arguments[1]);
        Assert.NotEmpty(BlueMarshal.Encode(all));
        f.Clock.Milliseconds = 5000;
        context.Notifications.Clear();
        Assert.Null(
            handler.CallWire(
                context,
                "dogmaIM",
                "UnloadAmmo",
                [f.Pilot.ShipId, f.Gun, new object?[] { f.Pilot.ShipId, f.Pilot.PilotId, (long)InventoryFlag.Cargo }, null]
            )
        );
        Assert.Contains(
            context.Notifications,
            n =>
                n.Name == "OnModuleAttributeChanges"
                && Assert.IsType<List<object?>>(n.Arguments[0]).Cast<object?[]>().Any(a => a[2] is object?[] && Equals(a[5], 0d))
        );
        Assert.Equal(20, f.Total(222));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HullLossIncludesLoadedRoundsInTheManifestAndAppliesTheirOwnDropRoll(bool survives)
    {
        using Fixture f = new();
        f.Load(quantity: 20);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemGroup VALUES (29,'Capsule',6);
                INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius,Capacity) VALUES (670,'Capsule',29,32000,2,0),
                    (26510,'Caldari Rookie ship Wreck',186,10000,14,27500);
                INSERT INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
                UPDATE SourceRecord SET Data='{{"volume":15000,"wreckTypeID":26510}}' WHERE Dataset='client/types' AND Key='601';
                UPDATE TypeAttribute SET Value=10000 WHERE ItemTypeId=30380 AND AttributeId=54;
                """
            );
        }
        f.Space();
        var pilot = f.Pilot;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = pilot.ShipId,
                    ShieldCharge = 0,
                    ArmorDamage = 125,
                    StructureDamage = 174,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.Add(new NpcAggression { NpcId = f.Npc.Id, TargetShipId = pilot.ShipId });
            world.SaveChanges();
        }
        INpcCombat combat = new NpcCombatStore(new(f.Db.Path), f.Db.Store, new FixedLoot(survives));
        Assert.NotNull(combat.Fire(pilot.PilotId, pilot.ShipId, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, 1)!.Capsule);
        using WorldDbContext saved = new(f.Db.Path);
        var loss = Assert.Single(saved.Set<ShipLoss>());
        var manifest = JsonSerializer.Deserialize<OwnedItem[]>(loss.ItemsJson)!;
        var loaded = Assert.Single(manifest, i => i.TypeId == 222);
        Assert.Equal(f.Gun, loaded.LocationId);
        Assert.Equal(20, loaded.Quantity);
        Assert.Empty(saved.Set<ModuleCooldown>());
        if (survives)
        {
            Assert.Equal(Assert.Single(saved.Set<WorldWreck>()).Id, saved.Set<OwnedItem>().Find(loaded.Id)!.LocationId);
        }
        else
        {
            Assert.Null(saved.Set<OwnedItem>().Find(loaded.Id));
        }
    }

    private sealed class FixedLoot(bool survives) : IShipLootRoll
    {
        public bool Survives() => survives;
    }
}
