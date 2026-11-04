using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Loot;
using Eden.Protocol.Market;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Combat;

public sealed class PilotCombatTests
{
    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public Clock Clock { get; } = new();
        public IPilotCombat Combat => Db.Services.GetRequiredService<IPilotCombat>();
        public IPilotFitting Fitting => Db.Services.GetRequiredService<IPilotFitting>();
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        public INpcEncounters Encounters => Db.Services.GetRequiredService<INpcEncounters>();
        public NpcContact Npc { get; }
        public long Module { get; }

        public Fixture()
        {
            NpcEncounterTests.Prepare(Db);
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (6,'capacitorNeed'),(14,'hiSlots'),(48,'cpuOutput'),
                        (11,'powerOutput'),
                        (50,'cpu'),(30,'power'),(51,'speed'),(54,'maxRange'),(64,'damageMultiplier'),(114,'emDamage'),
                            (116,'explosiveDamage'),
                        (117,'kineticDamage'),(118,'thermalDamage'),(127,'ammoLoaded'),(158,'falloff'),(160,'trackingSpeed'),
                            (620,'optimalSigRadius'),
                        (183,'requiredSkill2'),(278,'requiredSkill2Level'),(273,'shieldKineticDamageResonance'),
                            (274,'shieldThermalDamageResonance'),(481,'entityKillBounty');
                    INSERT INTO TypeAttribute VALUES (601,14,2),(601,48,135),(601,11,23);
                    INSERT INTO TypeAttribute VALUES (3638,6,1),(3638,9,40),(3638,30,6),(3638,50,4),(3638,51,2000),(3638,54,4800),
                        (3638,64,1.25),(3638,114,0),(3638,116,0),(3638,117,3),(3638,118,2),(3638,127,20132),(3638,158,2000),
                        (3638,160,500),(3638,620,40000),(3638,183,3300),(3638,278,1);
                    INSERT INTO TypeAttribute VALUES (30380,273,1),(30380,274,0.9),(30380,481,3000);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (3300,'Gunnery',257);
                    INSERT INTO TypeAttribute VALUES (3300,275,1);
                    """
                );
            }
            using (WorldDbContext world = new(Db.Path))
            {
                world.Database.ExecuteSqlRaw("INSERT INTO WorldPilotSkill VALUES (90000001,3300,250),(90000002,3300,250)");
            }
            Npc = Encounters.List(30000144, Clock.Now)[0];
            Module = Fit(1000001);
        }

        public long Fit(long account)
        {
            var pilot = Db.Store.FindPilot(account)!;
            var gun = Inventory.List(pilot.PilotId, pilot.StationId).Single(i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun);
            var module = Assert.Single(Fitting.Fit(pilot.PilotId, [gun.Id], pilot.StationId, (int)InventoryFlag.AutoFit)).Item;
            Fitting.SetOnline(pilot.PilotId, pilot.ShipId, module.Id, true);
            Db.Store.SetPosition(pilot.ShipId, Npc.Position + new SpacePosition(1000, 0, 0), Npc.SystemId);
            return module.Id;
        }

        public CombatShot Fire(double roll = 0.5, long? target = null) =>
            Combat.Fire(90000001, Module, target ?? Npc.Id, SpacePosition.Zero, Clock.Now, roll);

        public void AlmostDefeated()
        {
            using WorldDbContext world = new(Db.Path);
            var npc = world.Set<WorldNpc>().Find(Npc.Id)!;
            npc.ShieldCharge = 0;
            npc.ArmorDamage = 15;
            npc.StructureDamage = 19;
            npc.UpdatedAt = Clock.Now;
            world.SaveChanges();
        }

        public (PilotCallContext Context, TargetingHandler Targeting, CombatHandler Combat) Client()
        {
            var pilot = Db.Store.FindPilot(1000001)!;
            PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, Clock, new(), [], encounters: Encounters, wrecks: Db.Services.GetRequiredService<IWrecks>()),
                10,
                SignedSettings.Empty
            );
            context.Select(pilot);
            context.Flight.Snapshot();
            TargetingHandler targeting = new(Clock, Fitting, new());
            Assert.IsType<object?[]>(targeting.CallWire(context, "dogmaIM", "AddTarget", [Npc.Id]));
            Clock.Milliseconds += 5000;
            targeting.Advance(context);
            Assert.True(targeting.IsLocked(Npc.Id));
            context.Notifications.Clear();
            context.Flight.Updates.Clear();
            return (context, targeting, new(Clock, Combat, targeting));
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void AutomaticRepeatsKeepHudEffectActiveUntilARealStop()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=100000 WHERE ItemTypeId=30380 AND AttributeId=263");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge = 100000;
            world.SaveChanges();
        }
        var (context, targeting, combat) = f.Client();
        using var owned = context;
        using var locks = targeting;
        Assert.Null(combat.CallWire(context, "dogmaIM", "Activate", [f.Module, "targetAttack", f.Npc.Id, 100L]));
        Assert.True(Assert.Single(context.Notifications.OfType<ShipEffectChanged>()).Start);
        context.Notifications.Clear();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            f.Clock.Milliseconds += 3000;
            combat.Advance(context);
            Assert.NotNull(combat.ActiveEffect(context.Pilot, f.Module));
            Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        }
        Assert.Null(combat.CallWire(context, "dogmaIM", "Deactivate", [f.Module, "targetAttack"]));
        f.Clock.Milliseconds += 3000;
        combat.Advance(context);
        Assert.Null(combat.ActiveEffect(context.Pilot, f.Module));
        Assert.False(Assert.Single(context.Notifications.OfType<ShipEffectChanged>()).Start);
    }

    [Fact]
    public void FirstShotSpendsCapacitorImmediatelyAndCooldownSurvivesReconnect()
    {
        using Fixture f = new();
        var shot = f.Fire();
        Assert.Equal(5.94, shot.Applied, 8);
        Assert.Equal(14.06, shot.Health.Remaining.Shield, 8);
        Assert.Equal(1960, shot.Weapon.CycleMilliseconds);
        Assert.Equal(20132, shot.Weapon.ChargeTypeId);
        Assert.Equal(129, shot.Capacitor);
        Assert.False(shot.Destroyed);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => f.Fire());
        Assert.Equal(0, TaskTrackingTestData.Count(f.Db, TaskEvents.NpcDestroyed));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(129, world.Set<ShipEnergy>().Single().Charge);
        Assert.Equal(14.06, world.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge, 8);
        Assert.Empty(world.Set<NpcBounty>());
        Assert.Equal(f.Clock.Now + 1960 * TimeSpan.TicksPerMillisecond, world.Set<ModuleCooldown>().Single().ReadyAt);
        f.Clock.Milliseconds += 1960;
        Assert.True(f.Fire().Applied > 0);
    }

    [Fact]
    public void MissesStillConsumeCapacitorAndRangeAndTrackingAffectAccuracy()
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(pilot.ShipId, f.Npc.Position + new SpacePosition(9000, 0, 0), f.Npc.SystemId);
        var miss = f.Fire(0.5);
        Assert.Equal(0, miss.Applied);
        Assert.Equal(20, miss.Health.Remaining.Shield);
        Assert.Equal(129, miss.Capacitor);
        Assert.Equal(0, CombatDamage.AngularVelocity(new(1000, 0, 0), new(500, 0, 0)));
        Assert.Equal(0.5, CombatDamage.AngularVelocity(new(1000, 0, 0), new(0, 500, 0)));
        Assert.Equal(0, CombatDamage.AngularVelocity(SpacePosition.Zero, new(0, 500, 0)));
    }

    [Fact]
    public void FailedWalletCreditRollsBackDamageCapacitorAndCooldownTogether()
    {
        using Fixture f = new();
        f.AlmostDefeated();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new PilotWallet { PilotId = 90000001, Cents = long.MaxValue });
            world.SaveChanges();
        }
        Assert.Throws<OverflowException>(() => f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<WorldNpc>().Find(f.Npc.Id)!.DestroyedAt);
        Assert.Equal(19, saved.Set<WorldNpc>().Find(f.Npc.Id)!.StructureDamage);
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.DoesNotContain(saved.Set<OwnedItem>(), i => i.TypeId == WreckDefinitions.StarterLootType);
        Assert.Equal(long.MaxValue, saved.Set<PilotWallet>().Single().Cents);
    }

    [Fact]
    public async Task CompetingLastShotsAwardExactlyOneDurableBounty()
    {
        using Fixture f = new();
        long wingGun = f.Fit(1000002);
        f.AlmostDefeated();
        var results = await Task.WhenAll(
            new[] { (Pilot: 90000001L, Module: f.Module), (Pilot: 90000002L, Module: wingGun) }.Select(pair =>
                Task.Run(() =>
                {
                    try
                    {
                        return f.Combat.Fire(pair.Pilot, pair.Module, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
                    }
                    catch (InvalidDataException)
                    {
                        return null;
                    }
                })
            )
        );
        var kill = Assert.Single(results.OfType<CombatShot>());
        Assert.True(kill.Destroyed);
        Assert.Equal(300000, kill.Reward!.AmountCents);
        Assert.Equal(1, TaskTrackingTestData.Count(f.Db, TaskEvents.NpcDestroyed, kill.Reward.PilotId));
        Assert.Equal(0, TaskTrackingTestData.Count(f.Db, TaskEvents.NpcDestroyed, kill.Reward.PilotId == 90000001 ? 90000002 : 90000001));
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<NpcBounty>());
        var wreck = Assert.Single(saved.Set<WorldWreck>());
        Assert.Equal(kill.Reward.PilotId, wreck.OwnerId);
        Assert.Equal(100, Assert.Single(saved.Set<OwnedItem>().Where(i => i.LocationId == wreck.Id)).Quantity);
        Assert.Equal(300000, saved.Set<PilotWallet>().Sum(w => w.Cents));
        Assert.Single(saved.Set<ModuleCooldown>());
        Assert.Single(saved.Set<ShipEnergy>());
        Assert.Throws<InvalidDataException>(() => f.Fire());
    }

    [Fact]
    public void ActivationIsIdempotentAndManualStopDoesNotFireAnotherCycle()
    {
        using Fixture f = new();
        var (context, targeting, combat) = f.Client();
        using var owned = context;
        using var locks = targeting;
        using var activationScope = f.Db.Services.CreateScope();
        ModuleActivationHandler activation = new(
            new(f.Clock, f.Db.Services.GetRequiredService<IPilotMining>(), targeting),
            combat,
            new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Repair.IPilotShieldBoosting>()),
            activationScope.ServiceProvider.GetRequiredService<FittingOperations>(),
            new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotPropulsion>()),
            new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Repair.IPilotLocalRepairing>()),
            new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Repair.IPilotHardening>()),
            new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotCapacitorBoosting>())
        );
        object? Activate() => activation.CallWire(context, "dogmaIM", "Activate", [f.Module, "targetAttack", f.Npc.Id, 100L]);
        Assert.Null(Activate());
        using WorldDbContext saved = new(f.Db.Path);
        double firstDamage = saved.Set<WorldNpc>().AsNoTracking().Single(n => n.Id == f.Npc.Id).ShieldCharge;
        Assert.True(firstDamage < 20);
        Assert.Null(Activate());
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.Single(context.NotificationsBeforeFlight).Arguments));
        Assert.Equal("OnDamageMessage", context.NotificationsBeforeFlight[0].Name);
        Assert.Equal(f.Npc.Id, message["target"]);
        Assert.Equal((int)CombatWeaponType.CivilianGatlingRailgun, message["weapon"]);
        Assert.Equal("me", message["attackType"]);
        Assert.Equal(false, message["isBanked"]);
        Assert.InRange(Assert.IsType<int>(message["hitQuality"]), (int)CombatHitQuality.Grazing, (int)CombatHitQuality.Wrecking);
        Assert.Equal(20 - firstDamage, Assert.IsType<double>(message["damage"]), 8);
        Assert.NotEmpty(BlueMarshal.Encode(message));
        Assert.Equal(firstDamage, saved.Set<WorldNpc>().AsNoTracking().Single(n => n.Id == f.Npc.Id).ShieldCharge);
        Assert.NotNull(combat.ActiveEffect(context.Pilot, f.Module));
        Assert.Null(activation.CallWire(context, "dogmaIM", "Deactivate", [f.Module, "targetAttack"]));
        f.Clock.Milliseconds += 2000;
        combat.Advance(context);
        Assert.Null(combat.ActiveEffect(context.Pilot, f.Module));
        Assert.Single(context.NotificationsBeforeFlight);
        Assert.Equal(firstDamage, saved.Set<WorldNpc>().AsNoTracking().Single(n => n.Id == f.Npc.Id).ShieldCharge);
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
        Assert.Contains(context.Flight.WireUpdates(), update => Equals(((object?[])((object?[])update!)[1]!)[0], "OnDamageStateChange"));
        Assert.NotEmpty(BlueMarshal.Encode(context.Flight.WireUpdates()));
    }

    [Fact]
    public void KillUsesNativeTerminalRemovalAndWalletJournalAndRespawnsWithANewIdentity()
    {
        using Fixture f = new();
        var (context, targeting, combat) = f.Client();
        using var owned = context;
        using var locks = targeting;
        f.AlmostDefeated();
        Assert.Null(combat.CallWire(context, "dogmaIM", "Activate", [f.Module, "targetAttack", f.Npc.Id, 100L]));
        Assert.Null(combat.ActiveEffect(context.Pilot, f.Module));
        var feedback = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.Single(context.NotificationsBeforeFlight).Arguments)
        );
        Assert.Equal(f.Npc.Id, feedback["target"]);
        Assert.True(Assert.IsType<double>(feedback["damage"]) > 0);
        var actions = context.Flight.Updates.Select(update => update.Command).ToList();
        var removal = Assert.Single(actions.OfType<DestinyCommand.RemoveBall>());
        Assert.True(actions.FindIndex(action => action is DestinyCommand.AddBalls) < actions.IndexOf(removal));
        Assert.Single(context.Flight.VisibleWrecks);
        Assert.Equal(f.Npc.Id, removal.Id);
        Assert.True(removal.Explode);
        Assert.Contains(context.Notifications, n => n.Name == "OnAccountChange" && Equals(n.Arguments[2], 3000d));
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var journal = Assert.IsType<List<object?>>(
            router.CallWire(context, "account", "GetTransactions", new object?[] { 1000L, null, null, false }, _ => { })
        );
        var entry = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(Assert.Single(journal)).State);
        Assert.Equal(85, entry["entryTypeID"]);
        Assert.Equal(30000144L, entry["referenceID"]);
        Assert.Equal(3000d, entry["amount"]);
        Assert.NotEmpty(BlueMarshal.Encode(journal));
        Assert.DoesNotContain(f.Encounters.List(30000144, f.Clock.Now + TimeSpan.TicksPerSecond * 599), n => n.Id == f.Npc.Id);
        var respawned = f.Encounters.List(30000144, f.Clock.Now + TimeSpan.TicksPerSecond * 600);
        Assert.Equal(2, respawned.Count);
        Assert.DoesNotContain(respawned, n => n.Id == f.Npc.Id);
        Assert.True(respawned.Max(n => n.Id) > f.Npc.Id);
        Assert.All(respawned, n => Assert.Equal(n.Health.Defense.Undamaged, n.Health.Remaining));
        Assert.Single(f.Combat.Rewards(90000001));
    }

    [Fact]
    public async Task FatalShotSendsCombatTextBeforeRemovingTheTargetsClientRecord()
    {
        using Fixture f = new();
        f.AlmostDefeated();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES
                    ('client/asteroidcountbytypeidbeltid','40009232','{{}}'),
                    ('client/asteroidcountbytypeidbeltid','40009245','{{}}');
                """
            );
        }
        const string address = "carbon.common.script.net.machoNetAddress.MachoAddress";
        BlueInstance Request(string service, string method, params object?[] args) =>
            new(
                "carbon.common.script.net.machoNetPacket.CallReq",
                new object?[]
                {
                    6L,
                    new BlueInstance(address, new object?[] { 2L, 0L, 1L, null }),
                    new BlueInstance(address, new object?[] { 8L, service, null }),
                    null,
                    new object?[]
                    {
                        new object?[] { 0L, BlueMarshal.Encode(new object?[] { null, method, args, new Dictionary<string, object?>() }) },
                    },
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                }
            );
        Queue<BlueInstance> input = new([
            Request("charUnboundMgr", "SelectCharacterID", 90000001L),
            Request("beyonce", "MachoBindObject", (long)f.Npc.SystemId, null),
            Request("dogmaIM", "AddTarget", f.Npc.Id),
            Request("dogmaIM", "Activate", f.Module, "targetAttack", f.Npc.Id, 0L),
        ]);
        List<(string Name, object?[] Args)> notifications = [];
        using var scope = f.Db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                async () =>
                {
                    // The real session clock must complete the native NPC target lock.
                    if (input.Count == 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }
                    return input.TryDequeue(out var request) ? request : throw new EndOfStreamException();
                },
                packet =>
                {
                    var value = Assert.IsType<BlueInstance>(packet);
                    if (!value.Name.EndsWith(".Notification", StringComparison.Ordinal))
                    {
                        return Task.CompletedTask;
                    }
                    var fields = BlueRead.Tuple(value.State, 14);
                    string name = BlueMarshal.Text(BlueRead.Tuple(Assert.IsType<BlueInstance>(fields[2]).State, 4)[1]);
                    var payload = BlueRead.Tuple(BlueRead.Tuple(fields[4], 1)[0], 2);
                    var call = BlueRead.Tuple(BlueMarshal.Decode(BlueRead.Bytes(payload[1])), 2);
                    notifications.Add((name, Assert.IsType<object?[]>(BlueRead.Tuple(call[1], 2)[1])));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        int messageIndex = notifications.FindIndex(n => n.Name == "OnDamageMessage");
        Assert.True(messageIndex >= 0);
        int removalIndex = notifications.FindIndex(n =>
            n.Name == "DoDestinyUpdate"
            && Assert
                .IsType<List<object?>>(n.Args[0])
                .Cast<object?[]>()
                .Select(u => (object?[])u[1]!)
                .Any(action =>
                    BlueMarshal.Text(action[0]) == "RemoveBall" && ((object?[])action[1]!) is [var id, true] && Equals(id, f.Npc.Id)
                )
        );
        Assert.True(removalIndex > messageIndex, $"Damage message index {messageIndex}, terminal target removal index {removalIndex}.");
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(notifications[messageIndex].Args));
        Assert.Equal(f.Npc.Id, message["target"]);
        Assert.True(Assert.IsType<double>(message["damage"]) > 0);
    }

    [Fact]
    public void MissAndWreckingFeedbackUseNativeQualityCodesAndDamageIcons()
    {
        using Fixture f = new();
        f.Db.Store.SetPosition(1000000000001, f.Npc.Position + new SpacePosition(9000, 0, 0), f.Npc.SystemId);
        var miss = CombatMessages.Outgoing(f.Fire(), f.Npc.Id);
        Assert.Equal(CombatHitQuality.Miss, miss.HitQuality);
        Assert.Equal(0d, miss.Damage);
        Assert.Equal(DamageVector.None, miss.DamageAttributes);
        f.Clock.Milliseconds += 2000;
        f.Db.Store.SetPosition(1000000000001, f.Npc.Position + new SpacePosition(1000, 0, 0), f.Npc.SystemId);
        var wrecking = CombatMessages.Outgoing(f.Fire(0), f.Npc.Id);
        Assert.Equal(CombatHitQuality.Wrecking, wrecking.HitQuality);
        var damageTypes = wrecking.DamageAttributes;
        Assert.Equal(11.25, damageTypes.Kinetic);
        Assert.Equal(7.5, damageTypes.Thermal);
        Assert.Equal(0d, damageTypes.Em);
    }

    [Fact]
    public void RepeatFiresOnlyWhenDueAndTargetLossStopsFurtherShots()
    {
        using Fixture f = new();
        var (context, targeting, combat) = f.Client();
        using var owned = context;
        using var locks = targeting;
        Assert.Null(combat.CallWire(context, "dogmaIM", "Activate", [f.Module, "targetAttack", f.Npc.Id, 100L]));
        using WorldDbContext saved = new(f.Db.Path);
        double Charge() => saved.Set<WorldNpc>().AsNoTracking().Single(n => n.Id == f.Npc.Id).ShieldCharge;
        double first = Charge();
        f.Clock.Milliseconds += 1959;
        combat.Advance(context);
        Assert.Equal(first, Charge());
        f.Clock.Milliseconds++;
        combat.Advance(context);
        double second = Charge();
        Assert.True(second < first);
        saved.Set<WorldNpc>().Find(f.Npc.Id)!.X += NpcEncounterDefinitions.VisibilityRange * 2;
        saved.SaveChanges();
        context.Flight.RefreshNpcs();
        targeting.Advance(context);
        combat.Advance(context);
        f.Clock.Milliseconds += 10000;
        combat.Advance(context);
        Assert.Null(combat.ActiveEffect(context.Pilot, f.Module));
        Assert.Equal(second, Charge());
    }

    [Fact]
    public void EmptyCapacitorDoesNotDamageTheTargetOrStartACooldown()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipEnergy
                {
                    ShipId = 1000000000001,
                    Charge = 0,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(20, saved.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge);
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Equal(0, saved.Set<ShipEnergy>().Single().Charge);
    }

    [Fact]
    public void TrainedGunnerySkillsModifyWeaponAttributesBeforeAccuracyAndDamage()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(90000001L, (int)CombatSkill.Gunnery)!.Points = 8000;
            foreach (var skill in Enum.GetValues<CombatSkill>().Where(s => s != CombatSkill.Gunnery))
            {
                catalog.Database.ExecuteSql($"INSERT INTO ItemType(Id,Name,GroupId) VALUES ({(int)skill},{skill.ToString()},257)");
                catalog.Database.ExecuteSql($"INSERT INTO TypeAttribute VALUES ({(int)skill},275,1)");
                world.Add(
                    new PilotSkill
                    {
                        PilotId = 90000001,
                        TypeId = (int)skill,
                        Points = 1415,
                    }
                );
            }
            world.SaveChanges();
        }
        var shot = f.Fire();
        Assert.Equal(1729.6, shot.Weapon.CycleMilliseconds, 8);
        Assert.Equal(5280, shot.Weapon.Optimal, 8);
        Assert.Equal(2200, shot.Weapon.Falloff, 8);
        Assert.Equal(550, shot.Weapon.Tracking, 8);
        Assert.Equal(6.625, shot.Weapon.Damage.Total, 8);
        Assert.Equal(0.9, shot.Weapon.CapacitorNeed, 8);
        Assert.Equal(6.625 * 0.99 * 0.96, shot.Applied, 8);
    }

    [Fact]
    public void VersionNineteenGrantsOneWeaponWithoutChangingExistingWalletHealthEnergyOrNpcs()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new PilotWallet { PilotId = 90000001, Cents = 12345 });
            world.Add(
                new ShipEnergy
                {
                    ShipId = 1000000000001,
                    Charge = 17,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.Add(
                new ShipHealthState
                {
                    ShipId = 1000000000001,
                    ShieldCharge = 10,
                    ArmorDamage = 20,
                    StructureDamage = 30,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
            world.Set<OwnedItem>().Where(i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun).ExecuteDelete();
            world.Database.ExecuteSqlRaw(
                TestWorld.DropTestingMarketSchema
                    + TestWorld.DropNpcNavigationColumns
                    + "DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP TABLE WorldNpcAggression; DROP TABLE "
                    + "WorldShipLoss; DROP TABLE WorldWeaponCooldown; DROP TABLE WorldNpcBounty; PRAGMA user_version=19"
            );
        }
        _ = WorldInitialization.Open(f.Db.Path);
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext migrated = new(f.Db.Path);
        Assert.Equal(2, migrated.Set<OwnedItem>().Count(i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun));
        Assert.Equal(2, migrated.Set<OwnedItem>().Count(i => i.TypeId == InventoryDefinitions.CivilianMinerType));
        Assert.All(
            migrated.Pilots.ToArray(),
            p =>
                Assert.Single(
                    migrated.Set<OwnedItem>().Where(i => i.OwnerId == p.Id && i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun)
                )
        );
        Assert.True(
            migrated.Set<OwnedItem>().Where(i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun).Min(i => i.Id) > f.Module
        );
        Assert.Equal(100012345, migrated.Set<PilotWallet>().Find(90000001L)!.Cents);
        Assert.Equal(17, migrated.Set<ShipEnergy>().Single().Charge);
        Assert.Equal(20, migrated.Set<ShipHealthState>().Single().ArmorDamage);
        Assert.Equal(2, migrated.Set<WorldNpc>().Count());
        Assert.Empty(migrated.Set<NpcBounty>());
        Assert.Empty(migrated.Set<ModuleCooldown>());
    }

    [Fact]
    public void RejectsForeignOfflineUntrainedDistantAndDockedShotsWithoutSpendingResources()
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var foreign = f.Inventory.List(90000002, pilot.StationId).Single(i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun);
        Assert.Throws<InvalidDataException>(() => f.Combat.Fire(pilot.PilotId, foreign.Id, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5));
        Assert.Throws<InvalidDataException>(() => f.Fire(target: f.Db.Store.FindPilot(1000002)!.ShipId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(f.Module)!.Online = false;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Fire());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(f.Module)!.Online = true;
            world.Set<PilotSkill>().Find(pilot.PilotId, (int)CombatSkill.Gunnery)!.Points = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Fire());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(pilot.PilotId, (int)CombatSkill.Gunnery)!.Points = 250;
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(pilot.ShipId, f.Npc.Position + new SpacePosition(100000, 0, 0), f.Npc.SystemId);
        Assert.Throws<InvalidDataException>(() => f.Fire());
        f.Db.Store.SetPosition(pilot.ShipId, null);
        Assert.Throws<InvalidDataException>(() => f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Equal(20, saved.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge);
    }
}
