using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class RemainingModuleHeatTests
{
    [Theory]
    [InlineData(450, 239, true)]
    [InlineData(452, 239, false)]
    [InlineData(484, 178, true)]
    [InlineData(485, 178, false)]
    [InlineData(561, 215, true)]
    [InlineData(563, 215, false)]
    public void TurretBanksPayNativeDamageOrRateAndWarmResidentRepeats(int type, int charge, bool resident)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWeaponTiers(f.CatalogPath, new HashSet<int> { type, charge });
        PassiveCatalogFixture.LoadProjectileTiers(f.CatalogPath, new HashSet<int> { type, charge });
        PassiveCatalogFixture.LoadEnergyTiers(f.CatalogPath, new HashSet<int> { type, charge });
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        NpcEncounterTests.Prepare(f.Db);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000 WHERE ItemTypeId=601 AND AttributeId IN (11,48,482)");
        catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000 WHERE ItemTypeId=30380 AND AttributeId=263");
        catalog.Database.ExecuteSqlRaw(
            "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144','{{\"securityStatus\":0}}')"
        );
        long first = f.Fit(type),
            second = f.Fit(type);
        long ship = f.ShipId;
        f.Db.Services.GetRequiredService<IPilotWeaponBanks>().Change(f.PilotId, ship, WeaponBankOperation.Link, first, second);
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammo.Load(f.PilotId, ship, [first], [f.Grant(charge, charge == 239 ? 2 : 50)], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 20000;
        var npc = MissileCombatTests.Space(f);
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var combat = f.Db.Services.GetRequiredService<PilotCombatStore>();
        var normal = combat.FireBank(f.PilotId, first, npc.Id, SpacePosition.Zero, f.Clock.Now, .5);
        f.Clock.Milliseconds += 30000;
        CombatVolley Fire() => combat.FireOverloadedBank(f.PilotId, second, npc.Id, SpacePosition.Zero, f.Clock.Now, .5);
        var hot = Fire();
        var binding = Assert.Single(OverloadCatalog.Definition(catalog, type)!.Bonuses);
        double factor = 1 + catalog.FittingAttributes(type)[binding.SourceAttribute] / 100;
        var before = normal.Weapons[0];
        Assert.All(
            hot.Weapons,
            w =>
            {
                Assert.Equal(
                    before.CycleMilliseconds * (binding.Attribute == DogmaAttributeId.WeaponCycleTime ? factor : 1),
                    w.CycleMilliseconds,
                    7
                );
                Assert.Equal(
                    before.Damage.Scale(binding.Attribute == DogmaAttributeId.DamageMultiplier ? factor : 1).Total,
                    w.Damage.Total,
                    7
                );
            }
        );
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        Assert.Equal(2, heat.Read(f.PilotId, ship, f.Clock.Now).Cycles.Count);
        Assert.Throws<InvalidDataException>(() => Fire());
        f.Clock.Milliseconds += 30000;
        using (var metrics = SessionPerformance.Begin("repeat turret overload", _ => { }, force: true)!)
        {
            Assert.Equal(2, Fire().Weapons.Count);
            if (resident)
            {
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
        }
        if (live is not null)
        {
            using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            snapshot.Project();
            using WorldDbContext captured = new(snapshot.Database);
            var cycles = ShipHeatPersistence.Decode(Assert.Single(captured.Set<SavedShipHeat>()).StateJson).Cycles;
            Assert.Equal(2, cycles.Count);
            Assert.All(cycles, c => Assert.Equal(c.CompletesAt, captured.Set<ModuleCooldown>().Find(c.ModuleId)!.ReadyAt));
        }
        f.Clock.Milliseconds += 30000;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var victim = world.Set<WorldNpc>().Find(npc.Id)!;
            victim.ShieldCharge = 0;
            victim.ArmorDamage = 15;
            victim.StructureDamage = 19;
            victim.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        var fatal = combat.FireOverloadedBank(f.PilotId, first, npc.Id, SpacePosition.Zero, f.Clock.Now, 0);
        Assert.True(fatal.Shot.Destroyed);
        Assert.Equal(2, heat.Read(f.PilotId, ship, f.Clock.Now).Cycles.Count);
        f.Clock.Milliseconds += 30000;
        var target = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(target.ShipId, npc.Position, npc.SystemId);
        var player = combat.FireOverloadedBank(
            f.PilotId,
            first,
            target.ShipId,
            SpacePosition.Zero,
            f.Clock.Now,
            0,
            playerTarget: new(target.ShipId, SpacePosition.Zero)
        );
        Assert.True(player.Shot.Applied > 0);
        Assert.Equal(2, heat.Read(f.PilotId, ship, f.Clock.Now).Cycles.Count);
    }

    [Fact]
    public void OverloadedCapacitorInjectionSpendsOneChargeAtTheShortenedDeadline()
    {
        using Fixture f = new();
        const int Booster = CapacitorBoosterDefinitions.SmallTechOne;
        const int Charge = CapacitorBoosterDefinitions.ChargeOneHundred;
        PassiveCatalogFixture.LoadActiveTiers(f.CatalogPath, new HashSet<int> { Booster, Charge });
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(Booster),
            ship = f.ShipId;
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammo.Load(f.PilotId, ship, [module], [f.Grant(Charge, 9)], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        var attrs = f.Fitting.ModuleAttributes(f.PilotId, f.Inventory.List(f.PilotId, ship).Single(i => i.Id == module));
        var loaded = Assert.Single(ammo.Read(f.PilotId, ship));
        var store = f.Db.Services.GetRequiredService<PilotCapacitorBoosterStore>();
        var paid = store.InjectOverloaded(f.PilotId, module, f.Clock.Now);
        Assert.Equal(loaded.Quantity - 1, paid.Magazine.Quantity);
        Assert.True(paid.Module.DurationMilliseconds < attrs[DogmaAttributeId.Duration]);
        Assert.Throws<InvalidDataException>(() => store.InjectOverloaded(f.PilotId, module, f.Clock.Now));
        var cycle = Assert.Single(f.Db.Services.GetRequiredService<IPilotShipHeat>().Read(f.PilotId, ship, f.Clock.Now).Cycles);
        Assert.Equal(f.Clock.Now + (long)Math.Ceiling(paid.Module.DurationMilliseconds * TimeSpan.TicksPerMillisecond), cycle.CompletesAt);
    }

    [Theory]
    [InlineData(578)]
    [InlineData(11301)]
    [InlineData(1952)]
    [InlineData(1977)]
    [InlineData(23533)]
    [InlineData(35788)]
    public void LocalActiveModulePublishesFundedBonusesUntilStop(int type)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadSupportTiers(f.CatalogPath, new HashSet<int> { type });
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(type),
            ship = f.ShipId;
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using CatalogDbContext catalog = new(f.CatalogPath);
        var effect = FittingCatalog.DefaultEffect(catalog, type)!.Value;
        var item = f.Inventory.List(f.PilotId, ship).Single(i => i.Id == module);
        var normal = f.Fitting.ModuleAttributes(f.PilotId, item);
        var hard = f.Db.Services.GetRequiredService<PilotHardenerStore>();
        var support = f.Db.Services.GetRequiredService<PilotSupportModuleStore>();
        bool hardening = HardenerDefinitions.Effect(item.GroupId) != null;
        if (hardening)
        {
            hard.ActivateOverloaded(f.PilotId, module, effect, f.Clock.Now);
        }
        else
        {
            support.ActivateOverloaded(f.PilotId, module, effect, f.Clock.Now);
        }
        var attrs = f.Fitting.ModuleAttributes(f.PilotId, item);
        foreach (var binding in OverloadCatalog.Definition(catalog, type)!.Bonuses)
        {
            double baseline = normal.GetValueOrDefault(
                binding.Attribute,
                catalog.Read(CatalogData.Client.Dogmaattributes, (long)binding.Attribute)?.DefaultValue ?? 0
            );
            Assert.Equal(baseline * (1 + normal[binding.SourceAttribute] / 100), attrs.GetValueOrDefault(binding.Attribute), 7);
        }
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        Assert.Equal(module, Assert.Single(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles).ModuleId);
        if (hardening)
        {
            hard.Stop(f.PilotId, ship, module);
        }
        else
        {
            support.Stop(f.PilotId, ship, module);
        }
        Assert.Empty(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles);
        f.Clock.Milliseconds += 60000;
        if (hardening)
        {
            hard.ActivateOverloaded(f.PilotId, module, effect, f.Clock.Now);
        }
        else
        {
            support.ActivateOverloaded(f.PilotId, module, effect, f.Clock.Now);
        }
        var paid = Assert.Single(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.CompletesAt - f.Clock.Now));
        Assert.Empty(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles);
        Assert.True(heat.Read(f.PilotId, ship, f.Clock.Now).Heat(paid.Rack) > 0);
        var expired = f.Fitting.ModuleAttributes(f.PilotId, item);
        foreach (var binding in OverloadCatalog.Definition(catalog, type)!.Bonuses)
        {
            Assert.Equal(normal.GetValueOrDefault(binding.Attribute), expired.GetValueOrDefault(binding.Attribute), 7);
        }
    }
}
