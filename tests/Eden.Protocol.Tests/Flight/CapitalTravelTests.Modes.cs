using Eden.Data.World.Combat;
using Eden.Data.World.Structures;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Structures;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    private const int Revelation = 19720;
    private const int Strontium = 16275;
    private const int CapitalPulseLaser = 20444;
    private const int MultifrequencyXl = 17686;
    private const int Apostle = 37604;
    private const int CapitalRemoteArmor = 24569;

    [Fact]
    public async Task RevelationJumpsAndPaysForSiegeThroughCheckpointReopen()
    {
        using Fixture fixture = new(Revelation, 16274, Rapier, CapitalTravelRules.StandardGenerator, structures: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        {
            foreach (int type in new[] { 19720, 19722, 19724, 19726, 37604, 37605, 37606, 37607, 73787, 73790, 73792, 73793 })
            {
                Assert.True(
                    HullCatalog.CanAssemble(catalog, catalog.RequireItem(type), (int)InventoryCategory.Ship),
                    $"Capital hull {type} failed catalog admission."
                );
            }
        }
        fixture.CalibrateDrive();
        long siege = fixture.FitCapitalModule(CapitalModeRules.SiegeOne);
        long gun = fixture.FitCapitalModule(CapitalPulseLaser);
        long charge = fixture.PutInCapitalCargo(MultifrequencyXl, 1);
        fixture.PutInCapitalCargo(Strontium, 224);

        var first = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(first.ShipId, new(10_000_000, 0, 0), Jan);
        var second = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(second.ShipId, new(1_000_000_000, 0, 0), NullSystem);
        using Client dread = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var field = fixture.Light(emitter);
        var jump = fixture.Jump(dread, field);
        Assert.True(jump is PilotCallResult.Success, jump.ToString());
        await dread.TickAsync();
        fixture.Clock.Seconds += 3;
        await dread.TickAsync();
        Assert.Equal(NullSystem, dread.Pilot.SystemId);
        Assert.True(Quantity(fixture, dread, 16274) < 60_000);
        dread.Context.Flight.Snapshot();
        fixture.Clock.Seconds += 61;
        await dread.TickAsync();

        var ammunition = fixture.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammunition.Load(first.PilotId, first.ShipId, [gun], [charge], first.ShipId, fixture.Clock.Now);
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Null(fixture.Travel.Mode(first.ShipId));
        Assert.Equal(224, Quantity(fixture, dread, Strontium));
        double normalMultiplier = Multiplier();
        long structure = StructureRules.FirstStructureId + 100L;
        using (var action = fixture.Live.Economy.Begin(new(systems: [NullSystem])))
        {
            var target = dread.Pilot.Position! + new SpacePosition(20_000, 0, 0);
            action.Add(
                new SavedStructure
                {
                    Id = structure,
                    TypeId = 35832,
                    Name = "Astrahus",
                    CorporationId = emitter.Pilot.CorpId,
                    SystemId = NullSystem,
                    X = target.X,
                    Y = target.Y,
                    Z = target.Z,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    LowPowerAt = fixture.Clock.Now,
                }
            );
            action.Commit();
        }
        fixture.PutInCapitalCargo(Strontium, 51);
        var activation = Activate();
        Assert.True(activation is PilotCallResult.Success, activation.ToString());
        var mode = Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(first.ShipId));
        Assert.Equal(DogmaEffect.CapitalSiege, mode.Effect);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, mode.EndsAt - mode.StartedAt);
        Assert.Equal(50, Quantity(fixture, dread, Strontium));
        Assert.Equal(normalMultiplier * 8, Multiplier(), 6);
        fixture.Clock.Seconds += 5;
        var combat = fixture.Db.Services.GetRequiredService<IPilotCombat>();
        var volley = combat.Fire(first.PilotId, gun, structure, SpacePosition.Zero, fixture.Clock.Now, 0.5);
        Assert.True(volley.Applied > 0);
        Assert.Equal(volley.Health.Remaining, fixture.Structures.Read(first.PilotId, structure)!.Battle!.Health);
        Assert.IsType<PilotCallResult.Success>(dread.Call("dogmaIM", "Deactivate", siege, "moduleBonusSiegeModule"));
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Equal(50, Quantity(fixture, dread, Strontium));
        var warp = fixture.Db.Services.GetRequiredService<IPilotWarpDrive>();
        Assert.Contains(
            "capital mode",
            Assert.Throws<InvalidDataException>(() => warp.Activate(first.PilotId, first.ShipId, 1_000_000, fixture.Clock.Now)).Message
        );
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        Assert.Contains(
            "capital mode",
            Assert.Throws<InvalidDataException>(() => docking.Dock(first.PilotId, first.ShipId, JanStation)).Message
        );
        await dread.TickAsync();
        Assert.Contains(dread.Packets, packet => ContainsText(packet, "effects.SiegeMode"));

        dread.Dispose();
        emitter.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(mode, fixture.Travel.Mode(first.ShipId));
        using Client reopened = fixture.Select(1000001);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(mode.EndsAt - fixture.Clock.Now - 1));
        Assert.Equal(mode, fixture.Travel.Mode(first.ShipId));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        await reopened.TickAsync();
        Assert.Null(fixture.Travel.Mode(first.ShipId));
        Assert.Equal(50, Quantity(fixture, reopened, Strontium));
        Assert.Equal(normalMultiplier, Multiplier(), 6);

        PilotCallResult Activate() => dread.Call("dogmaIM", "Activate", siege, "moduleBonusSiegeModule", null, 0);

        double Multiplier()
        {
            var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
            var item = fixture.Inventory.List(first.PilotId, first.ShipId).Single(i => i.Id == gun);
            return fitting.ModuleAttributes(first.PilotId, item)[DogmaAttributeId.DamageMultiplier];
        }
    }

    [Fact]
    public async Task ApostleJumpsAndAppliesPaidTriageArmorRepair()
    {
        using Fixture fixture = new(Apostle, 16274, Rapier, CapitalTravelRules.StandardGenerator);
        fixture.CalibrateDrive();
        long triage = fixture.FitCapitalModule(CapitalModeRules.TriageTwo);
        long repair = fixture.FitCapitalModule(CapitalRemoteArmor);
        fixture.PutInCapitalCargo(Strontium, 100);
        var first = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(first.ShipId, new(10_000_000, 0, 0), Jan);
        var second = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(second.ShipId, new(1_000_000_000, 0, 0), NullSystem);
        using Client logistics = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var field = fixture.Light(emitter);
        var jump = fixture.Jump(logistics, field);
        Assert.True(jump is PilotCallResult.Success, jump.ToString());
        await logistics.TickAsync();
        fixture.Clock.Seconds += 3;
        await logistics.TickAsync();
        Assert.Equal(NullSystem, logistics.Pilot.SystemId);
        logistics.Context.Flight.Snapshot();
        fixture.Clock.Seconds += 61;
        await logistics.TickAsync();

        using (var action = fixture.Live.Economy.Begin(new(ships: [second.ShipId])))
        {
            action.Add(
                new ShipHealthState
                {
                    ShipId = second.ShipId,
                    ArmorDamage = 250,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            action.Commit();
        }
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var module = fixture.Inventory.List(first.PilotId, first.ShipId).Single(i => i.Id == repair);
        var ordinary = fitting.ModuleAttributes(first.PilotId, module);
        var activated = logistics.Call("dogmaIM", "Activate", triage, "moduleBonusTriageModule", null, 0);
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        Assert.Equal(25, Quantity(fixture, logistics, Strontium));
        var enhanced = fitting.ModuleAttributes(first.PilotId, module);
        Assert.Equal(ordinary[DogmaAttributeId.Duration] * 0.25, enhanced[DogmaAttributeId.Duration], 6);
        Assert.Equal(ordinary[DogmaAttributeId.ArmorRepairAmount] * 5.5, enhanced[DogmaAttributeId.ArmorRepairAmount], 6);
        var remote = fixture.Db.Services.GetRequiredService<IPilotRemoteRepairing>();
        var paid = remote.Start(
            first.PilotId,
            repair,
            DogmaEffect.RemoteArmorRepair,
            second.ShipId,
            new HashSet<long> { second.ShipId },
            fixture.Clock.Now
        );
        Assert.True(paid.Cycle.CompletesAt > fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.Cycle.CompletesAt - fixture.Clock.Now));
        remote.Complete(first.PilotId, repair, fixture.Clock.Now);
        Assert.Equal(0, fixture.Live.Economy.Inspect(world => world.Set<ShipHealthState>().Find(second.ShipId)!.ArmorDamage));
        Assert.NotNull(fixture.Travel.Mode(first.ShipId));

        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        using (var action = fixture.Live.Economy.Begin(new(pilots: [first.PilotId, second.PilotId], systems: [NullSystem])))
        {
            var pilot = action.Pilots.Find(first.PilotId)!;
            var loot = fixture.Db.Services.GetRequiredService<IShipLootRoll>();
            var attacker = PilotInteractionHistoryStore.Player(action, action.Pilots.Find(second.PilotId)!, second.ShipId);
            ShipLossPersistence.Apply(
                action,
                catalog,
                pilot,
                null,
                first.ShipId,
                NullSystem,
                pilot.ActiveShip.Position!,
                fixture.Clock.Now,
                loot,
                attacker
            );
            action.Commit();
        }
        Assert.Null(fixture.Travel.Mode(first.ShipId));
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Null(fixture.Travel.Mode(first.ShipId));
        Assert.NotEqual(first.ShipId, fixture.Db.Store.FindPilot(1000001)!.ShipId);
        var loss = fixture.Live.Economy.Inspect(world => world.Filter<ShipLoss>(row => row.ShipId == first.ShipId).Single());
        Assert.Equal(Apostle, loss.TypeId);
    }

    [Fact]
    public void CombatCapitalCannotTakeStargateIntoHighSecurityOrWhileInSiege()
    {
        using Fixture fixture = new(Revelation, 16274);
        long module = fixture.FitCapitalModule(CapitalModeRules.SiegeOne);
        fixture.PutInCapitalCargo(Strontium, 225);
        const int vaajaita = 30001383;
        const long entry = 50002908;
        const long exit = 50002011;
        var gatePosition = new SpacePosition(-3177874022400, 242763079680, -891612487680);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO SolarSystem(Id,Name,ConstellationId) VALUES ({vaajaita},'Vaajaita',20000202);
                INSERT OR IGNORE INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                  ({entry},{Jan},16,10,{gatePosition.X},{gatePosition.Y},{gatePosition.Z}),
                  ({exit},{vaajaita},16,10,465029160960,49665761280,99157647360);
                INSERT OR IGNORE INTO Stargate(Id,SolarSystemId,DestinationId) VALUES ({entry},{Jan},{exit}),({exit},{vaajaita},{entry});
                """
            );
            const string security = """{"security":0.462063640356065}""";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001383',{security})");
        }
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(pilot.ShipId, gatePosition, Jan);
        var gate = fixture.Db.Services.GetRequiredService<IPilotGateTravel>();
        Assert.Contains(
            "high security",
            Assert.Throws<InvalidDataException>(() => gate.Transfer(pilot.PilotId, pilot.ShipId, entry, exit)).Message
        );
        fixture.Travel.ActivateMode(pilot.PilotId, pilot.ShipId, module, DogmaEffect.CapitalSiege);
        Assert.Contains(
            "capital mode",
            Assert.Throws<InvalidDataException>(() => gate.Transfer(pilot.PilotId, pilot.ShipId, entry, exit)).Message
        );
        Assert.Equal(Jan, fixture.Db.Store.FindPilot(1000001)!.SystemId);
    }
}
