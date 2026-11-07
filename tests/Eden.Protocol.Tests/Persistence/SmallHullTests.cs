using System.Reflection;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SmallHullTests
{
    public static IEnumerable<object[]> Hulls()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.TechOneHulls.json")!;
        using var document = JsonDocument.Parse(stream);
        return document
            .RootElement.GetProperty("hulls")
            .EnumerateArray()
            .Where(h => TechOneHullRules.IsSmall((HullClass)h.GetProperty("groupId").GetInt32()))
            .Select(h => new object[] { h.GetProperty("id").GetInt32() })
            .ToArray();
    }

    internal static void TrainCatalog(Fixture f)
    {
        using CatalogDbContext catalog = new(f.CatalogPath);
        using WorldDbContext world = new(f.Db.Path);
        foreach (int skill in catalog.Set<ItemType>().Where(t => t.Group.CategoryId == (int)InventoryCategory.Skill).Select(t => t.Id))
        {
            double rank = catalog.Set<TypeAttribute>().Find(skill, (int)DogmaAttributeId.SkillTimeConstant)?.Value ?? 1;
            var saved =
                world.Set<PilotSkill>().Find(f.PilotId, skill)
                ?? world.Set<PilotSkill>().Add(new() { PilotId = f.PilotId, TypeId = skill }).Entity;
            saved.Points = TrainedSkill.PointsForLevel(rank, 5);
        }
        world.SaveChanges();
    }

    internal static long Board(Fixture f, int type)
    {
        TrainCatalog(f);
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long ship = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(type)], "Hull acceptance").ShipIds);
        fleet.Board(f.PilotId, ship, f.ShipId);
        return ship;
    }

    [Fact]
    public void CohortIncludesEverySmallTechOneEntryAndEveryEffectHasAnExplicitOwner()
    {
        Assert.Equal(99, Hulls().Count());
        using Fixture f = new();
        PassiveCatalogFixture.LoadSmallHulls(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        foreach (int type in Hulls().Select(h => (int)h[0]))
        {
            Assert.True(HullCatalog.UsesCatalogBonuses(catalog, type));
            Assert.DoesNotContain(HullCatalog.Capabilities(catalog, type), e => e.Capability == HullCapability.Unclassified);
            Assert.All(HullCatalog.Modifiers(catalog, type), m => Assert.Equal(PassiveEffectPhase.Fitted, m.Phase));
        }
        var report = HullSupportAudit.Read(catalog);
        Assert.Equal(Hulls().Select(h => (int)h[0]).Order(), report.Hulls.Select(h => h.TypeId));
        Assert.All(report.Hulls, hull => Assert.DoesNotContain(HullCapability.Unclassified, hull.Deferred));
        Assert.DoesNotContain(HullCapability.CombatDrones, Assert.Single(report.Hulls, hull => hull.TypeId == 594).Deferred);
        Assert.Contains(report.Hulls, hull => hull.TypeId == 2078 && hull.Deferred.Contains(HullCapability.Probes));
        Assert.Contains(report.Hulls, hull => hull.TypeId == 3532 && hull.Deferred.Contains(HullCapability.Hacking));
        Assert.Contains(
            report.Hulls,
            hull => hull.TypeId == (int)TechOneHullType.Tholos && hull.Deferred.Contains(HullCapability.BreacherPods)
        );
    }

    [Theory]
    [MemberData(nameof(Hulls))]
    public void PurchasedHullRetainsItsIdentityFittingCargoHealthAndFlightAfterRestart(int type)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadSmallHull(f.CatalogPath, type);
        VerifyLifecycle(f, type);
    }

    internal static void VerifyLifecycle(Fixture f, int type, bool developerFixture = false)
    {
        using (CatalogDbContext setup = new(f.CatalogPath, writable: true))
        {
            setup.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[7,17]}}')"
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new PilotWallet { PilotId = f.PilotId, Cents = 10000 });
            world.SaveChanges();
        }
        long original = f.ShipId;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        long stack;
        if (developerFixture)
        {
            Assert.DoesNotContain(market.Orders(f.PilotId, type), order => order.TypeId == type);
            stack = f.Grant(type, 2);
        }
        else
        {
            var bought = market.Buy(f.PilotId, checked((int)f.StationId), [new(type, 100, 2)]);
            Assert.Equal(9800, bought.BalanceCents);
            stack = Assert.Single(bought.Changes).Item.Id;
        }
        Assert.Throws<InvalidDataException>(() => fleet.Assemble(f.PilotId, [stack, stack], null));
        Assert.Throws<InvalidDataException>(() => fleet.Assemble(f.PilotId, [stack, long.MaxValue], null));
        Assert.Throws<InvalidDataException>(() => fleet.Assemble(90000002, [stack], null));
        long ship = Assert.Single(fleet.Assemble(f.PilotId, [stack], "Hull acceptance").ShipIds);
        Assert.NotEqual(stack, ship);
        Assert.Empty(fleet.Assemble(f.PilotId, [ship], "Repeated request").Changes);
        Assert.Throws<InvalidDataException>(() => fleet.Board(90000002, ship, f.Db.Store.FindPilot(1000002)!.ShipId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.StationId++;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => fleet.Board(f.PilotId, ship, original));
        Assert.Throws<InvalidDataException>(() => fleet.Assemble(f.PilotId, [ship], null));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.StationId--;
            world.SaveChanges();
        }
        Assert.Equal(1, Assert.Single(f.Inventory.List(f.PilotId, f.StationId), i => i.Id == stack).Quantity);
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var requirements = HullCatalog.Skills(catalog, type);
            if (requirements.Count > 0)
            {
                foreach (int skill in requirements)
                {
                    f.Train(skill, 0);
                }
                Assert.Throws<InvalidDataException>(() => fleet.Board(f.PilotId, ship, original));
            }
        }
        TrainCatalog(f);
        fleet.Board(f.PilotId, ship, original);
        var attributes = f.Fitting.ShipAttributes(f.PilotId);
        var raw = f.Fitting.Attributes(type);
        foreach (
            var limit in new[]
            {
                DogmaAttributeId.HighSlots,
                DogmaAttributeId.MediumSlots,
                DogmaAttributeId.LowSlots,
                DogmaAttributeId.TurretSlotsLeft,
                DogmaAttributeId.LauncherSlotsLeft,
            }
        )
        {
            Assert.Equal(raw.GetValueOrDefault(limit), attributes.GetValueOrDefault(limit));
        }
        int weaponType =
            raw.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft) > 0 ? 561
            : raw.GetValueOrDefault(DogmaAttributeId.LauncherSlotsLeft) > 0 ? 499
            : 0;
        if (weaponType > 0)
        {
            long weapon = f.Fit(weaponType);
            int chargeType = weaponType == 561 ? 215 : 210;
            long charge = f.Grant(chargeType, 250, cargo: true);
            f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, ship, [weapon], [charge], ship, f.Clock.Now);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => f.Fit(561));
        }
        long cargo = f.Grant(215, cargo: true);
        var inventory = f.Inventory.List(f.PilotId, ship).ToArray();
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        var magazines = ammo.Read(f.PilotId, ship).ToArray();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = ship,
                    ArmorDamage = 10,
                    StructureDamage = 5,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        fleet.Board(f.PilotId, original, ship);
        Assert.Contains(f.Inventory.List(f.PilotId, ship), item => item.Id == cargo);
        fleet.Board(f.PilotId, ship, original);
        Assert.Equal(inventory, f.Inventory.List(f.PilotId, ship));
        Assert.Equal(magazines, ammo.Read(f.PilotId, ship));
        var restarted = new SqliteWorld(new(f.Db.Path)).FindPilot(1000001)!;
        Assert.Equal((ship, type, "Hull acceptance"), (restarted.ShipId, restarted.ShipTypeId, restarted.ShipName));
        Assert.True(restarted.Flight.Mass > 0 && restarted.Flight.MaxSpeed > 0 && restarted.Flight.Agility > 0);
        Assert.True(restarted.Flight.ShipRadius > 0 && restarted.Flight.WarpSpeed > 0);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(10, saved.Set<ShipHealthState>().Find(ship)!.ArmorDamage);
            Assert.Equal(5, saved.Set<ShipHealthState>().Find(ship)!.StructureDamage);
        }
        using FlightSession session = new(f.Db.Store, restarted, f.Clock, new(), f.Db.Objects);
        Assert.IsType<PilotCallResult.Success>(session.Execute(new FlightCommand.Undock(ship)));
        double separation = session.Pilot.Position!.DistanceTo(restarted.Home.Position);
        Assert.True(separation - restarted.Flight.StationRadius - restarted.Flight.ShipRadius >= 499);
        session.Snapshot();
        Assert.True(session.InBallpark);
        Assert.Contains(session.Updates, update => update.Command is DestinyCommand.SetState);
        // Collision clearance can launch a hull beyond the station's docking range.
        Assert.IsType<PilotCallResult.Success>(session.Execute(new FlightCommand.Follow(f.StationId, 0, false)));
        f.Clock.Seconds += checked((long)Math.Ceiling(restarted.Flight.InertiaTime * 6 + 30));
        session.Checkpoint();
        Assert.IsType<PilotCallResult.Success>(session.Execute(new FlightCommand.Dock(f.StationId, ship)));
    }
}
