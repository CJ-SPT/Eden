using System.Reflection;
using System.Text.Json;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Repair;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class TechOneHullTests
{
    [Theory]
    [InlineData(603, 3330, 450, DogmaAttributeId.DamageMultiplier)]
    [InlineData(603, 3330, 570, DogmaAttributeId.DamageMultiplier)]
    [InlineData(597, 3331, 561, DogmaAttributeId.CapacitorNeed)]
    [InlineData(597, 3331, 456, DogmaAttributeId.CapacitorNeed)]
    [InlineData(594, 3328, 484, DogmaAttributeId.DamageMultiplier)]
    [InlineData(594, 3328, 570, DogmaAttributeId.DamageMultiplier)]
    [InlineData(587, 3329, 561, DogmaAttributeId.WeaponCycleTime)]
    [InlineData(587, 3329, 490, DogmaAttributeId.WeaponCycleTime)]
    public void WeaponBonusesExcludeOtherFamiliesAndSizes(int type, int skill, int moduleType, DogmaAttributeId attribute)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadFrigateHulls(f.CatalogPath);
        f.Train(skill, 3);
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long ship = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(type)], null).ShipIds);
        fleet.Board(f.PilotId, ship, f.ShipId);
        // Offline medium guns can occupy a hardpoint without exceeding online resources.
        long fitted = f.Fit(moduleType, online: false);
        var module = Assert.Single(f.Inventory.List(f.PilotId, ship), item => item.Id == fitted);
        var trained = f.Fitting.ModuleAttributes(f.PilotId, module);
        f.Train(skill, 0);
        var untrained = f.Fitting.ModuleAttributes(f.PilotId, module);
        Assert.Equal(untrained[attribute], trained[attribute], 8);
        if (type == (int)TechOneHullType.Rifter)
        {
            Assert.Equal(untrained[DogmaAttributeId.AccuracyFalloff], trained[DogmaAttributeId.AccuracyFalloff], 8);
        }
    }

    [Fact]
    public void IncomingInventoryBoundBeforeBoardingStillAnswersTurretAndListCalls()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadFrigateHulls(f.CatalogPath);
        f.Train(3330, 3);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long ship = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant((int)TechOneHullType.Merlin)], null).ShipIds);
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new FlightSession(f.Db.Store, selected, f.Clock, new(), f.Db.Objects),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var incoming = context.Reference($"@ship:{ship}", PilotBindingLifetime.Location);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "ship", "Board", new object?[] { ship, pilot.ShipId }, _ => { }));
        string service = context.ResolveReference(incoming.Id)!;
        Assert.Equal($"@ship:{ship}", service);
        Assert.IsType<PilotCallResult.Success>(router.Call(context, service, "GetAvailableTurretSlots", Array.Empty<object?>(), _ => { }));
        Assert.IsType<PilotCallResult.Success>(router.Call(context, service, "List", Array.Empty<object?>(), _ => { }));
        context.ReleaseLocationReferences();
        Assert.Null(context.ResolveReference(incoming.Id));
    }

    [Fact]
    public void IncursusPaysAndCompletesItsBonusedRepairOnce()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadFrigateHulls(f.CatalogPath);
        f.Train(3328, 3);
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long ship = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant((int)TechOneHullType.Incursus)], null).ShipIds);
        fleet.Board(f.PilotId, ship, f.ShipId);
        long module = f.Fit(1183);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = ship,
                    ArmorDamage = 200,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        var repairs = f.Db.Services.GetRequiredService<IPilotLocalRepairing>();
        var activation = repairs.Start(f.PilotId, module, f.Clock.Now);
        double amount = f.Fitting.Attributes(1183)[DogmaAttributeId.ArmorRepairAmount] * 1.225;
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(amount, world.Set<PendingLocalRepair>().Find(module)!.Amount, 8);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(activation.Schedule.CompletesAt - f.Clock.Now));
        var completed = repairs.Complete(f.PilotId, ship, f.Clock.Now);
        Assert.Equal(200 - amount, completed.Defense.Armor - completed.Remaining.Armor, 8);
        Assert.Equal(completed, repairs.Complete(f.PilotId, ship, f.Clock.Now));
    }

    [Fact]
    public void PinnedInventoryContainsAllPublishedTechOneHullIdsIncludingExceptions()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.TechOneHulls.json")!;
        using var document = JsonDocument.Parse(stream);
        var hulls = document.RootElement.GetProperty("hulls").EnumerateArray().ToArray();
        Assert.Equal(3662168, document.RootElement.GetProperty("build").GetInt32());
        Assert.Equal(275, hulls.Length);
        Assert.Equal(275, hulls.Select(h => h.GetProperty("id").GetInt32()).Distinct().Count());
        Assert.Equal(272, hulls.Count(h => h.GetProperty("acquisition").GetString() == "market"));
        Assert.Contains(hulls, h => h.GetProperty("id").GetInt32() == FleetDefinitions.CapsuleType);
    }

    [Theory]
    [InlineData(603, 3330, 561, 1.15)]
    [InlineData(597, 3331, 450, 0.7)]
    [InlineData(594, 3328, 1183, 1.225)]
    [InlineData(587, 3329, 484, 0.775)]
    public void AssemblyBoardingAndProductionBonusesUseEarnedRacialSkill(int type, int skill, int moduleType, double factor)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadFrigateHulls(f.CatalogPath);
        var original = f.Db.Store.FindPilot(1000001)!;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long stack = f.Grant(type, 2);
        long first = Assert.Single(fleet.Assemble(f.PilotId, [stack], "Racial frigate").ShipIds);
        Assert.NotEqual(stack, first);
        Assert.Empty(fleet.Assemble(f.PilotId, [first], null).Changes);
        Assert.Single(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == stack && item.Quantity == 1);
        f.Train(skill, 3);
        fleet.Board(f.PilotId, first, original.ShipId);
        Assert.Equal(type, f.Db.Store.FindPilot(1000001)!.ShipTypeId);
        Assert.Equal("Racial frigate", fleet.Name(f.PilotId, first));
        long fitted = f.Fit(moduleType);
        var module = Assert.Single(f.Inventory.List(f.PilotId, first), item => item.Id == fitted);
        DogmaAttributeId attribute = (TechOneHullType)type switch
        {
            TechOneHullType.Merlin => DogmaAttributeId.DamageMultiplier,
            TechOneHullType.Punisher => DogmaAttributeId.CapacitorNeed,
            TechOneHullType.Incursus => DogmaAttributeId.ArmorRepairAmount,
            _ => DogmaAttributeId.WeaponCycleTime,
        };
        var enhanced = f.Fitting.ModuleAttributes(f.PilotId, module);
        f.Train(skill, 0);
        var untrained = f.Fitting.ModuleAttributes(f.PilotId, module);
        Assert.Equal(untrained[attribute] * factor, enhanced[attribute], 8);
        if (type == (int)TechOneHullType.Rifter)
        {
            Assert.Equal(untrained[DogmaAttributeId.AccuracyFalloff] * 1.3, enhanced[DogmaAttributeId.AccuracyFalloff], 8);
        }
        f.Train(skill, 3);
        if (type != (int)TechOneHullType.Incursus)
        {
            var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
            double actual = combat.ModuleAttributes(f.PilotId, module)[attribute];
            f.Train(skill, 0);
            double baseline = combat.ModuleAttributes(f.PilotId, module)[attribute];
            Assert.Equal(baseline * factor, actual, 8);
            f.Train(skill, 3);
        }
        if (type is (int)TechOneHullType.Merlin or (int)TechOneHullType.Punisher)
        {
            var resonance =
                type == (int)TechOneHullType.Merlin ? DogmaAttributeId.ShieldKineticResonance : DogmaAttributeId.ArmorKineticResonance;
            double raw = f.Fitting.Attributes(type)[resonance];
            Assert.Equal(raw * 0.88, f.Fitting.ShipAttributes(f.PilotId)[resonance], 8);
        }
        long weapon = fitted;
        if (type == (int)TechOneHullType.Incursus)
        {
            weapon = f.Fit(561);
            var hybrid = Assert.Single(f.Inventory.List(f.PilotId, first), item => item.Id == weapon);
            double damage = f.Fitting.ModuleAttributes(f.PilotId, hybrid)[DogmaAttributeId.DamageMultiplier];
            f.Train(skill, 0);
            double baseline = f.Fitting.ModuleAttributes(f.PilotId, hybrid)[DogmaAttributeId.DamageMultiplier];
            Assert.Equal(baseline * 1.15, damage, 8);
            f.Train(skill, 3);
        }
        int chargeType = (TechOneHullType)type switch
        {
            TechOneHullType.Punisher => 246,
            TechOneHullType.Rifter => 179,
            _ => 215,
        };
        int chargeQuantity = type == (int)TechOneHullType.Punisher ? 4 : 500;
        long cargo = f.Grant(chargeType, chargeQuantity, cargo: true);
        var ammunition = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammunition.Load(f.PilotId, first, [weapon], [cargo], first, f.Clock.Now);
        var magazines = ammunition.Read(f.PilotId, first).ToArray();
        var inventory = f.Inventory.List(f.PilotId, first).ToArray();
        var flight = f.Db.Store.FindPilot(1000001)!.Flight;
        Assert.True(flight.Mass > 0 && flight.Agility > 0 && flight.ShipRadius > 0 && flight.MaxSpeed > 0 && flight.WarpSpeed > 0);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = first,
                    ArmorDamage = 10,
                    StructureDamage = 5,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        fleet.Board(f.PilotId, original.ShipId, first);
        Assert.Single(f.Inventory.List(f.PilotId, first), item => item.Id == fitted);
        fleet.Board(f.PilotId, first, original.ShipId);
        Assert.Equal(inventory, f.Inventory.List(f.PilotId, first));
        Assert.Equal(magazines, ammunition.Read(f.PilotId, first));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(first, saved.Pilots.Single(p => p.Id == f.PilotId).ActiveShipId);
        Assert.Equal(10, saved.Set<ShipHealthState>().Find(first)!.ArmorDamage);
        Assert.Equal(type, new SqliteWorld(new(f.Db.Path)).FindPilot(1000001)!.ShipTypeId);
        using FlightSession session = new(f.Db.Store, f.Db.Store.FindPilot(1000001)!, f.Clock, new(), f.Db.Objects);
        Assert.IsType<PilotCallResult.Success>(session.Execute(new FlightCommand.Undock(first)));
        session.Snapshot();
        Assert.True(session.InBallpark);
        Assert.Contains(session.Updates, update => update.Command is DestinyCommand.SetState);
        Assert.IsType<PilotCallResult.Success>(session.Execute(new FlightCommand.Dock(f.StationId, first)));
        Assert.Null(f.Db.Store.FindPilot(1000001)!.Position);
    }

    [Theory]
    [InlineData(false, 1, InventoryCategory.Ship)]
    [InlineData(true, 2, InventoryCategory.Ship)]
    [InlineData(true, 1, InventoryCategory.Module)]
    public void HullRuleRejectsUnpublishedHigherTechAndNonShips(bool published, int tech, InventoryCategory category) =>
        Assert.False(TechOneHullRules.CanAssemble(new((int)TechOneHullType.Merlin, category, published, tech)));

    [Fact]
    public void AssemblyRollsBackMixedBatchAndBoardingChecksSkills()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadFrigateHulls(f.CatalogPath);
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long original = f.ShipId;
        long valid = f.Grant((int)TechOneHullType.Merlin);
        long invalid = f.Grant(561);
        Assert.Throws<InvalidDataException>(() => fleet.Assemble(f.PilotId, [valid, invalid], null));
        Assert.Contains(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == valid && !item.Singleton);
        long ship = Assert.Single(fleet.Assemble(f.PilotId, [valid], null).ShipIds);
        Assert.Equal(valid, ship);
        f.Train(3330, 0);
        Assert.Throws<InvalidDataException>(() => fleet.Board(f.PilotId, ship, original));
        Assert.Equal(original, f.ShipId);
    }
}
