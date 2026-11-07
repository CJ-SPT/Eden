using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PlayerEwarTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JammedNpcCanShootTheJammerButCannotRetaliateAgainstTheJammersDrones(bool resident)
    {
        using CombatDroneTests.Fixture f = new();
        PassiveCatalogFixture.LoadPlayerEwar(f.Base.CatalogPath);
        long module = f.Base.Fit(1948);
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (30380,211,10)");
        }
        var database = f.Base.Db.Services.GetRequiredService<WorldDatabase>();
        using LiveWorld? live = resident ? new(database, f.Base.Clock, _ => { }) : null;
        var drones = f.Launch(quantity: 1);
        f.Drones.Order(f.Pilot, drones, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now);
        Store(f.Base)
            .Activate(f.Pilot, module, DogmaEffect.PlayerEcm, new(f.Npc.Id, TackleTargetKind.Npc), new HashSet<long> { f.Npc.Id }, f.Now);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Set<NpcAggression>().Find(f.Npc.Id)!.ReadyAt = f.Now;
            world.SaveChanges();
        }
        if (resident)
        {
            database.MotionStamps[f.Npc.SystemId] = 1;
        }
        var combat = f.Base.Db.Services.GetRequiredService<INpcCombat>();
        var jammed = Assert.IsType<NpcShot>(combat.Fire(f.Pilot, f.Ship, f.Npc.Id, SpacePosition.Zero, f.Now, 0, 1));
        Assert.Equal(f.Ship, jammed.Health.ShipId);
        Store(f.Base).Stop(f.Pilot, f.Ship, module);
        f.Base.Clock.Seconds += 3;
        var restored = Assert.IsType<NpcShot>(combat.Fire(f.Pilot, f.Ship, f.Npc.Id, SpacePosition.Zero, f.Now, 0, 1));
        Assert.Equal(drones[0], restored.Health.ShipId);
    }

    [Fact]
    public void WarmSourceAndVictimCyclesReadResidentContactsAndMembershipWithoutSqlAndSeeOfflineChanges()
    {
        using Fixture f = Prepare();
        long module = f.Fit(12709);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using Clients clients = new(f);
        long victim = Wing(f);
        clients.Handler.Call(clients.Pilot, "dogmaIM", "Activate", [module, "remoteTargetPaintFalloff", Wing(f), 2L]);
        clients.Handler.Advance(clients.Pilot);
        clients.WingHandler.Advance(clients.Wing);
        clients.Targeting.Advance(clients.Pilot);
        clients.WingTargeting.Advance(clients.Wing);
        using (var metrics = SessionPerformance.Begin("warm player EWAR", _ => { }, force: true))
        {
            for (int frame = 0; frame < 10; frame++)
            {
                f.Clock.Milliseconds += 50;
                clients.Handler.Advance(clients.Pilot);
                clients.WingHandler.Advance(clients.Wing);
                clients.Targeting.Advance(clients.Pilot);
                clients.WingTargeting.Advance(clients.Wing);
                Assert.Single(Store(f).Incoming(victim));
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Online = false;
            world.SaveChanges();
        }
        clients.Handler.Advance(clients.Pilot);
        Assert.Empty(Store(f).Incoming(victim));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NpcTargetsReceiveEffectivePaintingAndDisruptionAndResidentCachesRestoreOnStop(bool resident)
    {
        using Fixture f = Prepare();
        long painter = f.Fit(12709);
        long disruptor = f.Fit(2108);
        NpcEncounterTests.Prepare(f.Db);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock) : null;
        double baseline = f.Fitting.SignatureRadius(npc.Id, npc.TypeId);
        Store(f)
            .Activate(
                f.PilotId,
                painter,
                DogmaEffect.TargetPainter,
                new(npc.Id, TackleTargetKind.Npc),
                new HashSet<long> { npc.Id },
                f.Clock.Now
            );
        Store(f)
            .Activate(
                f.PilotId,
                disruptor,
                DogmaEffect.TrackingDisruptor,
                new(npc.Id, TackleTargetKind.Npc),
                new HashSet<long> { npc.Id },
                f.Clock.Now
            );
        Assert.Equal(baseline * 1.25, f.Fitting.SignatureRadius(npc.Id, npc.TypeId), 7);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var raw = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
            var changed = PlayerEwarStore.NpcAttributes(world, npc.Id, raw);
            Assert.Equal(raw[DogmaAttributeId.TrackingSpeed] * .847, changed[DogmaAttributeId.TrackingSpeed], 7);
            Assert.Equal(raw[DogmaAttributeId.MaxRange] * .847, changed[DogmaAttributeId.MaxRange], 7);
        }
        _ = encounters.List(npc.SystemId, f.Clock.Now);
        Store(f).Stop(f.PilotId, f.ShipId, painter);
        Store(f).Stop(f.PilotId, f.ShipId, disruptor);
        Assert.Equal(baseline, f.Fitting.SignatureRadius(npc.Id, npc.TypeId));
        _ = encounters.List(npc.SystemId, f.Clock.Now);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NpcJammingBlocksOtherVictimsInBothSqlAndResidentCombatButAllowsTheJammer(bool resident)
    {
        using Fixture f = Prepare();
        long jammer = f.Fit(1948);
        NpcEncounterTests.Prepare(f.Db);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (30380,211,10)");
        }
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        f.Db.Store.SetPosition(Wing(f), npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        var database = f.Db.Services.GetRequiredService<WorldDatabase>();
        using LiveWorld? live = resident ? new(database, f.Clock) : null;
        var activation = Store(f)
            .Activate(
                f.PilotId,
                jammer,
                DogmaEffect.PlayerEcm,
                new(npc.Id, TackleTargetKind.Npc),
                new HashSet<long> { npc.Id },
                f.Clock.Now
            );
        Assert.NotNull(Assert.Single(activation.Contacts).Applied);
        Assert.True(PlayerEwarRules.CanTarget(activation.Contacts, f.ShipId));
        Assert.False(PlayerEwarRules.CanTarget(activation.Contacts, Wing(f)));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var aggression = world.Set<NpcAggression>().Find(npc.Id)!;
            aggression.TargetShipId = Wing(f);
            aggression.ReadyAt = f.Clock.Now;
            world.SaveChanges();
        }
        if (resident)
        {
            database.MotionStamps[npc.SystemId] = 1;
        }
        var combat = f.Db.Services.GetRequiredService<INpcCombat>();
        Assert.Null(combat.Fire(f.Db.Store.FindPilot(1000002)!.PilotId, Wing(f), npc.Id, SpacePosition.Zero, f.Clock.Now, .5, 1));
        Store(f).Stop(f.PilotId, f.ShipId, jammer);
        Assert.NotNull(combat.Fire(f.Db.Store.FindPilot(1000002)!.PilotId, Wing(f), npc.Id, SpacePosition.Zero, f.Clock.Now, .5, 1));
    }

    [Fact]
    public void NpcGuidanceDisruptionChangesMissilesWithoutChangingShipSpeedAndDampeningUsesTheDetectionEnvelope()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var guidance = PlayerEwarRules.Define(1, 37543, DogmaEffect.GuidanceDisruptor, PilotFittingStore.ReadAttributes(catalog, 37543));
        var dampener = PlayerEwarRules.Define(2, 1968, DogmaEffect.SensorDampener, PilotFittingStore.ReadAttributes(catalog, 1968));
        Dictionary<DogmaAttributeId, double> npc = new()
        {
            [DogmaAttributeId.MaxVelocity] = 500,
            [DogmaAttributeId.EntityAttackRange] = 30000,
        };
        ElectronicWarfareContact Contact(ElectronicWarfareModule module) =>
            new(1, 2, module, new(3, TackleTargetKind.Npc), module.Power, 0, 100000);
        var changed = PlayerEwarRules.NpcAttributes(npc, [Contact(guidance), Contact(dampener)], 60000);
        Assert.Equal(500, changed[DogmaAttributeId.MaxVelocity]);
        Assert.Equal(1 + guidance.Power.MissileSpeedPercent / 100, changed[DogmaAttributeId.EntityMissileVelocityMultiplier], 7);
        Assert.Equal(1 + guidance.Power.ExplosionRadiusPercent / 100, changed[DogmaAttributeId.EntityMissileExplosionRadiusMultiplier], 7);
        Assert.Equal(60000 * dampener.Power.TargetRangeMultiplier, changed[DogmaAttributeId.MaxTargetRange], 7);
    }
}
