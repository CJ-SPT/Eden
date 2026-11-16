using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
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
