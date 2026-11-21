using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class PilotSpaceActivitiesTests
{
    [Fact]
    public async Task RatterBuysSalvagerAndResumesPaidRecoveryBeforeSellingIntoFundedDemand()
    {
        const int salvagerType = 25861;
        const int wreckType = 26573;
        const int material = (int)SalvageMaterial.TrippedPowerCircuit;
        const long npcId = NpcEncounterDefinitions.FirstNpcId + 155;
        const long wreckId = WreckDefinitions.FirstWreckId + 155;
        const long foreignWreck = wreckId + 1;
        const long bidPrice = 150_000;
        using CapsuleerHaulerTests.Fixture f = new();
        PassiveCatalogFixture.LoadInformation(
            f.Database.CatalogPath,
            new HashSet<int> { salvagerType, wreckType, material, (int)InformationSkill.Salvaging }
        );
        long pilot = PrepareDockedRatter(f);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"UPDATE TypeAttribute SET Value=100 WHERE ItemTypeId={wreckType} AND AttributeId={(int)DogmaAttributeId.AccessDifficulty}"
            );
        }
        long buyer;
        long ship;
        long balance;
        long module;
        long sellersBalance;
        RattingSalvage paid;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (CatalogDbContext catalog = new(f.Database.CatalogPath))
            using (var turn = live.Economy.Begin())
            {
                buyer = turn.Set<SavedCapsuleerAgent>().Single(row => row.Career == CapsuleerCareer.Mining).PilotId;
                ship = turn.Pilots.Find(pilot)!.ActiveShipId;
                // Keep the legacy founder eligible when startup reapplies the saved traffic density.
                var population =
                    turn.Set<SavedCapsuleerProvisioning>().Find(1) ?? turn.Add(new SavedCapsuleerProvisioning { Id = 1 }).Entity;
                population.WorldSeed = 1;
                turn.Set<PilotWallet>().Find(pilot)!.Cents = 1_000_000;
                turn.Set<PilotWallet>().Find(buyer)!.Cents = 1_000_000;
                int skill = (int)InformationSkill.Salvaging;
                var trained =
                    turn.Set<PilotSkill>().Find(pilot, skill) ?? turn.Add(new PilotSkill { PilotId = pilot, TypeId = skill }).Entity;
                trained.Points = TrainedSkill.PointsForLevel(catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant], 1);
                module = InventoryPersistence.NextId(turn);
                turn.Add(
                    new OwnedItem
                    {
                        Id = module,
                        OwnerId = buyer,
                        TypeId = salvagerType,
                        LocationId = Station,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 1,
                    }
                );
                var belt = SystemObjects.Geometry(catalog, SystemId).Single(o => o.Id == 40009051);
                for (int index = 0; index < 2; index++)
                {
                    turn.Add(
                        new WorldNpc
                        {
                            Id = npcId + index,
                            TypeId = 601,
                            SystemId = SystemId,
                            DestroyedAt = f.Clock.Now,
                        }
                    );
                    turn.Add(
                        new WorldWreck
                        {
                            Id = wreckId + index,
                            NpcId = npcId + index,
                            OwnerId = index == 0 ? pilot : buyer,
                            TypeId = wreckType,
                            Name = "Guristas Small Wreck",
                            SystemId = SystemId,
                            X = belt.Position.X + (index + 1) * 1000,
                            Y = belt.Position.Y,
                            Z = belt.Position.Z,
                            CreatedAt = f.Clock.Now,
                            ExpiresAt = f.Clock.Now + TimeSpan.TicksPerHour,
                            SalvageJson = JsonSerializer.Serialize(new[] { new ScanItem(material, 3) }),
                        }
                    );
                }
                turn.Commit();
            }
            var market = host.GetRequiredService<IPersonalMarket>();
            market.PlaceSell(buyer, [new(module, Station, salvagerType, 10_000, 1)], 7);
            market.PlaceBuy(buyer, new(Station, material, bidPrice, 1, 7));
            using var observer = host.GetRequiredService<IHumanPresence>().Select(90000001, SystemId);
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (int i = 0; i < 500 && scheduler.Hauler(pilot)?.Work.Ratting?.Salvage?.CycleReadyAt is not > 0; i++)
            {
                f.Clock.Seconds++;
                maintenance.Tick();
                Assert.Null(scheduler.Hauler(pilot)?.Work.WaitingReason);
            }
            var observation = scheduler.Hauler(pilot)!;
            Assert.True(
                observation.Work.Ratting?.Salvage is { CycleReadyAt: > 0 },
                $"Expected a paid cycle. Last activity: {observation.Work.Activity}, {observation.Work.Ratting?.Phase}."
            );
            paid = observation.Work.Ratting!.Salvage!;
            Assert.True(paid.CycleReadyAt > f.Clock.Now);
            Assert.Equal(wreckId, paid.WreckId);
            module = paid.ModuleId;
            (balance, sellersBalance) = live.Economy.Read(world =>
            {
                Assert.True(world.Set<OwnedItem>().Find(module)!.Online);
                Assert.Equal(paid.CycleReadyAt, world.Set<SavedInformationCycle>().Find(module)!.ReadyAt);
                Assert.Equal(paid.CycleReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
                Assert.Null(world.Set<WorldWreck>().Find(wreckId)!.SalvagedAt);
                Assert.Empty(world.Filter<OwnedItem>(item => item.LocationId == ship).Where(item => item.TypeId == material));
                return (world.Set<PilotWallet>().Find(pilot)!.Cents, world.Set<PilotWallet>().Find(buyer)!.Cents);
            });
            Assert.Equal(990_000, balance);
            observer.Dispose();
            f.Clock.Seconds += 120;
            maintenance.Tick();
            Assert.Equal(paid, scheduler.Hauler(pilot)?.Work.Ratting?.Salvage);
            await live.FlushAsync();
        }

        WorldInitialization.Initialize(f.Database, f.Clock);
        using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(paid.CycleReadyAt, restored.Economy.Read(world => world.Set<SavedInformationCycle>().Find(module)!.ReadyAt));
        using var observerAfterReopen = reopened.GetRequiredService<IHumanPresence>().Select(90000001, SystemId);
        var resumed = reopened.GetRequiredService<ICapsuleerScheduler>();
        Assert.Equal(paid, resumed.Hauler(pilot)?.Work.Ratting?.Salvage);
        Assert.NotNull(restored.Economy.Read(world => world.Set<ActiveFittedModule>().Find(module)));
        var jobs = reopened.GetRequiredService<CapsuleerMaintenance>();
        Assert.False(restored.Economy.Read(world => world.Set<SavedCapsuleerSuspension>().Find(pilot) is not null));
        observerAfterReopen.Transfer(30000142);
        HaulerObservation? returned = null;
        for (int i = 0; i < 500; i++)
        {
            f.Clock.Seconds++;
            jobs.Tick();
            var state = resumed.Hauler(pilot)!;
            Assert.Null(state.Work.WaitingReason);
            if (state.Pilot.Position is null && state.Work.Ratting is null)
            {
                returned = state;
                break;
            }
        }
        Assert.NotNull(returned);
        restored.Economy.Read(world =>
        {
            Assert.NotNull(world.Set<WorldWreck>().Find(wreckId)!.SalvagedAt);
            Assert.Null(world.Set<WorldWreck>().Find(foreignWreck)!.SalvagedAt);
            Assert.Null(world.Set<SavedInformationCycle>().Find(module));
            Assert.Equal(paid.CycleReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
            Assert.Equal(sellersBalance, world.Set<PilotWallet>().Find(buyer)!.Cents);
            Assert.InRange(world.Set<PilotWallet>().Find(pilot)!.Cents, balance + 1, balance + bidPrice);
            var order = Assert.Single(world.Filter<SavedPersonalOrder>(order => order.PilotId == buyer).Where(order => order.Bid));
            Assert.Equal(0, order.Remaining);
            Assert.Equal(0, order.EscrowCents);
            var materials = world.Filter<OwnedItem>(item => item.LocationId == Station).Where(item => item.TypeId == material).ToArray();
            Assert.Equal(2, Assert.Single(materials, item => item.OwnerId == pilot).Quantity);
            Assert.Equal(1, Assert.Single(materials, item => item.OwnerId == buyer).Quantity);
            return true;
        });
    }
}
