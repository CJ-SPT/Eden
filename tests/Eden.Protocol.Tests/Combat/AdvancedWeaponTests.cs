using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class AdvancedWeaponTests
{
    private const int Disintegrator = 47272;
    private const int ExoticPlasma = 47885;
    private const int Projector = 54739;
    private const int Condenser = 54769;
    private const int PodLauncher = 85084;
    private const int SmallPod = 85088;
    private const int MediumPod = 85089;

    [Fact]
    public void PublishedCohortsValidateWithTheirNativeChargeAndFittingRestrictions()
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadAdvancedWeapons(fixture.CatalogPath);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        foreach (
            var (group, count) in new (int Group, int Count)[]
            {
                (CombatDefinitions.DisintegratorGroup, 21),
                (CombatDefinitions.VortonGroup, 15),
                (1988, 5),
                (4067, 5),
                ((int)MissileLauncherGroup.RapidTorpedo, 5),
                ((int)MissileLauncherGroup.BreacherPod, 2),
            }
        )
        {
            var types = catalog
                .Set<ItemType>()
                .Where(t => t.GroupId == group)
                .ToArray()
                .Where(t => catalog.Require(CatalogData.Client.Types, (long)t.Id).MetaGroupID != 15)
                .ToArray();
            Assert.Equal(count, types.Length);
            Assert.All(types, type => Assert.True(FittingCatalog.Definition(catalog, type.Id) is not null, type.Name));
        }
        foreach (int group in new[] { 1987, 1989, 4061, 4062, (int)MissileChargeGroup.BreacherPod })
        {
            var charges = catalog.Set<ItemType>().Where(t => t.GroupId == group).ToArray();
            Assert.All(charges, type => Assert.True(AmmunitionCatalog.Supported(catalog, type.Id, group), type.Name));
        }
        Assert.NotNull(FittingCatalog.Definition(catalog, BreacherRules.CounterType));
        var launcher = catalog.FittingAttributes(PodLauncher);
        Assert.True(
            MissileDefinitions.CompatibleCharge(launcher, (int)MissileChargeGroup.BreacherPod, catalog.FittingAttributes(SmallPod))
        );
        Assert.False(
            MissileDefinitions.CompatibleCharge(launcher, (int)MissileChargeGroup.BreacherPod, catalog.FittingAttributes(MediumPod))
        );
    }

    [Fact]
    public void DisintegratorSpoolsOnlyOnPaidContinuousCyclesAndStopsBeyondOptimal()
    {
        using var fixture = Prepare(47269);
        long module = MissileCombatTests.Load(fixture, Disintegrator, ExoticPlasma, 10);
        var npc = MissileCombatTests.Space(fixture);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotCombat>();
        CombatShot Fire(AdvancedWeaponCycle cycle) =>
            store.FireAdvanced(fixture.PilotId, module, npc.Id, null, SpacePosition.Zero, fixture.Clock.Now, .5, cycle).Shot;
        var first = Fire(new(WeaponActivation.Disintegrator));
        Assert.Equal(0, first.SpoolCycles);
        Assert.Throws<InvalidDataException>(() => Fire(new(WeaponActivation.Disintegrator, 1, ExoticPlasma)));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(Math.Ceiling(first.Weapon.CycleMilliseconds)));
        var next = Fire(new(WeaponActivation.Disintegrator, 1, ExoticPlasma));
        Assert.Equal(first.Weapon.Damage.Total * 1.07, next.Weapon.Damage.Total, 8);
        Assert.Equal(8, next.Magazine!.Quantity);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(Math.Ceiling(next.Weapon.CycleMilliseconds)));
        var restarted = Fire(new(WeaponActivation.Disintegrator));
        Assert.Equal(first.Weapon.Damage, restarted.Weapon.Damage);
    }

    [Fact]
    public void VortonHitsOnlySecondaryTargetsNearPrimaryAndConsumesOneCondenser()
    {
        using var fixture = Prepare(54731);
        long module = MissileCombatTests.Load(fixture, Projector, Condenser, 5);
        var npc = MissileCombatTests.Space(fixture);
        long near = npc.Id + 1000;
        long far = npc.Id + 1001;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            foreach (var (id, distance) in new[] { (near, 9000), (far, 18000) })
            {
                world.Add(
                    new WorldNpc
                    {
                        Id = id,
                        TypeId = npc.TypeId,
                        SystemId = npc.SystemId,
                        X = npc.Position.X + distance,
                        Y = npc.Position.Y,
                        Z = npc.Position.Z,
                        ShieldCharge = 1000000,
                        UpdatedAt = fixture.Clock.Now,
                    }
                );
            }
            world.SaveChanges();
        }
        var store = fixture.Db.Services.GetRequiredService<IPilotCombat>();
        var volley = store.FireAdvanced(
            fixture.PilotId,
            module,
            npc.Id,
            null,
            SpacePosition.Zero,
            fixture.Clock.Now,
            .99,
            new(WeaponActivation.Vorton, Npcs: [far, near, npc.Id, near])
        );
        Assert.True(volley.Shot.Applied > 0);
        var arc = Assert.Single(volley.SecondaryHits);
        Assert.Equal(near, arc.Health.ShipId);
        Assert.True(arc.Applied > 0);
        Assert.Equal(4, Assert.Single(volley.Magazines).Quantity);
    }

    [Fact]
    public void PodFlightAttachesThenDealsExactlyOneResistanceBypassingTick()
    {
        using var fixture = Prepare(85087);
        long module = MissileCombatTests.Load(fixture, PodLauncher, SmallPod);
        var npc = MissileCombatTests.Space(fixture, 500);
        var launch = MissileCombatTests.Launch(fixture, module, npc.Id);
        Assert.False(launch.Launcher.Repeats);
        Assert.Equal(2, launch.Magazine.Quantity);
        var missiles = MissileCombatTests.Missiles(fixture);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        missiles.Advance(npc.SystemId, fixture.Clock.Now);
        Assert.Empty(missiles.List(npc.SystemId));
        Assert.Empty(missiles.TakeImpacts(fixture.PilotId));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        missiles.Advance(npc.SystemId, fixture.Clock.Now);
        var impact = Assert.Single(missiles.TakeImpacts(fixture.PilotId));
        Assert.Equal(200, impact.Applied, 6);
        missiles.Advance(npc.SystemId, fixture.Clock.Now);
        Assert.Empty(missiles.TakeImpacts(fixture.PilotId));
    }

    [Fact]
    public void BreachControlPaysForOneTwoMinuteActivation()
    {
        using var fixture = Prepare(47269);
        long module = fixture.Fit(BreacherRules.CounterType);
        _ = MissileCombatTests.Space(fixture);
        var store = fixture.Db.Services.GetRequiredService<IPilotAssaultDamageControls>();
        Assert.Throws<InvalidDataException>(() =>
            store.Activate(fixture.PilotId, module, fixture.Clock.Now, DogmaEffect.AssaultDamageControl)
        );
        var paid = store.Activate(fixture.PilotId, module, fixture.Clock.Now, DogmaEffect.BreachControl);
        Assert.Equal(120000, paid.Module.DurationMilliseconds);
        Assert.True(store.Active(fixture.PilotId, fixture.ShipId, module, fixture.Clock.Now));
        Assert.Throws<InvalidDataException>(() => store.Activate(fixture.PilotId, module, fixture.Clock.Now, DogmaEffect.BreachControl));
    }

    private static Fixture Prepare(int hull)
    {
        var fixture = MissileCombatTests.Prepare();
        PassiveCatalogFixture.LoadAdvancedWeapons(fixture.CatalogPath);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Ships.Find(fixture.ShipId)!.TypeId = hull;
            world.SaveChanges();
        }
        using CatalogDbContext catalog = new(fixture.CatalogPath, writable: true);
        foreach (var attribute in new[] { DogmaAttributeId.CpuOutput, DogmaAttributeId.PowerOutput, DogmaAttributeId.CapacitorCapacity })
        {
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({hull},{(int)attribute},1000000)");
        }
        return fixture;
    }
}
