using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Characters;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class CapsuleCombatTests
{
    private const long Victim = 90000002;
    private const long Capsule = 1000000000002;
    private const int SystemId = 30000144;
    private const int OcularImplant = 9899;
    private const long ActiveImplant = 2000000000400;
    private const long StoredImplant = 2000000000401;
    private const int CapsuleVariant = 9991670;

    private static Fixture Prepare(bool resident = false, double security = 0)
    {
        Fixture f = new(liveWorld: resident, useTestClock: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            string data = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = security });
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144',{data})");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(Capsule)!.TypeId = FleetDefinitions.CapsuleType;
            world.Set<Account>().Find(1000002L)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(Capsule, f.Pilot.Position! + new SpacePosition(500, 0, 0), SystemId);
        return f;
    }

    private static void AlmostLost(Fixture f)
    {
        var defense = f.Health.Read(Victim, f.Clock.Now).Defense;
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipHealthState
            {
                ShipId = Capsule,
                ShieldCharge = 0,
                ArmorDamage = defense.Armor,
                StructureDamage = defense.Structure - 1,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    private static CombatShot Fire(Fixture f) =>
        f.Weapons.FirePlayerBank(Fixture.PilotId, f.Module, new(Capsule, SpacePosition.Zero), SpacePosition.Zero, f.Clock.Now, 0).Shot;

    private static long AddBodies(Fixture f)
    {
        using WorldDbContext world = new(f.Db.Path);
        var active = world.Add(new SavedJumpClone { PilotId = Victim, Active = true }).Entity;
        var stored = world
            .Add(
                new SavedJumpClone
                {
                    PilotId = Victim,
                    StationId = f.Pilot.StationId,
                    Name = "Stored body",
                }
            )
            .Entity;
        world.SaveChanges();
        world.AddRange(
            new SavedPilotImplant
            {
                ItemId = ActiveImplant,
                CloneId = active.Id,
                TypeId = OcularImplant,
                Slot = 1,
                Attribute = DogmaAttributeId.Perception,
                Bonus = 3,
            },
            new SavedPilotImplant
            {
                ItemId = StoredImplant,
                CloneId = stored.Id,
                TypeId = OcularImplant,
                Slot = 1,
                Attribute = DogmaAttributeId.Perception,
                Bonus = 3,
            }
        );
        world.Add(new SavedJumpCloneTimer { PilotId = Victim, Time = f.Clock.Now });
        world.SaveChanges();
        return stored.Id;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalCapsuleHitRespawnsAtHomeAndPersistsBodyLossWithoutInsuranceOrNpcRewards(bool resident)
    {
        using var f = Prepare(resident);
        long storedBody = AddBodies(f);
        var homes = f.Db.Services.GetRequiredService<IPilotHomeStation>();
        var home = homes.Read(Victim);
        Assert.NotEqual(SystemId, home.SystemId);
        long balance;
        int skills;
        int assets;
        using (WorldDbContext world = new(f.Db.Path))
        {
            balance = world.Set<PilotWallet>().Find(Victim)?.Cents ?? 0;
            skills = world.Set<PilotSkill>().Count(s => s.PilotId == Victim);
            assets = world.Set<OwnedItem>().Count(i => i.OwnerId == Victim);
        }
        AlmostLost(f);
        List<PilotDamage> messages = [];
        f.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        Assert.True(Fire(f).Destroyed);
        var message = Assert.Single(messages);
        var replacement = Assert.IsType<DockedPilot>(message.Capsule);
        Assert.Null(replacement.Position);
        Assert.Equal(home.Id, replacement.StationId);
        Assert.Equal(home.SystemId, replacement.SystemId);
        Assert.Equal(FleetDefinitions.CapsuleGroup, replacement.ShipGroupId);
        Assert.NotEqual(Capsule, replacement.ShipId);
        Assert.Null(message.Insurance);
        Assert.Equal(2, message.InventoryChanges.Count);
        f.Db.Services.GetService<LiveWorld>()?.Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(replacement, f.Db.Store.FindPilot(1000002));
        using WorldDbContext saved = new(f.Db.Path);
        var loss = Assert.Single(saved.Set<ShipLoss>());
        Assert.Equal(Capsule, loss.ShipId);
        Assert.Equal(Fixture.PilotId, loss.AttackerPilotId);
        Assert.Equal(replacement.ShipId, loss.CapsuleId);
        Assert.Null(saved.Ships.Find(Capsule));
        Assert.Null(saved.Set<SavedPilotImplant>().Find(ActiveImplant));
        Assert.Equal(storedBody, Assert.Single(saved.Set<SavedJumpClone>()).Id);
        Assert.NotNull(saved.Set<SavedPilotImplant>().Find(StoredImplant));
        Assert.Equal(1, saved.Set<SavedImplantState>().Find(Victim)!.Revision);
        Assert.Equal(f.Clock.Now, saved.Set<SavedJumpCloneTimer>().Find(Victim)!.Time);
        Assert.Equal(home.Id, Assert.Single(saved.Set<SavedHomeCloneTransfer>()).DestinationStationId);
        Assert.Equal(balance, saved.Set<PilotWallet>().Find(Victim)?.Cents ?? 0);
        Assert.Equal(skills, saved.Set<PilotSkill>().Count(s => s.PilotId == Victim));
        Assert.Equal(assets, saved.Set<OwnedItem>().Count(i => i.OwnerId == Victim));
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<SavedInsuranceLedger>());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<SavedSecurityStatus>());
        var history = f.Db.Services.GetRequiredService<IPilotInteractionHistory>();
        var report = Assert.Single(history.Combat(Victim, 10, null));
        var lostImplant = Assert.Single(report.Items);
        Assert.Equal(OcularImplant, lostImplant.TypeId);
        Assert.Equal(1, lostImplant.Destroyed);
        Assert.Equal(0, lostImplant.Dropped);
        Assert.Single(history.Combat(Fixture.PilotId, 10, null));
        Assert.Equal(0, saved.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Theory]
    [InlineData(0, ShipSafetyLevel.Full, false, true)]
    [InlineData(.2, ShipSafetyLevel.Full, false, false)]
    [InlineData(.2, ShipSafetyLevel.Partial, false, false)]
    [InlineData(.2, ShipSafetyLevel.Disabled, false, true)]
    [InlineData(.8, ShipSafetyLevel.Disabled, false, false)]
    [InlineData(.8, ShipSafetyLevel.Full, true, true)]
    public void CapsuleAttacksUseCriminalSafetyAndExistingLegalRights(double security, ShipSafetyLevel safety, bool suspect, bool allowed)
    {
        using var f = Prepare(security: security);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Pilots.Find(Fixture.PilotId)!.SafetyLevel = safety;
            if (suspect)
            {
                world.Add(
                    new SavedCrimewatch
                    {
                        PilotId = Victim,
                        SystemId = SystemId,
                        SuspectUntil = f.Clock.Now + CrimewatchRules.FlagDuration.Ticks,
                    }
                );
            }
            world.SaveChanges();
        }
        if (allowed)
        {
            Assert.False(Fire(f).Destroyed);
            using WorldDbContext saved = new(f.Db.Path);
            var state = saved.Set<SavedCrimewatch>().Find(Fixture.PilotId)!;
            Assert.Equal(security > 0 && !suspect ? CrimewatchFlag.Criminal : CrimewatchFlag.None, state.Timers().Flag(f.Clock.Now));
            Assert.True(state.Timers().Weapons.Running(f.Clock.Now));
        }
        else
        {
            var health = f.Health.Read(Victim, f.Clock.Now);
            Assert.Throws<InvalidDataException>(() => Fire(f));
            Assert.Equal(health, f.Health.Read(Victim, f.Clock.Now));
            using WorldDbContext saved = new(f.Db.Path);
            Assert.Null(saved.Set<SavedCrimewatch>().Find(Fixture.PilotId));
            Assert.Empty(saved.Set<ModuleCooldown>());
            Assert.Empty(saved.Set<ShipLoss>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BodyLossPreservesElapsedTrainingAndBoosterButRebasesTheRemainingQueue(bool resident)
    {
        using var f = Prepare(resident);
        AddBodies(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(Victim, 3327)!.Points = 250;
            world.Add(
                new SavedBooster
                {
                    ItemId = ActiveImplant + 10,
                    PilotId = Victim,
                    TypeId = 3898,
                    Slot = 1,
                    Bonus = 2,
                    StartedAt = f.Clock.Now,
                    ExpiresAt = f.Clock.Now + TimeSpan.FromDays(1).Ticks,
                }
            );
            world.SaveChanges();
        }
        var training = f.Db.Services.GetRequiredService<IPilotTraining>();
        var old = Assert.Single(training.Save(Victim, [new(3327, 2)], true).Queue);
        f.Clock.Seconds += 30;
        long expected = old.PointsAt(f.Clock.Now);
        AlmostLost(f);
        Fire(f);
        var next = Assert.Single(training.Advance(Victim).Queue);
        Assert.Equal(expected, next.StartPoints);
        Assert.Equal(old.EndPoints, next.EndPoints);
        Assert.True(next.EndTime > old.EndTime);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(expected, saved.Set<PilotSkill>().Find(Victim, 3327)!.Points);
        Assert.Single(saved.Set<SavedBooster>());
        Assert.NotNull(saved.Set<SavedPilotImplant>().Find(StoredImplant));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCapsuleSettlementRollsBackBodyTrainingHealthPaymentAndNotifications(bool resident)
    {
        using var f = Prepare(resident);
        AddBodies(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(Victim, 3327)!.Points = 250;
            world.SaveChanges();
        }
        var training = f.Db.Services.GetRequiredService<IPilotTraining>();
        var queue = Assert.Single(training.Save(Victim, [new(3327, 2)], true).Queue);
        f.Clock.Seconds += 30;
        AlmostLost(f);
        var before = f.Health.Read(Victim, f.Clock.Now);
        List<PilotDamage> messages = [];
        f.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_capsule_transfer BEFORE INSERT ON WorldHomeCloneTransfer BEGIN SELECT RAISE(ABORT,'test'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => Fire(f));
        Assert.Empty(messages);
        Assert.Equal(before, f.Health.Read(Victim, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(Capsule, world.Pilots.Find(Victim)!.ActiveShipId);
            Assert.NotNull(world.Set<SavedPilotImplant>().Find(ActiveImplant));
            Assert.Equal(2, world.Set<SavedJumpClone>().Count());
            Assert.Empty(world.Set<SavedHomeCloneTransfer>());
            Assert.Empty(world.Set<SavedCombatReport>());
            Assert.Empty(world.Set<ShipLoss>());
            Assert.Empty(world.Set<ModuleCooldown>());
            Assert.Empty(world.Set<SavedCrimewatch>());
            var savedQueue = Assert.Single(world.Set<QueuedLevel>());
            Assert.Equal(queue.EndTime, savedQueue.EndTime);
            Assert.Equal(queue.StartPoints, world.Set<PilotSkill>().Find(Victim, 3327)!.Points);
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_capsule_transfer");
        }
        Assert.True(Fire(f).Destroyed);
        Assert.Single(messages);
    }

    [Fact]
    public async Task ConcurrentFatalHitsCannotDuplicateARespawnOrKillReport()
    {
        using var f = Prepare(resident: true);
        AddBodies(f);
        AlmostLost(f);
        List<PilotDamage> messages = [];
        f.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        var outcomes = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return Fire(f).Destroyed;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(outcomes, destroyed => destroyed);
        Assert.Single(messages);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<ShipLoss>());
        Assert.Single(saved.Set<SavedHomeCloneTransfer>());
        Assert.Equal(2, saved.Set<SavedCombatReport>().Count());
        Assert.Single(saved.Set<SavedImplantState>());
    }

    [Fact]
    public async Task NativePodDeathPrecedesRemovalAndHomeSessionWithoutCreatingASpaceEgo()
    {
        using var f = Prepare(resident: true);
        AddBodies(f);
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Db.Store.FindPilot(1000002)!;
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        using PilotCallContext context = new(
            1000002,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, space, [], health: f.Health),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        var monitor = scope.ServiceProvider.GetRequiredService<PilotDamageMonitor>();
        monitor.Enter(context);
        var before = context.Pilot;
        AlmostLost(f);
        Fire(f);
        scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().ApplyPlayerDamage(context);
        Assert.Null(context.Pilot.Position);
        Assert.False(context.Flight.InBallpark);
        Assert.Null(space.FindOnline(SystemId, Capsule));
        Assert.Null(space.FindOnline(context.Pilot.SystemId, context.Pilot.ShipId));
        Assert.DoesNotContain(context.Flight.Updates, u => u.Command is DestinyCommand.AddBalls);
        Assert.Contains(context.Flight.Updates, u => u.Command is DestinyCommand.RemoveBall { Id: Capsule, Explode: true });
        Assert.Contains(context.Notifications, n => n is ServerImplantsChanged);
        Assert.Contains(context.Notifications, n => n is JumpCloneCacheInvalidated);
        List<object?> packets = [];
        PilotSessionOutput output = new(
            context,
            1000002,
            42,
            packet =>
            {
                packets.Add(packet);
                return Task.CompletedTask;
            },
            _ => { }
        );
        await output.AnnounceTransitionAsync(before);
        string Kind(object? packet)
        {
            var model = Assert.IsType<BlueInstance>(packet);
            if (model.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal))
            {
                return "session";
            }
            var fields = BlueRead.Tuple(model.State, 14);
            return BlueMarshal.Text(BlueRead.Tuple(Assert.IsType<BlueInstance>(fields[2]).State, 4)[1]);
        }
        var kinds = packets.Select(Kind).ToList();
        Assert.True(kinds.IndexOf("OnDamageMessage") < kinds.IndexOf("OnPlayerPodDeath"));
        Assert.True(kinds.IndexOf("OnPlayerPodDeath") < kinds.IndexOf("DoDestinyUpdate"));
        Assert.True(kinds.IndexOf("DoDestinyUpdate") < kinds.IndexOf("session"));
        var session = Assert.IsType<BlueInstance>(packets[kinds.IndexOf("session")]);
        var body = BlueRead.Tuple(BlueRead.Tuple(session.State, 14)[4], 3);
        var changes = Assert.IsType<Dictionary<string, object?>>(BlueRead.Tuple(body[1], 2)[1]);
        Assert.Equal(new object?[] { null, context.Pilot.StationId }, changes["stationid"]);
        Assert.Equal(new object?[] { SystemId, null }, changes["solarsystemid"]);
        Assert.Equal(new object?[] { Capsule, context.Pilot.ShipId }, changes["shipid"]);
        Assert.All(packets, packet => Assert.NotEmpty(BlueMarshal.Encode(packet)));
    }

    [Theory]
    [InlineData(FleetDefinitions.CapsuleType)]
    [InlineData(CapsuleVariant)]
    public void EveryCapsuleGroupVariantUsesBodyLossAndAStandardHomeCapsule(int type)
    {
        using var f = Prepare();
        if (type != FleetDefinitions.CapsuleType)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSql($"INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES ({type},'Test capsule',29,32000,2)");
            catalog.Database.ExecuteSql(
                $"INSERT INTO TypeAttribute SELECT {type},AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=670"
            );
            string data = "{\"published\":1,\"volume\":1000}";
            catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES ('client/types',{type.ToString()},{data})");
            using WorldDbContext world = new(f.Db.Path);
            world.Ships.Find(Capsule)!.TypeId = type;
            world.SaveChanges();
        }
        AlmostLost(f);
        Assert.True(Fire(f).Destroyed);
        var replacement = f.Db.Store.FindPilot(1000002)!;
        Assert.Equal(FleetDefinitions.CapsuleType, replacement.ShipTypeId);
        Assert.Null(replacement.Position);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(type, Assert.Single(saved.Set<ShipLoss>()).TypeId);
        Assert.Single(saved.Set<SavedHomeCloneTransfer>());
        Assert.Empty(saved.Set<WorldWreck>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflineCapsuleLossRemovesTimedPresenceInsteadOfPublishingTheDockedReplacement(bool resident)
    {
        using var f = Prepare(resident);
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        crimewatch.Disconnect(Victim, Capsule);
        Assert.Single(crimewatch.LoggedOff(SystemId));
        AlmostLost(f);
        Assert.True(Fire(f).Destroyed);
        Assert.Empty(crimewatch.LoggedOff(SystemId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<SavedLogoff>().Find(Victim));
        Assert.Null(f.Db.Store.FindPilot(1000002)!.Position);
    }

    [Fact]
    public void InvalidSavedHomeFallsBackWithoutChangingHomeSettingsOrOtherAssets()
    {
        using var f = Prepare();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedHomeStation>().Find(Victim)!.StationId = 1;
            world.SaveChanges();
        }
        var home = f.Db.Services.GetRequiredService<IPilotHomeStation>().Read(Victim);
        Assert.True(home.IsFallback);
        AlmostLost(f);
        Fire(f);
        Assert.Equal(home.Id, f.Db.Store.FindPilot(1000002)!.StationId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(1, saved.Set<SavedHomeStation>().Find(Victim)!.StationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingHomeAndFallbackRollBackTheFatalHitInsteadOfLosingThePilot(bool resident)
    {
        using var f = Prepare(resident);
        AddBodies(f);
        AlmostLost(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedHomeStation>().Find(Victim)!.StationId = 1;
            world.Pilots.Find(Victim)!.SchoolId = -1;
            world.Ships.Find(Capsule)!.StationId = 1;
            world.SaveChanges();
        }
        var before = f.Health.Read(Victim, f.Clock.Now);
        var error = Assert.Throws<InvalidDataException>(() => Fire(f));
        Assert.Contains("home station", error.Message);
        Assert.Equal(before, f.Health.Read(Victim, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(Capsule, saved.Pilots.Find(Victim)!.ActiveShipId);
        Assert.NotNull(saved.Set<SavedPilotImplant>().Find(ActiveImplant));
        Assert.Empty(saved.Set<ShipLoss>());
        Assert.Empty(saved.Set<SavedHomeCloneTransfer>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<SavedCrimewatch>());
    }
}
