using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class AreaWeaponTests
{
    [Fact]
    public void DefenderPaysForAnEmptySweepAndInterceptsOneThreateningBombWithAPaidCharge()
    {
        using var fixture = Prepare(16242);
        long module = fixture.Fit(44102);
        Load(fixture, module, 32782);
        var victim = Place(fixture, new(30000, 0, 0));
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Pilots.Find(victim.PilotId)!.CorporationId =
                world.Pilots.Find(fixture.PilotId)!.CorporationId == 1000044 ? 1000169 : 1000044;
            world.SaveChanges();
        }
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var empty = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.UseMissiles,
            new(1, 0, 0),
            SpacePosition.Zero,
            null,
            fixture.Clock.Now
        );
        Assert.Null(empty.FlightId);
        Assert.Null(empty.Charge);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromSeconds(125).Ticks, empty.ReadyAt);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(125));
        long bombId;
        using (var action = live.Economy.Begin(new(pilots: [victim.PilotId], ships: [victim.ShipId])))
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        {
            var bomb = AreaWeaponCatalog.Bomb(catalog, Concussion);
            var profile = AreaWeaponRules.Define(
                module,
                BombLauncher,
                AreaWeaponRules.BombLauncherGroup,
                DogmaEffect.UseMissiles,
                catalog.FittingAttributes(BombLauncher)
            );
            SavedAreaWeapon row = new()
            {
                Id = action.Next("SavedAreaWeapon", AreaWeaponRules.FirstFlightId),
                PilotId = victim.PilotId,
                SourceShipId = victim.ShipId,
                ModuleId = module,
                SystemId = SystemId,
            };
            row.Save(
                new(
                    profile,
                    bomb,
                    new(30000, 0, 0),
                    new(-2500, 0, 0),
                    SpacePosition.Zero,
                    fixture.Clock.Now,
                    fixture.Clock.Now + TimeSpan.FromSeconds(12).Ticks,
                    Health: bomb.Defense.Undamaged
                )
            );
            action.Add(row);
            action.Commit();
            bombId = row.Id;
        }
        var intercepted = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.UseMissiles,
            new(1, 0, 0),
            SpacePosition.Zero,
            null,
            fixture.Clock.Now
        );
        Assert.NotNull(intercepted.FlightId);
        Assert.Equal(1, intercepted.Charge!.Current!.Quantity);
        var defender = Assert.Single(store.Projectiles(SystemId, fixture.Clock.Now), contact => contact.TargetId == bombId);
        Assert.Equal(15000, defender.Flight.Drive.MaxVelocity);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
        store.Advance(SystemId, fixture.Clock.Now);
        Assert.Empty(store.Projectiles(SystemId, fixture.Clock.Now));
        Assert.Throws<InvalidDataException>(() =>
            store.Activate(
                fixture.PilotId,
                fixture.ShipId,
                module,
                DogmaEffect.UseMissiles,
                new(1, 0, 0),
                SpacePosition.Zero,
                null,
                fixture.Clock.Now
            )
        );
    }

    [Theory]
    [InlineData(27678, DogmaEffect.ProjectorEcm)]
    [InlineData(40636, DogmaEffect.ProjectorNeutralizer)]
    [InlineData(40696, DogmaEffect.ProjectorBubble)]
    [InlineData(40697, DogmaEffect.ProjectorDampener)]
    [InlineData(40698, DogmaEffect.ProjectorPainter)]
    [InlineData(40699, DogmaEffect.ProjectorDisruptor)]
    public void ProjectorFamiliesApplyTheirActualEffectAndPersistMultipleRecipients(int type, DogmaEffect effect)
    {
        using var fixture = Prepare(projectors: true);
        long module = fixture.Fit(type);
        fixture.Grant(16272, 250, cargo: true);
        var target = Place(fixture, new(50000, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var before = fitting.ShipAttributes(target.PilotId);
        _ = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            effect,
            new(1, 0, 0),
            SpacePosition.Zero,
            new(50000, 0, 0),
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        store.Advance(SystemId, fixture.Clock.Now);
        var after = fitting.ShipAttributes(target.PilotId);
        var contacts = fixture.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(target.ShipId);
        switch (effect)
        {
            case DogmaEffect.ProjectorEcm:
                var jam = Assert.Single(contacts);
                Assert.NotNull(jam.Applied);
                Assert.False(PlayerEwarRules.CanTarget(contacts, fixture.ShipId));
                Assert.False(PlayerEwarRules.CanTarget(contacts, 123));
                break;
            case DogmaEffect.ProjectorNeutralizer:
                Assert.Empty(contacts);
                var energy = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(target.ShipId)!.Charge);
                Assert.True(energy < before[DogmaAttributeId.CapacitorCapacity]);
                break;
            case DogmaEffect.ProjectorBubble:
                Assert.True(
                    fixture
                        .Db.Services.GetRequiredService<IPilotInterdiction>()
                        .Resolve(target.ShipId, SystemId, new(50000, 0, 0), new(1000000, 0, 0), fixture.Clock.Now)
                        .Blocked
                );
                break;
            case DogmaEffect.ProjectorDampener:
                Assert.Equal(before[DogmaAttributeId.MaxTargetRange] * .82, after[DogmaAttributeId.MaxTargetRange], 6);
                Assert.Equal(before[DogmaAttributeId.ScanResolution] * .82, after[DogmaAttributeId.ScanResolution], 6);
                break;
            case DogmaEffect.ProjectorPainter:
                Assert.Equal(before[DogmaAttributeId.SignatureRadius] * 1.35, after[DogmaAttributeId.SignatureRadius], 6);
                break;
            case DogmaEffect.ProjectorDisruptor:
                var power = Assert.Single(contacts).Applied!;
                Assert.Equal(-20, power.TrackingPercent);
                Assert.Equal(-14, power.ExplosionVelocityPercent);
                Assert.Equal(14, power.ExplosionRadiusPercent);
                break;
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Equal(contacts.Count, saved.Set<WorldPlayerEwar>().Count());
    }

    [Fact]
    public void RejectedProjectorFuelAndPointLeaveCapacitorFuelAndCooldownUnchanged()
    {
        using var fixture = Prepare(projectors: true);
        long module = fixture.Fit(WebProjector);
        long fuel = fixture.Grant(16272, 249, cargo: true);
        _ = Place(fixture, new(50000, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        foreach (var point in new[] { new SpacePosition(double.NaN, 0, 0), new(1000000, 0, 0), new(50000, 0, 0) })
        {
            Assert.Throws<InvalidDataException>(() =>
                store.Activate(
                    fixture.PilotId,
                    fixture.ShipId,
                    module,
                    DogmaEffect.ProjectorWeb,
                    new(1, 0, 0),
                    SpacePosition.Zero,
                    point,
                    fixture.Clock.Now
                )
            );
        }
        live.Economy.Inspect(world =>
        {
            Assert.Equal(249, world.Set<OwnedItem>().Find(fuel)!.Quantity);
            Assert.Null(world.Set<ModuleCooldown>().Find(module));
            Assert.Null(world.Set<ShipEnergy>().Find(fixture.ShipId));
            Assert.Empty(world.Set<SavedAreaWeapon>());
            return true;
        });
    }

    [Fact]
    public void NativeBurstAndAimedProjectorRoutesPayAndRejectMalformedRequests()
    {
        using var fixture = Prepare(projectors: true);
        long module = fixture.Fit(WebProjector);
        fixture.Grant(16272, 250, cargo: true);
        _ = Place(fixture, new(50000, 0, 0));
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using var scope = fixture.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var handler = scope.ServiceProvider.GetRequiredService<AreaWeaponHandler>();
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(
                context,
                "superWeaponMgr",
                "ActivateSinglePointTargetedModule",
                [module, (long)DogmaEffect.ProjectorWeb, new object?[] { double.NaN, 0d, 0d }]
            )
        );
        var point = context.Pilot.Position! + new SpacePosition(50000, 0, 0);
        Assert.Same(
            PilotCallResult.None,
            handler.Call(
                context,
                "superWeaponMgr",
                "ActivateSinglePointTargetedModule",
                [module, (long)DogmaEffect.ProjectorWeb, new object?[] { point.X, point.Y, point.Z }]
            )
        );
        Assert.Contains(context.Notifications, notification => notification is ShipEffectChanged { Start: true });
        Assert.Contains(context.Notifications, notification => notification is ItemChanged);
        Assert.NotNull(handler.ActiveEffect(pilot, module));
    }
}
