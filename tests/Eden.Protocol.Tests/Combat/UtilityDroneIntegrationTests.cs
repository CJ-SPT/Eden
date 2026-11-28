using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class UtilityDroneIntegrationTests
{
    private const long Victim = 90000002;
    private const long VictimShip = 1000000000002;
    private const int System = 30000144;
    private const int SalvageDrone = 32787;
    private const int WreckType = 26557;

    internal static void Space(Fixture f)
    {
        f.Space();
        f.Base.Db.Store.SetPosition(VictimShip, f.Npc.Position + new SpacePosition(1100, 0, 0), System);
        var space = f.Base.Db.Services.GetRequiredService<SharedSpace>();
        foreach (long account in new long[] { 1000001, 1000002 })
        {
            var pilot = f.Base.Db.Store.FindPilot(account)!;
            space.Publish(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
        }
    }

    [Theory]
    [InlineData(23719, 0)]
    [InlineData(28203, 0)]
    [InlineData(23711, 1)]
    [InlineData(28201, 1)]
    [InlineData(33706, 2)]
    [InlineData(33712, 2)]
    public void RepairDronesRepairTheirLayerWithoutDamageOrHostileAggression(int type, int layer)
    {
        using Fixture f = UtilityDroneTests.Prepare();
        long stack = f.Bay(type, 1);
        Space(f);
        var health = f.Base.Db.Services.GetRequiredService<IPilotHealth>();
        var basis = health.Read(Victim, f.Now);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = VictimShip,
                    ShieldCharge = basis.Defense.Shield / 2,
                    ArmorDamage = basis.Defense.Armor / 2,
                    StructureDamage = basis.Defense.Structure / 2,
                    UpdatedAt = f.Now,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Engage, VictimShip, new HashSet<long> { VictimShip }, f.Now);
        var before = health.Read(Victim, f.Now);
        Assert.Empty(f.Tick().Hits);
        var initial = health.Read(Victim, f.Now);
        if (layer == 0)
        {
            Assert.True(initial.Remaining.Shield > before.Remaining.Shield);
        }
        else
        {
            Assert.Equal(before.Remaining.Armor, initial.Remaining.Armor);
            Assert.Equal(before.Remaining.Structure, initial.Remaining.Structure);
        }
        Assert.Empty(f.Tick(5).Hits);
        var after = health.Read(Victim, f.Now);
        Assert.True(
            layer switch
            {
                0 => after.Remaining.Shield > before.Remaining.Shield,
                1 => after.Remaining.Armor > before.Remaining.Armor,
                _ => after.Remaining.Structure > before.Remaining.Structure,
            }
        );
        Assert.Empty(live.Economy.Inspect(w => w.Set<SavedPilotHostility>().ToArray()));
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.True(saved.Set<ShipHealthState>().Find(VictimShip)!.StructureDamage <= basis.Defense.Structure / 2);
    }

    [Theory]
    [InlineData(23659, UtilityDroneKind.Neutralizing)]
    [InlineData(23707, UtilityDroneKind.Jamming)]
    [InlineData(23715, UtilityDroneKind.Dampening)]
    [InlineData(23727, UtilityDroneKind.TrackingDisruption)]
    [InlineData(23723, UtilityDroneKind.Painting)]
    [InlineData(23731, UtilityDroneKind.Webifying)]
    [InlineData(92033, UtilityDroneKind.Webifying)]
    [InlineData(92036, UtilityDroneKind.Neutralizing)]
    public void OffensiveDronesApplyRealEffectsAndStopOnRecall(int type, UtilityDroneKind kind)
    {
        using Fixture f = UtilityDroneTests.Prepare();
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144','{{"securityStatus":0}}')"""
            );
            foreach (
                var sensor in new[]
                {
                    DogmaAttributeId.RadarStrength,
                    DogmaAttributeId.LadarStrength,
                    DogmaAttributeId.MagnetometricStrength,
                    DogmaAttributeId.GravimetricStrength,
                }
            )
            {
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)sensor},0.01)");
            }
        }
        long stack = f.Bay(type, 1);
        Space(f);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Engage, VictimShip, new HashSet<long> { VictimShip }, f.Now);
        var update = f.Tick();
        if (kind == UtilityDroneKind.Neutralizing)
        {
            Assert.True(
                live.Economy.Inspect(w => w.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(VictimShip)!.Charge)
                    < live.Economy.Inspect(w =>
                    {
                        using CatalogDbContext catalog = new(f.Base.CatalogPath);
                        return PilotFittingStore.ShipAttributes(w, catalog, Victim)[DogmaAttributeId.CapacitorCapacity];
                    })
            );
        }
        else if (kind == UtilityDroneKind.Webifying)
        {
            var effect = Assert.Single(f.Base.Db.Services.GetRequiredService<IPilotTackle>().Incoming(VictimShip));
            Assert.True(effect.Module.WebPercent < 0);
        }
        else
        {
            var effect = Assert.Single(f.Base.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(VictimShip));
            Assert.NotNull(effect.Applied);
            if (kind == UtilityDroneKind.Jamming)
            {
                Assert.False(PlayerEwarRules.CanTarget([effect], f.Ship));
                Assert.True(PlayerEwarRules.CanTarget([effect], drone));
            }
        }
        Assert.Equal(type >= 92000, update.Hits.Count > 0);
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Orbit, null, new HashSet<long>(), f.Now);
        Assert.Empty(f.Base.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(VictimShip));
        Assert.Empty(f.Base.Db.Services.GetRequiredService<IPilotTackle>().Incoming(VictimShip));
    }

    [Fact]
    public void AutomaticSalvageKeepsItsCycleOnTheAssignedWreckUntilItDisappears()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        PassiveCatalogFixture.LoadInformation(f.Base.CatalogPath);
        long stack = f.Bay(SalvageDrone, 1);
        f.Space();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        const long first = 8_000_000_000_201;
        const long second = 8_000_000_000_202;
        AddWreck(f, live, first, f.Base.Db.Store.FindPilot(1000001)!.Position!);
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Salvage, null, new HashSet<long>(), f.Now);
        f.Tick();
        var running = Cycle();
        Assert.Equal(first, running.TargetId);
        Assert.True(running.StartedAt > 0);
        long secondNpc = f.Base.Db.Services.GetRequiredService<INpcEncounters>().List(System, f.Now).First(npc => npc.Id != f.Npc.Id).Id;
        AddWreck(f, live, second, Assert.Single(f.Drones.List(System, f.Now)).Position, secondNpc);

        f.Tick();

        Assert.Equal(running, Cycle());
        using (var action = live.Economy.Begin())
        {
            action.Set<WorldWreck>().Find(first)!.SalvagedAt = f.Now;
            action.Commit();
        }
        f.Tick();
        var replacement = Cycle();
        Assert.Equal(second, replacement.TargetId);
        Assert.Equal(f.Now, replacement.StartedAt);
        Assert.True(replacement.DueAt > running.DueAt);

        DroneWork Cycle() => live.Economy.Inspect(world => PilotDroneStore.ReadWork(world.Set<WorldDrone>().Find(drone)!));
    }

    [Fact]
    public void SalvageDroneStartsWorkingAtTheResidentTractorPositionBeforeItIsCheckpointed()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        PassiveCatalogFixture.LoadInformation(f.Base.CatalogPath);
        long stack = f.Bay(SalvageDrone, 1);
        f.Space();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        const long wreck = 8_000_000_000_203;
        var source = f.Base.Db.Store.FindPilot(1000001)!.Position!;
        var original = source + new SpacePosition(15_000, 0, 0);
        AddWreck(f, live, wreck, original);
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        InformationModule tractor = new(1, 24348, DogmaEffect.TractorBeam, 10_000, 0, 20_000, 0, 2_000, 0);
        live.Tractors.Start(
            new(f.Pilot, f.Ship, wreck, tractor, f.Now, f.Now + TimeSpan.TicksPerSecond * 10),
            source,
            original,
            0,
            null,
            System
        );
        f.Base.Clock.Seconds += 10;
        live.Tractors.Advance(f.Now);
        var pulled = Assert.IsType<SpacePosition>(live.PublishedPosition(System, wreck));
        Assert.Equal(InformationRules.TractorStopDistance, pulled.DistanceTo(source), 3);
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Salvage, wreck, new HashSet<long> { wreck }, f.Now);
        Assert.Equal(original.X, live.Economy.Inspect(world => world.Set<WorldWreck>().Find(wreck)!.X));

        f.Tick();

        var work = live.Economy.Inspect(world => PilotDroneStore.ReadWork(world.Set<WorldDrone>().Find(drone)!));
        Assert.Equal(wreck, work.TargetId);
        Assert.Equal(f.Now, work.StartedAt);
    }

    [Fact]
    public void RepairDroneCancelsCleanlyWhenTheTargetDroneOwnerBecomesCriminalBeforeFirstCycle()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144','{{"securityStatus":0.7}}')"""
            );
        }
        long stack = f.Bay(23719, 1);
        Space(f);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long repaired;
        const double damagedShield = 10;
        using (var action = live.Economy.Begin())
        {
            action.Pilots.Find(f.Pilot)!.SafetyLevel = ShipSafetyLevel.Full;
            repaired = action.NextItemId();
            action.Add(
                new OwnedItem
                {
                    Id = repaired,
                    OwnerId = Victim,
                    LocationId = System,
                    TypeId = 2454,
                    Quantity = 1,
                    Singleton = true,
                }
            );
            var position = action.Positions.Find(VictimShip)!;
            action.Add(
                new WorldDrone
                {
                    ItemId = repaired,
                    PilotId = Victim,
                    ControllerShipId = VictimShip,
                    SystemId = System,
                    Shield = damagedShield,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    UpdatedAt = f.Now,
                }
            );
            action.Commit();
        }
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Engage, repaired, new HashSet<long> { repaired }, f.Now);
        using (var action = live.Economy.Begin())
        {
            action.Add(
                new SavedCrimewatch
                {
                    PilotId = Victim,
                    SystemId = System,
                    CriminalUntil = f.Now + TimeSpan.TicksPerMinute,
                }
            );
            action.Commit();
        }

        Assert.Empty(f.Tick().Hits);

        live.Economy.Inspect(world =>
        {
            Assert.Equal(DroneActivity.Idle, world.Set<WorldDrone>().Find(drone)!.Activity);
            Assert.Equal(damagedShield, world.Set<WorldDrone>().Find(repaired)!.Shield);
            Assert.Null(world.Set<SavedCrimewatch>().Find(f.Pilot));
            return true;
        });
    }

    private static void AddWreck(Fixture f, LiveWorld live, long id, SpacePosition point, long? npcId = null)
    {
        using var action = live.Economy.Begin();
        action.Add(
            new WorldWreck
            {
                Id = id,
                NpcId = npcId ?? f.Npc.Id,
                TypeId = WreckType,
                OwnerId = f.Pilot,
                CorporationId = 1000009,
                SystemId = System,
                Name = "Test wreck",
                X = point.X,
                Y = point.Y,
                Z = point.Z,
                CreatedAt = f.Now,
                ExpiresAt = f.Now + TimeSpan.TicksPerHour,
                SalvageJson = JsonSerializer.Serialize(new ScanItem[] { new(34, 2) }),
            }
        );
        action.Commit();
    }

    [Fact]
    public void SalvageDronesReturnMaterialAndPreserveLootWithoutReplayingTheWreck()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        PassiveCatalogFixture.LoadInformation(f.Base.CatalogPath);
        const int wreckType = 26557;
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES ({wreckType},{(int)DogmaAttributeId.AccessDifficulty},100)"
            );
        }
        long stack = f.Bay(32787, 1);
        f.Space();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long wreck = 8000000000200;
        using (var transaction = live.Economy.Begin())
        {
            var position = transaction.Pilots.Find(f.Pilot)!.ActiveShip.Position!;
            transaction.Add(
                new WorldWreck
                {
                    Id = wreck,
                    NpcId = f.Npc.Id,
                    TypeId = wreckType,
                    OwnerId = f.Pilot,
                    CorporationId = 1000009,
                    SystemId = System,
                    Name = "Test wreck",
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    CreatedAt = f.Now,
                    ExpiresAt = f.Now + TimeSpan.TicksPerHour,
                    SalvageJson = JsonSerializer.Serialize(new ScanItem[] { new(34, 2) }),
                }
            );
            transaction.Commit();
        }
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        using var scope = f.Base.Db.Services.CreateScope();
        using var context = DroneSynchronizationTests.Context(f, scope.ServiceProvider, 1000001);
        context.Flight.Snapshot();
        var command = scope
            .ServiceProvider.GetRequiredService<DroneHandler>()
            .Call(context, "entity", "CmdSalvage", [new List<object?> { drone }, null]);
        var reply = Assert.IsType<DroneCommandReply>(Assert.IsType<PilotCallResult.Success>(command).Reply);
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IDictionary>(BlueMarshal.Decode(BlueMarshal.Encode(reply))));
        Assert.Empty(f.Tick().Hits);
        context.Flight.RefreshDrones();
        Assert.Contains(
            context.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.Salvaging", Start: true }
        );
        Assert.Empty(f.Tick(10).Changes);
        var delivered = Assert.Single(f.Tick().Changes);
        Assert.Equal((34, 2), (delivered.Item.TypeId, delivered.Item.Quantity));
        Assert.Empty(f.Tick(20).Changes);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.NotNull(saved.Set<WorldWreck>().Find(wreck)!.SalvagedAt);
    }
}
