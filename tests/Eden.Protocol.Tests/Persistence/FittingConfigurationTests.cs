using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class FittingConfigurationTests
{
    [Fact]
    public async Task ConfiguredBudgetsDriveResidentFittingAndNativeModifiersAcrossRestart()
    {
        const int merlin = 603;
        const int extender = 377;
        const int rig = 31544;
        const int shieldUpgrades = 3425;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        var database = fixture.Services.GetRequiredService<WorldDatabase>();
        PassiveCatalogFixture.Load(database.CatalogPath);
        PassiveCatalogFixture.LoadRigs(database.CatalogPath);
        using (CatalogDbContext edit = new(database.CatalogPath, writable: true))
        {
            var resources = edit.Set<TypeAttribute>().AsTracking().Where(attribute => attribute.ItemTypeId == extender);
            resources.Single(attribute => attribute.AttributeId == (int)DogmaAttributeId.Cpu).Value = 220;
            resources.Single(attribute => attribute.AttributeId == (int)DogmaAttributeId.Power).Value = 60;
            edit.SaveChanges();
        }
        var configuration = fixture.Services.GetRequiredService<IWorldConfiguration>();
        var original = configuration.Read();
        var legacy = JsonNode.Parse(configuration.Export(original.Active.Id))!;
        legacy["settings"]!.AsObject().Remove("shipFitting");
        Assert.Equal(new ShipFittingPolicy(), ConfigurationValidation.Decode(legacy.ToJsonString()).Settings.ShipFitting);
        Assert.Throws<InvalidDataException>(() =>
            configuration.Validate(
                original.Active.Draft with
                {
                    Settings = original.Active.Draft.Settings with { ShipFitting = new(PowerGridMultiplier: 0) },
                }
            )
        );

        var fitting = fixture.Services.GetRequiredService<IPilotFitting>();
        var inventory = fixture.Services.GetRequiredService<IPilotInventory>();
        Assert.Equal(220, fitting.Attributes(extender)[DogmaAttributeId.Cpu]);
        Assert.Equal(60, fitting.Attributes(extender)[DogmaAttributeId.Power]);
        ShipFittingPolicy policy = new(1.5m, 2, 1.5m, 2);
        long pilotId;
        long shipId;
        int stationId;
        long module;
        long rigStack;
        double cpu;
        double grid;
        using (var live = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false))
        {
            var pilot = fixture.Store.FindPilot(1000001)!;
            (pilotId, shipId, stationId) = (pilot.PilotId, pilot.ShipId, pilot.StationId);
            using (var turn = live.Economy.Begin())
            {
                turn.Ships.Find(shipId)!.TypeId = merlin;
                turn.RemoveRange(
                    turn.Filter<OwnedItem>(item => item.LocationId == shipId)
                        .AsEnumerable()
                        .Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId))
                );
                using CatalogDbContext catalog = new(database.CatalogPath);
                foreach (
                    var (skill, level) in new[]
                    {
                        ((int)FittingSkill.CpuManagement, 2),
                        ((int)FittingSkill.PowerGridManagement, 3),
                        (shieldUpgrades, 1),
                    }
                )
                {
                    var trained =
                        turn.Set<PilotSkill>().Find(pilotId, skill)
                        ?? turn.Add(new PilotSkill { PilotId = pilotId, TypeId = skill }).Entity;
                    double rank = catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant];
                    trained.Points = TrainedSkill.PointsForLevel(rank, level);
                }
                module = Grant(extender, 1);
                rigStack = Grant(rig, 2);
                turn.Commit();

                long Grant(int type, int quantity)
                {
                    long id = InventoryPersistence.NextId(turn);
                    turn.Add(
                        new OwnedItem
                        {
                            Id = id,
                            OwnerId = pilotId,
                            LocationId = stationId,
                            TypeId = type,
                            Quantity = quantity,
                            FlagId = (int)InventoryFlag.Hangar,
                        }
                    );
                    return id;
                }
            }
            fitting.Fit(pilotId, [module], stationId, (int)InventoryFlag.AutoFit);
            fitting.Fit(pilotId, [rigStack], stationId, (int)InventoryFlag.AutoFit);
            var baseline = fitting.ShipAttributes(pilotId);
            cpu = baseline[DogmaAttributeId.CpuOutput];
            grid = baseline[DogmaAttributeId.PowerOutput];
            Assert.Equal(180 * 1.1, cpu, 8);
            Assert.Equal(40 * 1.15, grid, 8);
            Assert.False(inventory.List(pilotId, shipId).Single(item => item.Id == module).Online);
            Assert.Throws<InvalidDataException>(() => fitting.SetOnline(pilotId, shipId, module, true));
            Assert.Throws<InvalidDataException>(() => fitting.Fit(pilotId, [rigStack], stationId, (int)InventoryFlag.AutoFit));
            configuration.Save(
                original.Generation,
                original.Active.Draft with
                {
                    Settings = original.Active.Draft.Settings with { ShipFitting = policy },
                },
                "fitting budget replay"
            );
            Assert.Equal(cpu, fitting.ShipAttributes(pilotId)[DogmaAttributeId.CpuOutput]);
            await live.FlushAsync();
        }

        WorldInitialization.Initialize(database, clock);
        using (var live = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false))
        {
            var attributes = fitting.ShipAttributes(pilotId);
            Assert.Equal(cpu * 1.5, attributes[DogmaAttributeId.CpuOutput], 8);
            Assert.Equal(grid * 2, attributes[DogmaAttributeId.PowerOutput], 8);
            Assert.Equal(600, attributes[DogmaAttributeId.CalibrationCapacity]);
            Assert.Equal(0, attributes.GetValueOrDefault(DogmaAttributeId.DroneBandwidth));
            var brain = fixture.Services.GetRequiredService<PilotBrain>().Read(pilotId, shipId);
            foreach (var (attribute, factor) in WorldTuningRules.FittingMultipliers(policy))
            {
                var effect = Assert.Single(brain.ShipEffects, e => e.Attribute == attribute && e.Operation == BrainOperation.PostMultiply);
                Assert.Equal(BrainModifier.Item, effect.Modifier);
                Assert.Equal(shipId, effect.TargetId);
                var wire = Assert.IsType<BlueNewObject>(BlueMarshal.Decode(BlueMarshal.Encode(effect)));
                Assert.Equal(factor, Assert.IsType<Dictionary<string, object?>>(wire.State)["value"]);
            }
            Assert.Single(fitting.SetOnline(pilotId, shipId, module, true));
            fitting.Fit(pilotId, [rigStack], stationId, (int)InventoryFlag.AutoFit);
            Assert.Equal(600, fitting.ShipAttributes(pilotId)[DogmaAttributeId.CalibrationLoad]);
            configuration.Restore(configuration.Read().Generation, original.Active.Id, "restore fitting budget");
            await live.FlushAsync();
        }

        WorldInitialization.Initialize(database, clock);
        using var restored = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false);
        var retained = fitting.ShipAttributes(pilotId);
        Assert.Equal(cpu, retained[DogmaAttributeId.CpuOutput]);
        Assert.Equal(400, retained[DogmaAttributeId.CalibrationCapacity]);
        Assert.Equal(600, retained[DogmaAttributeId.CalibrationLoad]);
        Assert.True(inventory.List(pilotId, shipId).Single(item => item.Id == module).Online);
        fitting.SetOnline(pilotId, shipId, module, false);
        Assert.Throws<InvalidDataException>(() => fitting.SetOnline(pilotId, shipId, module, true));
    }
}
