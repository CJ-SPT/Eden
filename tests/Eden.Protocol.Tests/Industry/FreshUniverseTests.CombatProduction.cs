using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class FreshUniverseTests
{
    [InstalledCatalogFact]
    public async Task NativeCombatKitsPayForManufactureAndBoardTwoCruisersABattlecruiserAndABattleship()
    {
        var directory = TestDirectory.Create("eden-native-combat-production-");
        try
        {
            await VerifyNativeCombatProduction(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task VerifyNativeCombatProduction(string directory)
    {
        int[] targets = [CapsuleerPatrolFit.Caracal, CapsuleerPatrolFit.Moa, CapsuleerPatrolFit.Drake, CapsuleerPatrolFit.Raven];
        const long openingCapital = 100_000_000_000L;
        WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
        File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
        TestClock clock = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
        long buyer;
        List<long> boarded = [];
        using (var host = ServerServices.Build(database, clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using var catalog = new CatalogDbContext(database.CatalogPath);
            var presence = host.GetRequiredService<IHumanPresence>();
            var provisioner = host.GetRequiredService<CapsuleerProvisioner>();
            var synchronization = host.GetRequiredService<IWorldSynchronization>();
            presence.Update(Guid.NewGuid(), StarterSystem);
            long manufacturer = Assert.IsType<long>(
                provisioner.PrepareBackgroundSystem(StarterSystem, synchronization, () => true, CancellationToken.None)?.CreatedPilotId
            );
            buyer = Assert.IsType<long>(
                provisioner.PrepareBackgroundSystem(StarterSystem, synchronization, () => true, CancellationToken.None)?.CreatedPilotId
            );
            long supplier = CreateSupplier(host, manufacturer);
            var kits = targets.Select(type => Assert.IsType<ReplacementKit>(CapsuleerIndustry.CareerKit(catalog, type))).ToArray();
            Assert.All(kits, kit => Assert.Contains(kit.Slots, slot => InventoryDefinitions.IsMediumSlot(slot.FlagId)));
            var recipes = ManufacturingCatalog.Recipes(catalog).Values.ToLookup(recipe => recipe.ProductTypeId);
            var production = kits.SelectMany(kit => kit.Parts())
                .Select(part => (Part: part, Recipe: Assert.Single(recipes[part.TypeId])))
                .ToArray();
            var materials = production
                .SelectMany(part =>
                    IndustryMaterials.Calculate(part.Recipe.Materials, (part.Part.Quantity - 1) / part.Recipe.OutputQuantity + 1)
                )
                .GroupBy(input => input.Key)
                .ToDictionary(type => type.Key, type => type.Sum(input => input.Value));
            MarketSale[] inputs;
            using (var opening = live.Economy.Begin(TransactionParticipants.None))
            {
                foreach (long pilot in new[] { manufacturer, buyer, supplier })
                {
                    var wallet = opening.Set<PilotWallet>().Find(pilot) ?? opening.Add(new PilotWallet { PilotId = pilot }).Entity;
                    wallet.Cents = openingCapital;
                    opening.Pilots.Find(pilot)!.ActiveShip.StationId = Factory;
                }
                Train(opening, manufacturer, CorporationRules.ManagementSkill, 1);
                Train(opening, supplier, (int)MarketSkill.Trade, 4);
                foreach (var recipe in production.Select(part => part.Recipe).DistinctBy(recipe => recipe.BlueprintTypeId))
                {
                    // Open from established licenses and upstream material stock. Finished kits begin empty.
                    opening.Add(
                        new OwnedItem
                        {
                            Id = opening.NextItemId(),
                            OwnerId = manufacturer,
                            LocationId = Factory,
                            FlagId = (int)InventoryFlag.Hangar,
                            TypeId = recipe.BlueprintTypeId,
                            Quantity = 1,
                            Singleton = true,
                            BlueprintRuns = -1,
                        }
                    );
                    foreach (var skill in recipe.Skills)
                    {
                        Train(opening, manufacturer, skill.TypeId, skill.Level);
                    }
                }
                foreach (int part in production.Select(part => part.Part.TypeId).Distinct())
                {
                    CapsuleerCommunityStore.GrantRequirements(opening, catalog, buyer, part, []);
                }
                inputs = materials
                    .Select(material =>
                    {
                        var item = InventoryPersistence
                            .MergeOutput(opening, catalog, supplier, Factory, (int)InventoryFlag.Hangar, material.Key, material.Value)
                            .Item;
                        long price = CapsuleerIndustry.ReferencePrice(catalog, material.Key);
                        Assert.True(price > 0, $"Missing native reference for input {material.Key}.");
                        return new MarketSale(item.Id, Factory, material.Key, price, material.Value);
                    })
                    .ToArray();
                opening.Commit();
            }

            int corporation = host.GetRequiredService<IPlayerCorporations>()
                .Create(manufacturer, PlayerCorporationTests.Details("Combat Foundry", "CBTF"))
                .Corporation.Id;
            using (var treasury = live.Economy.Begin(TransactionParticipants.None))
            {
                var charter = treasury.Set<SavedCorporation>().Find(corporation)!;
                charter.CapsuleerFoundingSystemId = StarterSystem;
                charter.BalanceCents = openingCapital;
                treasury.Commit();
            }
            var personal = host.GetRequiredService<IPersonalMarket>();
            var market = host.GetRequiredService<IPilotMarket>();
            personal.PlaceSell(supplier, inputs, 7);
            Assert.All(
                kits.SelectMany(kit => kit.Parts()).Select(part => part.TypeId).Distinct(),
                part => Assert.DoesNotContain(market.Orders(buyer, part), order => !order.Bid)
            );
            var industry = host.GetRequiredService<CapsuleerIndustry>();
            var pilots = host.GetRequiredService<SqliteWorld>();
            foreach (var kit in kits)
            {
                var plan = new CapsuleerCareerPlan(CapsuleerProfession.Combat, kit.HullTypeId, CapsuleerTrainingFocus.Ship);
                int attempts = kit.Parts().Length;
                for (int step = 0; step <= attempts; step++)
                {
                    CareerShipTurn purchase;
                    using (var action = live.Economy.Begin(TransactionParticipants.None))
                    {
                        purchase = industry.AdvanceCareerShip(
                            action,
                            catalog,
                            pilots.FindPilot(action, CapsuleerCommunityStore.MinerAccountId, buyer)!,
                            plan,
                            clock.Now
                        );
                        if (purchase.Boarded)
                        {
                            long ship = action.Pilots.Find(buyer)!.ActiveShipId;
                            boarded.Add(ship);
                            Assert.All(
                                kit.Slots,
                                slot =>
                                    Assert.Contains(
                                        action.FittedItems(buyer, ship),
                                        item => item.FlagId == slot.FlagId && item.TypeId == slot.TypeId && item.Online
                                    )
                            );
                            var combat = CapsuleerDefenseProfile.Read(action, catalog, buyer);
                            Assert.True(combat.Combat && combat.HasDamage && combat.TackleRange > 0);
                            Assert.Contains(combat.RattingWeapons, weapon => weapon.AvailableShots > 0);
                        }
                        action.Commit();
                    }
                    if (purchase.Boarded)
                    {
                        break;
                    }
                    Assert.True(purchase.MarketChanged, $"Buyer placed no bid for a missing part of {kit.HullTypeId}.");
                    var bid = Assert.Single(personal.PersonalOrders(buyer));
                    Assert.Equal(bid.PriceCents * bid.Remaining, bid.EscrowCents);
                    int remaining = bid.Remaining;
                    while (remaining > 0)
                    {
                        WorkFactory();
                        var job = live.Economy.Inspect(world =>
                            Assert.Single(
                                world.Filter<IndustryJob>(job => job.PilotId == manufacturer).AsEnumerable(),
                                job => job.ProductTypeId == bid.TypeId && job.Status == IndustryJobStatus.Installed
                            )
                        );
                        Assert.True(job.CostCents > 0);
                        Assert.InRange(job.OutputQuantity, 1, remaining);
                        long ticks = job.CapsuleerEndTicks!.Value - presence.Read().ElapsedTicks;
                        clock.Milliseconds += Math.Max(1, (ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
                        WorkFactory();
                        remaining -= job.OutputQuantity;
                        live.Economy.Inspect(world =>
                        {
                            Assert.Equal(IndustryJobStatus.Delivered, world.Set<IndustryJob>().Find(job.Id)!.Status);
                            var order = world.Set<SavedPersonalOrder>().Find(bid.Id)!;
                            Assert.Equal(remaining, order.Remaining);
                            Assert.Equal(bid.PriceCents * remaining, order.EscrowCents);
                            Assert.Equal(remaining > 0 ? PersonalOrderStatus.Open : PersonalOrderStatus.Filled, order.Status);
                            return 0;
                        });
                    }
                }
                Assert.Equal(kit.HullTypeId, live.Economy.Inspect(world => world.Pilots.Find(buyer)!.ActiveShip.TypeId));
                output.WriteLine($"Hull {kit.HullTypeId}: {kit.Slots.Length} online modules and {kit.Magazines.Length} loaded magazines.");
            }
            Assert.Equal(targets.Length, boarded.Count);
            var transactions = market.Transactions(buyer);
            Assert.All(
                kits.SelectMany(kit => kit.Parts()),
                part =>
                    Assert.Contains(
                        transactions,
                        trade => trade.TypeId == part.TypeId && trade.IsBuy && trade.CounterpartyId == corporation && trade.PriceCents > 0
                    )
            );
            Assert.Contains(market.Transactions(supplier), trade => !trade.IsBuy && trade.CounterpartyId == corporation);
            Assert.Empty(personal.PersonalOrders(buyer));
            live.Economy.Inspect(world =>
            {
                Assert.DoesNotContain(world.Set<NpcMarketOrder>().AsEnumerable(), order => order.Kind == NpcMarketKind.Development);
                Assert.Empty(world.Set<TestingWalletGrant>());
                Assert.True(world.Set<SavedCorporation>().Find(corporation)!.BalanceCents > openingCapital);
                return 0;
            });
            await live.FlushAsync();

            void Train(EconomyTransaction setup, long pilot, int type, int level)
            {
                CapsuleerCommunityStore.GrantRequirements(setup, catalog, pilot, type, []);
                var skill =
                    setup.Set<PilotSkill>().Find(pilot, type) ?? setup.Add(new PilotSkill { PilotId = pilot, TypeId = type }).Entity;
                double rank = catalog.FittingAttributes(type).GetValueOrDefault(Eden.Gameplay.Dogma.DogmaAttributeId.SkillTimeConstant);
                skill.Points = Math.Max(skill.Points, TrainedSkill.PointsForLevel(rank, level));
            }

            void WorkFactory()
            {
                using var action = live.Economy.Begin(TransactionParticipants.None);
                var factory = pilots.FindPilot(action, CapsuleerCommunityStore.MinerAccountId, manufacturer)!;
                industry.ProduceScheduled(action, catalog, factory, clock.Now, out var shortages);
                Assert.Null(shortages);
                action.Commit();
            }
        }
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        reopened
            .GetRequiredService<LiveWorld>()
            .Economy.Inspect(world =>
            {
                Assert.Equal(boarded[^1], world.Pilots.Find(buyer)!.ActiveShipId);
                Assert.Equal(targets, boarded.Select(ship => world.Ships.Find(ship)!.TypeId));
                return 0;
            });
    }
}
