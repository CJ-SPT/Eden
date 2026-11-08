using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Information;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests
{
    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadInformation(f.CatalogPath);
        return f;
    }

    private static IPilotInformation Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotInformation>();

    private static long Target(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static void Space(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Target(f), origin + new SpacePosition(1000, 0, 0));
    }

    private static long Wreck(Fixture f, double distance = 1000, bool contents = false)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using WorldDbContext world = new(f.Db.Path);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var wreck = new WorldWreck
        {
            Id = WreckDefinitions.FirstWreckId + 17,
            NpcId = 17,
            OwnerId = f.PilotId,
            CorporationId = pilot.CorpId,
            TypeId = 26573,
            Name = "Guristas Small Wreck",
            SystemId = pilot.SystemId,
            X = pilot.Position!.X + distance,
            Y = pilot.Position.Y,
            Z = pilot.Position.Z,
            CreatedAt = f.Clock.Now,
            ExpiresAt = f.Clock.Now + TimeSpan.TicksPerHour,
        };
        SalvagePersistence.Seed(catalog, wreck);
        world.Add(
            new WorldNpc
            {
                Id = 17,
                TypeId = 601,
                SystemId = pilot.SystemId,
                DestroyedAt = f.Clock.Now,
            }
        );
        world.Add(wreck);
        world.SaveChanges();
        if (contents)
        {
            long item = f.Grant(34, 17);
            var stack = world.Set<OwnedItem>().Find(item)!;
            stack.LocationId = wreck.Id;
            stack.FlagId = (int)InventoryFlag.Cargo;
            world.SaveChanges();
        }
        return wreck.Id;
    }

    [Fact]
    public void EveryPublishedInformationProfileHasAnExplicitFittingValidator()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var groups = Enum.GetValues<InformationGroup>().Select(g => (int)g).ToArray();
        var types = catalog.Set<ItemType>().Where(t => groups.Contains(t.GroupId)).OrderBy(t => t.Id).ToArray();
        Assert.Equal(32, types.Length);
        foreach (var type in types)
        {
            Assert.True(InformationCatalog.Supported(catalog, type.Id, type.GroupId), type.Name);
            Assert.NotNull(FittingCatalog.Definition(catalog, type.Id));
        }
    }

    [Theory]
    [InlineData(442, DogmaEffect.CargoScan)]
    [InlineData(2038, DogmaEffect.CargoScan)]
    [InlineData(443, DogmaEffect.ShipScan)]
    [InlineData(1855, DogmaEffect.ShipScan)]
    public void ScanPaymentAndDeadlineAreAtomicAndDuplicateCompletionCannotDiscloseAgain(int type, DogmaEffect effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        long target = Target(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = 7000000000001,
                    TypeId = 34,
                    OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId,
                    LocationId = target,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = 47,
                }
            );
            world.SaveChanges();
        }
        var before = f.Fitting.ShipAttributes(f.PilotId);
        var activation = Store(f).Begin(f.PilotId, module, effect, target, new HashSet<long> { target }, f.Clock.Now);
        Assert.Equal(before[DogmaAttributeId.CapacitorCapacity] - activation.Cycle.Module.CapacitorNeed, activation.Capacitor, 8);
        Assert.Throws<InvalidDataException>(() => Store(f).Complete(f.PilotId, module, activation.Cycle.ReadyAt, f.Clock.Now));
        f.Clock.Milliseconds += (long)activation.Cycle.Module.DurationMilliseconds;
        var result = Store(f).Complete(f.PilotId, module, activation.Cycle.ReadyAt, f.Clock.Now);
        if (effect == DogmaEffect.CargoScan)
        {
            Assert.Contains(result.Cargo!.Cargo, i => i.TypeId == 34 && i.Quantity == 47);
        }
        else
        {
            Assert.Equal(target, result.Ship!.ShipId);
            Assert.True(result.Ship.CapacitorCapacity > 0);
            Assert.DoesNotContain(result.Ship.Hardware, i => i.TypeId == 34);
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Complete(f.PilotId, module, activation.Cycle.ReadyAt, f.Clock.Now));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(activation.Cycle.ReadyAt, verify.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Empty(verify.Set<SavedInformationCycle>());
        Assert.Equal(47, verify.Set<OwnedItem>().Find(7000000000001L)!.Quantity);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("owner")]
    [InlineData("range")]
    [InlineData("offline")]
    public void RejectedScanLeavesCapacitorCooldownAndCargoUnchanged(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(442);
        Space(f);
        long target = Target(f);
        if (reason == "range")
        {
            var p = f.Db.Store.FindPilot(1000001)!.Position!;
            f.Db.Store.SetPosition(target, p + new SpacePosition(100000, 0, 0));
        }
        if (reason == "offline")
        {
            using WorldDbContext offline = new(f.Db.Path);
            offline.Set<OwnedItem>().Find(module)!.Online = false;
            offline.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() =>
            Store(f)
                .Begin(
                    reason == "owner" ? f.Db.Store.FindPilot(1000002)!.PilotId : f.PilotId,
                    module,
                    DogmaEffect.CargoScan,
                    target,
                    reason == "lock" ? [] : new HashSet<long> { target },
                    f.Clock.Now
                )
        );
        using WorldDbContext world = new(f.Db.Path);
        Assert.Null(world.Set<ModuleCooldown>().Find(module));
        Assert.Null(world.Set<ShipEnergy>().Find(f.ShipId));
        Assert.Empty(world.Set<SavedInformationCycle>());
    }

    [Fact]
    public void StopAndReopenRetainPaymentAndCooldownWithoutOfflineResults()
    {
        using Fixture f = Prepare();
        long module = f.Fit(442);
        Space(f);
        var cycle = Store(f).Begin(f.PilotId, module, DogmaEffect.CargoScan, Target(f), new HashSet<long> { Target(f) }, f.Clock.Now);
        WorldInitialization.Initialize(new(f.Db.Path));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedInformationCycle>());
        Assert.Equal(cycle.Cycle.ReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(cycle.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Throws<InvalidDataException>(() => Store(f).Complete(f.PilotId, module, cycle.Cycle.ReadyAt, cycle.Cycle.ReadyAt));
    }

    [Fact]
    public void SurveyRequiresOnlineChipsetAndReturnsCurrentNearbyQuantities()
    {
        using Fixture f = Prepare();
        long module = f.Fit(444);
        Space(f);
        var p = f.Db.Store.FindPilot(1000001)!.Position!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new WorldAsteroid
                {
                    Id = 99001,
                    BeltId = 40009077,
                    SystemId = 30000142,
                    TypeId = 1230,
                    Quantity = 12345,
                    X = p.X + 1000,
                    Y = p.Y,
                    Z = p.Z,
                    Radius = 10,
                }
            );
            world.SaveChanges();
        }
        Assert.Contains(Store(f).Survey(f.PilotId, f.Clock.Now), r => r.AsteroidId == 99001 && r.Quantity == 12345);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Online = false;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Survey(f.PilotId, f.Clock.Now));
    }

    [Fact]
    public void SuccessfulSalvagePaysOnceAndPreservesUnlootedContentsAsAContainer()
    {
        using Fixture f = Prepare();
        long module = f.Fit(25861);
        Space(f);
        long wreck = Wreck(f, contents: true);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value={1000d} WHERE ItemTypeId={25861} AND AttributeId={902}");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldWreck>().Find(wreck)!.SalvageJson = JsonSerializer.Serialize(new[] { new ScanItem(25599, 2) });
            world.SaveChanges();
        }
        var activation = Store(f).Begin(f.PilotId, module, DogmaEffect.Salvaging, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        f.Clock.Milliseconds += (long)activation.Cycle.Module.DurationMilliseconds;
        var result = Store(f).Complete(f.PilotId, module, activation.Cycle.ReadyAt, f.Clock.Now);
        Assert.True(result.Salvage!.Success);
        Assert.Contains(result.Salvage.Cargo, c => c.Item.TypeId == 25599 && c.Item.Quantity == 2);
        var contact = Assert.Single(f.Db.Services.GetRequiredService<IWrecks>().List(30000142, f.Clock.Now));
        Assert.Equal(23, contact.TypeId);
        Assert.Equal(12, contact.GroupId);
        var contents = f.Db.Services.GetRequiredService<IWrecks>().Read(f.PilotId, wreck, f.Clock.Now);
        Assert.Contains(contents.Items, i => i.TypeId == 34 && i.Quantity == 17);
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Begin(f.PilotId, module, DogmaEffect.Salvaging, wreck, new HashSet<long> { wreck }, f.Clock.Now)
        );
        WorldInitialization.Initialize(new(f.Db.Path));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.NotNull(verify.Set<WorldWreck>().Find(wreck)!.SalvagedAt);
        Assert.Equal(2, verify.Set<OwnedItem>().Single(i => i.TypeId == 25599 && i.LocationId == f.ShipId).Quantity);
    }

    [Fact]
    public void TractorRejectsOthersWrecksAndMovesTheOwnedWreckWithoutOvershooting()
    {
        using Fixture f = Prepare();
        long module = f.Fit(24348);
        Space(f);
        long wreck = Wreck(f, 10000);
        var activation = Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        Store(f).AdvanceTractor(activation.Cycle, f.Clock.Now + TimeSpan.TicksPerSecond * 2);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(f.Db.Store.FindPilot(1000001)!.Position!.X + 9000, world.Set<WorldWreck>().Find(wreck)!.X, 5);
        }
        Store(f).Stop(f.PilotId, module, activation.Cycle.ReadyAt);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldWreck>().Find(wreck)!.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
            world.SaveChanges();
        }
        f.Clock.Seconds += 10;
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now)
        );
    }
}
