using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcMissileTests
{
    private const int Charge = 212;

    private static void Prepare(Fixture f, bool turret = false)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES (8,'Charge');
                INSERT OR IGNORE INTO ItemGroup VALUES (384,'Light Missile',8);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES (212,'Test missile',384,1000,1);
                """
            );
            Dictionary<DogmaAttributeId, double> charge = new()
            {
                [DogmaAttributeId.MaxVelocity] = 2000,
                [DogmaAttributeId.Agility] = 0.1,
                [DogmaAttributeId.MissileFlightTime] = 10000,
                [DogmaAttributeId.EmDamage] = 10,
                [DogmaAttributeId.ExplosionRadius] = 40,
                [DogmaAttributeId.ExplosionVelocity] = 1000,
                [DogmaAttributeId.MissileReductionExponent] = 1,
            };
            foreach (var (attribute, value) in charge)
            {
                catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({(int)attribute},{attribute.ToString()})");
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({Charge},{(int)attribute},{value})");
            }
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO DogmaAttribute VALUES (507,'entityMissileTypeID'),(506,'missileLaunchDuration');
                INSERT OR REPLACE INTO TypeAttribute VALUES (30380,507,212),(30380,506,2500);
                """
            );
            if (!turret)
            {
                catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=30380 AND AttributeId IN (245,51)");
            }
        }
        f.Db.Store.SetPosition(f.Pilot.ShipId, f.Npc.Position + new SpacePosition(4000, 0, 0), f.Npc.SystemId);
        using WorldDbContext world = new(f.Db.Path);
        world.Add(NpcAggression.Start(f.Npc.Id, f.Pilot.ShipId, 0, f.Clock.Now));
        world.SaveChanges();
    }

    private static INpcMissiles Incoming(Fixture f) => f.Db.Services.GetRequiredService<INpcMissiles>();

    private static NpcShot? Fire(Fixture f) =>
        f.Combat.Fire(f.Pilot.PilotId, f.Pilot.ShipId, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, 1);

    [Fact]
    public void MissileOnlyNpcLaunchesARealProjectileAndDamageWaitsForContact()
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.Pilot.ShipId;
        var before = f.Health.Read(f.Pilot.PilotId, f.Clock.Now);
        Assert.Null(Fire(f));
        var outbound = f.Db.Services.GetRequiredService<IPilotMissiles>();
        var contact = Assert.Single(outbound.List(f.Npc.SystemId));
        Assert.Equal(f.Npc.Id, contact.SourceShipId);
        Assert.Equal(ship, contact.TargetId);
        Assert.Equal(f.Npc.CorporationId, contact.CorporationId);
        Assert.Equal(before, f.Health.Read(f.Pilot.PilotId, f.Clock.Now));
        Assert.Empty(Incoming(f).Advance(f.Pilot.PilotId, ship, SpacePosition.Zero, f.Clock.Now));
        List<NpcShot> shots = [];
        for (int tick = 0; tick < 8; tick++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            shots.AddRange(Incoming(f).Advance(f.Pilot.PilotId, ship, SpacePosition.Zero, f.Clock.Now));
        }
        var impact = Assert.Single(shots);
        Assert.True(impact.IsMissile);
        Assert.True(impact.Applied > 0);
        Assert.Empty(Incoming(f).Advance(f.Pilot.PilotId, ship, SpacePosition.Zero, f.Clock.Now));
        Assert.True(outbound.Impacted(contact.Id));
        Assert.Empty(outbound.List(f.Npc.SystemId));
    }

    [Fact]
    public void LethalMissileUsesOneSavedShipLossAndCapsule()
    {
        using Fixture f = new();
        Prepare(f);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=100000 WHERE ItemTypeId=212 AND AttributeId=114");
        }
        long pilot = f.Pilot.PilotId;
        long ship = f.Pilot.ShipId;
        Fire(f);
        List<NpcShot> shots = [];
        for (int tick = 0; tick < 8; tick++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            shots.AddRange(Incoming(f).Advance(pilot, ship, SpacePosition.Zero, f.Clock.Now));
        }
        var shot = Assert.Single(shots);
        Assert.True(shot.Health.Remaining.Destroyed);
        Assert.NotNull(shot.Capsule);
        Assert.NotEqual(ship, shot.Capsule.ShipId);
        var report = Assert.Single(f.Db.Services.GetRequiredService<IPilotInteractionHistory>().Combat(pilot, 25, null));
        Assert.Equal(pilot, report.Victim.CharacterId);
        Assert.Null(report.FinalBlow.CharacterId);
        Assert.Equal(212, report.FinalBlow.WeaponTypeId);
        Assert.Equal(f.Npc.TypeId, report.FinalBlow.ShipTypeId);
        Assert.Equal(shot.Applied, report.Victim.Damage, 8);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(ship, Assert.Single(world.Set<ShipLoss>()).ShipId);
        Assert.Equal(ship, Assert.Single(world.Set<WorldWreck>()).ShipId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisengagementAndRestartCancelFlightWithoutOfflineDamage(bool restart)
    {
        using Fixture f = new();
        Prepare(f);
        long pilot = f.Pilot.PilotId;
        long ship = f.Pilot.ShipId;
        Fire(f);
        if (restart)
        {
            _ = WorldInitialization.Open(f.Db.Path);
        }
        else
        {
            f.Combat.Disengage(ship);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        Assert.Empty(Incoming(f).Advance(pilot, ship, SpacePosition.Zero, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ShipLoss>());
        Assert.All(world.Set<WorldMissile>(), m => Assert.Equal(MissileFlightStatus.Expired, m.Status));
    }

    [Fact]
    public void TurretAndMissileCyclesRemainIndependentAndRepeatedCallsCannotRelaunch()
    {
        using Fixture f = new();
        Prepare(f, turret: true);
        // The fixture starts nearly destroyed; restore enough health to test cycles.
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<ShipHealthState>().Where(h => h.ShipId == f.Pilot.ShipId).ExecuteDelete();
        }
        Fire(f);
        Fire(f);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<WorldMissile>());
        var aggression = saved.Set<NpcAggression>().Find(f.Npc.Id)!;
        Assert.NotEqual(aggression.ReadyAt, aggression.MissileReadyAt);
        Assert.Equal(f.Clock.Now + TimeSpan.FromMilliseconds(2500).Ticks, aggression.MissileReadyAt);
    }

    [Fact]
    public void SourceDestructionCancelsItsProjectile()
    {
        using Fixture f = new();
        Prepare(f);
        Fire(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.DestroyedAt = f.Clock.Now;
            world.SaveChanges();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(3));
        Assert.Empty(Incoming(f).Advance(f.Pilot.PilotId, f.Pilot.ShipId, SpacePosition.Zero, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(MissileFlightStatus.Expired, Assert.Single(saved.Set<WorldMissile>()).Status);
    }

    [Fact]
    public void AnEscapingTargetTakesNoDamageAndFlightExpires()
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.Pilot.ShipId;
        Fire(f);
        for (int tick = 1; tick <= 12; tick++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            f.Db.Store.SetPosition(ship, f.Npc.Position + new SpacePosition(4000 + tick * 50000, 0, 0), f.Npc.SystemId);
            Assert.Empty(Incoming(f).Advance(f.Pilot.PilotId, ship, new(50000, 0, 0), f.Clock.Now));
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(MissileFlightStatus.Expired, Assert.Single(saved.Set<WorldMissile>()).Status);
        Assert.Empty(saved.Set<ShipLoss>());
    }

    [Fact]
    public void Version65UpgradeKeepsProjectileIdentitySnapshotAndSequence()
    {
        using Fixture f = new();
        Prepare(f);
        Fire(f);
        long projectile;
        string snapshot;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var row = Assert.Single(world.Set<WorldMissile>());
            projectile = row.Id;
            snapshot = row.Snapshot;
            row.SourceShipId = f.Pilot.ShipId;
            row.TargetId = f.Npc.Id;
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldMissile DROP COLUMN SourceKind;
                ALTER TABLE WorldMissile DROP COLUMN SourceTypeId;
                ALTER TABLE WorldNpcAggression DROP COLUMN MissileReadyAt;
                UPDATE sqlite_sequence SET seq=seq+100 WHERE name='WorldMissile';
                PRAGMA user_version=65;
                """
            );
        }
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        var migrated = Assert.Single(saved.Set<WorldMissile>());
        Assert.Equal(projectile, migrated.Id);
        Assert.Equal(snapshot, migrated.Snapshot);
        Assert.Equal(MissileSourceKind.Pilot, migrated.SourceKind);
        Assert.Equal(MissileFlightStatus.Expired, migrated.Status);
        Assert.Equal(
            projectile + 100,
            saved.Database.SqlQueryRaw<long>("SELECT seq AS Value FROM sqlite_sequence WHERE name='WorldMissile'").Single()
        );
    }

    [Fact]
    public void NpcModifiersSnapshotDriveAndWarheadWithoutMutatingTheCharge()
    {
        Dictionary<DogmaAttributeId, double> charge = new()
        {
            [DogmaAttributeId.KineticDamage] = 10,
            [DogmaAttributeId.ExplosionRadius] = 40,
            [DogmaAttributeId.ExplosionVelocity] = 100,
            [DogmaAttributeId.MissileReductionExponent] = 0.5,
        };
        Dictionary<DogmaAttributeId, double> modifiers = new()
        {
            [DogmaAttributeId.MissileDamageMultiplier] = 2,
            [DogmaAttributeId.EntityMissileVelocityMultiplier] = 1.5,
            [DogmaAttributeId.EntityMissileFlightTimeMultiplier] = 2,
            [DogmaAttributeId.EntityMissileExplosionRadiusMultiplier] = 0.5,
            [DogmaAttributeId.EntityMissileExplosionVelocityMultiplier] = 3,
            [DogmaAttributeId.EntityMissileReductionMultiplier] = 2,
        };
        var drive = NpcMissileRules.Drive(new(1000, 1, 2000, 0.1f, 10000), modifiers);
        var warhead = NpcMissileRules.Warhead(charge, modifiers);
        Assert.Equal(3000, drive.MaxVelocity);
        Assert.Equal(20000, drive.LifetimeMilliseconds);
        Assert.Equal(20, warhead.Damage.Kinetic);
        Assert.Equal(20, warhead.ExplosionRadius);
        Assert.Equal(300, warhead.ExplosionVelocity);
        Assert.Equal(1, warhead.ReductionExponent);
        Assert.Equal(40, charge[DogmaAttributeId.ExplosionRadius]);
        modifiers[DogmaAttributeId.EntityMissileVelocityMultiplier] = double.NaN;
        Assert.Throws<InvalidDataException>(() => NpcMissileRules.Drive(drive, modifiers));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombatHandlerStreamsNpcMissilesAndDeliversIncomingDamage(bool resident)
    {
        using Fixture f = new(liveWorld: resident);
        Prepare(f);
        using var scope = f.Db.Services.CreateScope();
        SharedSpace space = new();
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            pilot =>
                new(
                    f.Db.Store,
                    pilot,
                    f.Clock,
                    space,
                    [],
                    health: f.Health,
                    encounters: f.Db.Services.GetRequiredService<INpcEncounters>(),
                    missiles: f.Db.Services.GetRequiredService<IPilotMissiles>()
                ),
            42,
            SignedSettings.Empty
        );
        context.Select(f.Pilot);
        context.Flight.Snapshot();
        using NpcCombatHandler handler = new(
            f.Clock,
            f.Combat,
            new SharedTargetLocks(),
            scope.ServiceProvider.GetRequiredService<ShipLossCoordinator>(),
            space,
            Incoming(f)
        );
        handler.Enter(context);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(NpcAggression.Start(f.Npc.Id, f.Pilot.ShipId, 0, f.Clock.Now));
            world.SaveChanges();
        }
        handler.Advance(context);
        var missile = Assert.Single(context.Flight.VisibleMissiles);
        var slim = Assert.IsType<BlueInstance>(WireAssertions.Materialize(Assert.Single(Ballpark.AddMissiles([missile], 42).Slims)));
        var fields = Assert.IsType<Dictionary<string, object?>>(slim.State);
        Assert.Equal(f.Npc.Id, fields["sourceShipID"]);
        Assert.Equal((long)f.Npc.CorporationId, fields["ownerID"]);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=30380 AND AttributeId=507");
        }
        for (int tick = 0; tick < 8; tick++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            handler.Advance(context);
        }
        Assert.Contains(context.NotificationsBeforeFlight, n => n is DamageMessageNotification);
        Assert.Empty(context.Flight.VisibleMissiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FasterNewerMissileOwnsTheEarlierFatalContact(bool resident)
    {
        using Fixture f = new(liveWorld: resident);
        Prepare(f);
        long ship = f.Pilot.ShipId;
        Fire(f);
        long fasterId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var original = Assert.Single(world.Set<WorldMissile>());
            var payload = original.Payload();
            var fast = new WorldMissile
            {
                SourceKind = original.SourceKind,
                SourceTypeId = original.SourceTypeId,
                PilotId = original.PilotId,
                SourceShipId = original.SourceShipId,
                ModuleId = original.ModuleId,
                ChargeTypeId = original.ChargeTypeId,
                GroupId = original.GroupId,
                SystemId = original.SystemId,
                TargetId = original.TargetId,
                TargetRadius = original.TargetRadius,
                CorporationId = original.CorporationId,
                LaunchedAt = original.LaunchedAt,
                UpdatedAt = original.UpdatedAt,
                ExpiresAt = original.ExpiresAt,
                Snapshot = original.Snapshot,
            };
            fast.Save(
                payload with
                {
                    Flight = payload.Flight with { Drive = payload.Flight.Drive with { MaxVelocity = 10000 } },
                    Warhead = payload.Warhead with { Damage = new(100000, 0, 0, 0) },
                }
            );
            world.Add(fast);
            world.SaveChanges();
            fasterId = fast.Id;
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(8));
        var shot = Assert.Single(Incoming(f).Advance(f.Pilot.PilotId, ship, SpacePosition.Zero, f.Clock.Now));
        Assert.True(shot.Health.Remaining.Destroyed);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(fasterId, Assert.Single(saved.Set<WorldMissile>().Where(m => m.Status == MissileFlightStatus.Impact)).Id);
        Assert.Single(saved.Set<ShipLoss>());
    }

    [Fact]
    public void LegacyPositionWithoutSystemUsesTheShipsStationSystem()
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.Pilot.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.SystemId = 30000142;
            world.Positions.Find(ship)!.SystemId = null;
            world.SaveChanges();
        }
        Assert.Null(Fire(f));
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(5));
        Assert.Single(Incoming(f).Advance(f.Pilot.PilotId, ship, SpacePosition.Zero, f.Clock.Now));
    }

    [Fact]
    public void LongRangeMissileNpcDetectsAndEngagesBeyondTheOriginalBeltLeash()
    {
        using Fixture f = new();
        Prepare(f);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=100000 WHERE ItemTypeId=212 AND AttributeId=281");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<NpcAggression>().ExecuteDelete();
        }
        long ship = f.Pilot.ShipId;
        f.Db.Store.SetPosition(ship, f.Npc.Position + new SpacePosition(100000, 0, 0), f.Npc.SystemId);
        f.Combat.Acquire([new(f.Pilot.PilotId, ship)], f.Clock.Now);
        Assert.Contains(f.Npc.Id, f.Combat.Aggressors(ship));
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(8));
        Assert.Null(Fire(f));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<WorldMissile>());
        Assert.Single(saved.Set<NpcAggression>());
    }
}
