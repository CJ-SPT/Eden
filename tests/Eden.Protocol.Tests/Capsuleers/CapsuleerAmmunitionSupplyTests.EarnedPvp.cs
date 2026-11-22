using Eden.Data.Catalog.Capsuleers;
using Eden.Data.Catalog.Market;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Protocol.Tests.Market;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EstablishedPilotBuysItsFirstPvpFitFromBountiesAndKeepsItAfterReopen(bool remoteSupplier, bool progress)
    {
        const int incursus = (int)TechOneHullType.Incursus;
        const int rifter = (int)TechOneHullType.Rifter;
        const int minmatarFrigate = 3329;
        const int gallenteDestroyer = 33093;
        int nextHull = progress ? CapsuleerPatrolFit.Catalyst : rifter;
        int nextSkill = progress ? gallenteDestroyer : minmatarFrigate;
        const int railgun = (int)CombatWeaponType.GatlingRailgun75mm;
        const int scrambler = 447;
        const int repairer = (int)StarterMarketType.SmallArmorRepairer;
        const int repairSlot = (int)InventoryFlag.LowSlot0 + 2;
        const int charge = CapsuleerAmmunitionSupplyRules.ChargeType;
        const int pirate = 30380;
        const int jitaTradeHub = 60003760;
        const long priceCents = MarketDefinitions.CentsPerIsk;
        const long openingCents = 100_000 * MarketDefinitions.CentsPerIsk;
        const long bountyIsk = 2_000_000;

        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        if (progress)
        {
            PassiveCatalogFixture.LoadSmallHull(fixture.Database.CatalogPath, nextHull);
        }
        PassiveCatalogFixture.LoadInformation(fixture.Database.CatalogPath, new HashSet<int> { 26528 });
        PassiveCatalogFixture.LoadTackleTiers(fixture.Database.CatalogPath, new HashSet<int> { scrambler });
        PassiveCatalogFixture.LoadStarterMarket(fixture.Database.CatalogPath, new HashSet<int> { repairer, 3426, 3435, 3449 });
        using (CatalogDbContext edit = new(fixture.Database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES (11,'Entity');
                INSERT OR IGNORE INTO ItemGroup VALUES (286,'Minor Threat',11),(186,'Wreck',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (30380,'Test Pirate',286),(26557,'Frigate Wreck',186);
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','30380','{{"wreckTypeID":26557}}'),
                    ('client/types','26557','{{"volume":27500}}'),('client/systems','30000141','{{"securityStatus":-0.2}}');
                UPDATE SourceRecord SET Data=json_set(Data,'$.basePrice',20)
                    WHERE Dataset='client/types' AND Key IN ('215','447');
                UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2)
                    WHERE Dataset='client/solarsystemcontent' AND Key='30000141';
                """
            );
        }

        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var listingStation = catalog.RequireStation(remoteSupplier ? jitaTradeHub : Depot);
        double railCapacity = catalog.RequireItem(railgun).Capacity!.Value;
        double roundVolume = PilotInventoryStore.Volume(catalog, charge);
        int magazine = (int)Math.Floor(railCapacity / roundVolume + InventoryDefinitions.ResourceTolerance);
        const int firstHigh = (int)InventoryFlag.HighSlot0;
        TrafficProfile profile = TrafficSnapshotTests.Profile(name: "Incursus pilot") with
        {
            HullTypeId = incursus,
            SystemId = Home,
            RegionId = listingStation.RegionId,
            SecurityBand = TrafficSecurityBand.Null,
            Equipment =
            [
                new(railgun, firstHigh, 1),
                new(charge, firstHigh, magazine),
                new(railgun, firstHigh + 1, 1),
                new(charge, firstHigh + 1, magazine),
                new(scrambler, (int)InventoryFlag.MediumSlot0, 1),
                new(repairer, repairSlot, 1),
            ],
        };
        TrafficProfile unavailableSkill = profile with
        {
            CharacterId = profile.CharacterId + 1,
            KillId = profile.KillId + 1,
            Name = "Next hull pilot",
            HullTypeId = nextHull,
        };
        TrafficSnapshotTests.Install(fixture.Database, unavailableSkill, profile);
        var parts = profile
            .Equipment.GroupBy(item => item.TypeId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        parts[incursus] = 1;
        parts[charge] *= 5;
        Assert.True(parts.Values.Sum() * priceCents < openingCents);
        var orders = parts
            .Append(new(nextHull, 1))
            .Select(
                (part, index) =>
                    new LiveMarketOrder(
                        index + 1,
                        listingStation.Id,
                        listingStation.SolarSystemId,
                        listingStation.RegionId,
                        part.Key,
                        Bid: false,
                        part.Key == nextHull ? priceCents / 2 : priceCents,
                        progress && part.Key != incursus && part.Key != nextHull ? part.Value * 2 : part.Value,
                        progress && part.Key != incursus && part.Key != nextHull ? part.Value * 2 : part.Value,
                        fixture.Clock.Now,
                        MarketDefinitions.NpcOrderDays,
                        MarketDefinitions.StationRange,
                        MinimumVolume: 1
                    )
            )
            .ToArray();
        StandardMarketTests.WriteSnapshot(fixture.Database, fixture.Clock, orders);

        var config = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var initial = config.Read();
        var draft = initial.Active.Draft;
        var settings = draft.Settings;
        config.Save(
            initial.Generation,
            draft with
            {
                Settings = settings with
                {
                    Capsuleers = settings.Capsuleers with
                    {
                        EarnedPvpShips = true,
                        Exploration = false,
                        GateCamps = false,
                        RoamingGangs = false,
                        RoamPercent = 100,
                    },
                    Economy = settings.Economy with
                    {
                        ProgressiveMarkets = false,
                        StandardMarket = true,
                        RegionalHubSupply = false,
                        BeginnerMarketSupply = false,
                    },
                    DevelopmentSupply = settings.DevelopmentSupply with { TestingMarket = false, TestingGrants = false },
                },
            },
            "Earned PvP ships"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        var snapshot = fixture.Database.Traffic;
        Assert.Equal(2, snapshot.Profiles.Count);

        long pilotId;
        long workShipId;
        long paidShipId;
        long walletAfterPurchase;
        long[] purchaseIds;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var ships = host.GetRequiredService<SqliteWorld>();
            var supply = host.GetRequiredService<CapsuleerIndustry>();
            using (var setup = live.Economy.Begin())
            {
                var agent = setup.Set<SavedCapsuleerAgent>().First(row => row.Career == CapsuleerCareer.Hauling);
                pilotId = agent.PilotId;
                foreach (var other in setup.Set<SavedCapsuleerAgent>().Where(row => row.PilotId != pilotId).ToArray())
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = other.PilotId });
                }
                var pilot = setup.Pilots.Find(pilotId)!;
                workShipId = pilot.ActiveShipId;
                pilot.ActiveShip.StationId = Depot;
                setup.RemoveRange(
                    setup
                        .Filter<OwnedItem>(item => item.LocationId == workShipId)
                        .Where(item => ShipHoldRules.IsHold(item.FlagId))
                        .ToArray()
                );
                agent.StartingCents = openingCents;
                setup.Set<PilotWallet>().Find(pilotId)!.Cents = openingCents;
                Charter(setup, catalog, pilotId, "Ratting Patrol", "RATP", fixture.Clock.Now);

                setup.Pilots.Find(Customer)!.ActiveShip.StationId = Depot;
                if (setup.Positions.Find(setup.Pilots.Find(Customer)!.ActiveShipId) is { } customerPosition)
                {
                    setup.Remove(customerPosition);
                }
                foreach (int type in parts.Keys)
                {
                    CapsuleerCommunityStore.GrantRequirements(setup, catalog, pilotId, type, []);
                }
                if (progress)
                {
                    CapsuleerCommunityStore.GrantRequirements(setup, catalog, pilotId, nextSkill, []);
                    var skill =
                        setup.Set<PilotSkill>().Find(pilotId, nextSkill)
                        ?? setup.Add(new PilotSkill { PilotId = pilotId, TypeId = nextSkill }).Entity;
                    skill.Points = 0;
                }
                else if (setup.Set<PilotSkill>().Find(pilotId, nextSkill) is { } trained)
                {
                    setup.Remove(trained);
                }
                setup.Commit();
            }
            var market = host.GetRequiredService<IPilotMarket>();
            var seededHull = Assert.Single(market.Orders(pilotId, incursus), order => !order.Bid && order.StationId == listingStation.Id);
            Assert.Equal((priceCents, 1), (seededHull.PriceCents, seededHull.Remaining));

            long fundedBalance;
            using (var work = live.Economy.Begin())
            {
                var docked = ships.FindPilot(work, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
                Assert.True(CapsuleerIndustry.CanStagePvp(work, catalog, docked.SystemId));
                Assert.True(CapsuleerWorkHulls.CanLeaveShip(work, catalog, docked));
                Assert.False(CapsuleerIndustry.HasPvpShip(work, pilotId, workShipId));
                var pending = supply.TryBuyEarnedPvpShip(work, catalog, snapshot, docked, fixture.Clock.Now);
                Assert.False(pending.Boarded);
                Assert.False(pending.MarketChanged);
                Assert.Equal(workShipId, work.Pilots.Find(pilotId)!.ActiveShipId);
                Assert.Equal(openingCents, work.Set<PilotWallet>().Find(pilotId)!.Cents);
                var organization = CapsuleerCorporations.Organization(work, pilotId);
                Assert.Null(organization.EarnedPvpShipId);
                Assert.Equal(remoteSupplier ? null : incursus, organization.PvpFitGoal?.HullTypeId);
                Assert.Empty(work.Filter<MarketExecution>(row => row.PilotId == pilotId));
                work.Commit();
            }

            using (var work = live.Economy.Begin())
            {
                var pilot = work.Pilots.Find(pilotId)!;
                var station = SystemObjects.Geometry(catalog, Home).Single(body => body.Id == Depot);
                SqliteWorld.SetPosition(work, catalog, workShipId, station.Position, Home, fixture.Clock.Now);
                var target = work.Add(
                    new WorldNpc
                    {
                        Id = NpcEncounterDefinitions.FirstNpcId + 20_000,
                        SystemId = Home,
                        TypeId = pirate,
                        Role = NpcRole.Pirate,
                        X = station.Position.X,
                        Y = station.Position.Y,
                        Z = station.Position.Z,
                        UpdatedAt = fixture.Clock.Now,
                    }
                ).Entity;
                var kill = NpcDamagePersistence.Apply(
                    work,
                    catalog,
                    target,
                    pilot,
                    workShipId,
                    new Dictionary<DogmaAttributeId, double>
                    {
                        [DogmaAttributeId.StructureHitPoints] = 20,
                        [DogmaAttributeId.EntityKillBounty] = bountyIsk,
                    },
                    new DamageVector(100, 0, 0, 0),
                    fixture.Clock.Now
                );
                Assert.True(kill.Destroyed);
                Assert.Null(kill.Reward);
                Assert.Equal(openingCents, work.Set<PilotWallet>().Find(pilotId)!.Cents);
                ships.Dock(work, catalog, pilotId, workShipId, Depot);
                work.Commit();
            }
            fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(fixture.Database.Settings.Rewards.BountyTickSeconds));
            host.GetRequiredService<IActivityRewardTicks>().Advance();
            using (var work = live.Economy.Begin())
            {
                var bounty = Assert.Single(work.Filter<NpcBounty>(row => row.PilotId == pilotId));
                Assert.Equal(bountyIsk * MarketDefinitions.CentsPerIsk, bounty.AmountCents);
                fundedBalance = openingCents + bounty.AmountCents - bounty.TaxCents;
                Assert.Equal(fundedBalance, work.Set<PilotWallet>().Find(pilotId)!.Cents);
                var docked = ships.FindPilot(work, CapsuleerCommunityStore.MinerAccountId, pilotId)!;

                var usableHulls = PilotSkillReader.UsableTypes(work, catalog, pilotId, [incursus, nextHull]);
                Assert.Equal(incursus, Assert.Single(usableHulls));
                var turn = supply.TryBuyEarnedPvpShip(work, catalog, snapshot, docked, fixture.Clock.Now);
                Assert.Equal(!remoteSupplier, turn.Boarded);
                Assert.Equal(!remoteSupplier, turn.MarketChanged);
                if (remoteSupplier)
                {
                    Assert.Empty(work.Filter<MarketExecution>(row => row.PilotId == pilotId));
                    var quote = supply.FindPvpSupplier(work, catalog, snapshot, docked, new HashSet<int>(), fixture.Clock.Now);
                    var route = Assert.IsType<CapsuleerTrafficRoute>(quote);
                    Assert.Equal(Depot, route.SourceStationId);
                    Assert.Equal(listingStation.Id, route.DestinationStationId);
                    Assert.Equal(new[] { Home, listingStation.SolarSystemId }, route.Systems);
                    Assert.False(CapsuleerIndustry.CanStagePvp(work, catalog, listingStation.SolarSystemId));
                }
                work.Commit();
            }
            if (remoteSupplier)
            {
                using var observer = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
                var outbound = host.GetRequiredService<CapsuleerScheduler>();
                var outboundMaintenance = host.GetRequiredService<CapsuleerMaintenance>();
                outbound.PopulationPrepared(Home);
                bool shopped = false;
                for (int elapsed = 0; elapsed < 1200 && live.Economy.ActiveShip(pilotId, workShipId); elapsed++)
                {
                    fixture.Clock.Seconds++;
                    outboundMaintenance.Tick();
                    if (live.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.Journey is { Kind: TrafficJourneyKind.PvpShopping } trip)
                    {
                        shopped |= trip.DestinationStationId == listingStation.Id && trip.HomeStationId == Depot;
                    }
                }
                Assert.True(outbound.Read().Failures == 0, outbound.Read().LastFailure);
                Assert.True(shopped);
                var returning = Assert.IsType<TrafficJourney>(live.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.Journey);
                Assert.Equal(TrafficJourneyKind.PvpShopping, returning.Kind);
                Assert.Equal(listingStation.Id, returning.SourceStationId);
                Assert.Equal(Depot, returning.DestinationStationId);
            }
            using (var work = live.Economy.Begin())
            {
                paidShipId = work.Pilots.Find(pilotId)!.ActiveShipId;
                Assert.NotEqual(workShipId, paidShipId);
                Assert.Equal(incursus, work.Ships.Find(paidShipId)!.TypeId);
                Assert.Equal(listingStation.Id, work.Ships.Find(workShipId)!.StationId);
                Assert.Equal(paidShipId, CapsuleerCorporations.Organization(work, pilotId).EarnedPvpShipId);
                Assert.True(CapsuleerIndustry.HasPvpShip(work, pilotId, paidShipId));
                Assert.False(CapsuleerIndustry.HasPvpShip(work, pilotId, workShipId));

                var guns = work.FittedItems(pilotId, paidShipId).Where(item => item.TypeId == railgun && item.Online).ToArray();
                Assert.Equal(2, guns.Length);
                Assert.All(
                    guns,
                    gun =>
                    {
                        var ammo = Assert.Single(work.Filter<OwnedItem>(item => item.LocationId == gun.Id && item.TypeId == charge));
                        Assert.Equal(magazine, ammo.Quantity);
                    }
                );
                Assert.Contains(work.FittedItems(pilotId, paidShipId), item => item.TypeId == scrambler && item.Online);
                Assert.Contains(
                    work.FittedItems(pilotId, paidShipId),
                    item => item.TypeId == repairer && item.FlagId == repairSlot && item.Online
                );
                var purchases = work.Filter<MarketExecution>(row => row.PilotId == pilotId).ToArray();
                Assert.Equal(parts.Count, purchases.Length);
                Assert.DoesNotContain(purchases, row => row.TypeId == nextHull);
                Assert.All(
                    purchases,
                    row =>
                    {
                        Assert.True(row.IsBuy);
                        Assert.Equal(listingStation.Id, row.StationId);
                        Assert.Equal(listingStation.OwnerId, row.CounterpartyId);
                        var stock = work.Set<NpcMarketOrder>().Find(row.OrderId)!;
                        Assert.Equal(NpcMarketKind.Standard, stock.Kind);
                        Assert.Equal(orders.Single(order => order.TypeId == row.TypeId).Remaining - row.Quantity, stock.Remaining);
                    }
                );
                Assert.Equal(1, purchases.Single(row => row.TypeId == incursus).Quantity);
                Assert.Equal(2, purchases.Single(row => row.TypeId == railgun).Quantity);
                walletAfterPurchase = fundedBalance - purchases.Sum(row => row.Quantity * row.PriceCents);
                Assert.Equal(walletAfterPurchase, work.Set<PilotWallet>().Find(pilotId)!.Cents);
                Assert.True(walletAfterPurchase >= openingCents);
                purchaseIds = purchases.Select(row => row.Id).Order().ToArray();
                work.Commit();
            }
            var otherHull = Assert.Single(market.Orders(pilotId, nextHull), order => !order.Bid && order.StationId == listingStation.Id);
            Assert.Equal((priceCents / 2, 1), (otherHull.PriceCents, otherHull.Remaining));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using (var review = restored.Economy.Begin())
        {
            var current = reopened.GetRequiredService<SqliteWorld>().FindPilot(review, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
            Assert.Equal(paidShipId, current.ShipId);
            Assert.Equal(paidShipId, CapsuleerCorporations.Organization(review, pilotId).EarnedPvpShipId);
            var supply = reopened.GetRequiredService<CapsuleerIndustry>();
            if (progress)
            {
                var goal = Assert.IsType<CapsuleerPvpFitGoal>(CapsuleerCorporations.Organization(review, pilotId).PvpFitGoal);
                Assert.Equal(nextHull, goal.HullTypeId);
                var wallet = review.Set<PilotWallet>().Find(pilotId)!;
                wallet.Cents = openingCents;
                Assert.DoesNotContain(nextHull, PilotSkillReader.UsableTypes(review, catalog, pilotId, [nextHull]));
                PilotTrainingStore.CreditSilentFreePoints(review, pilotId, 20_000);
                for (
                    int attempt = 0;
                    attempt < 8 && !PilotSkillReader.UsableTypes(review, catalog, pilotId, [nextHull]).Contains(nextHull);
                    attempt++
                )
                {
                    Assert.False(supply.TryBuyEarnedPvpShip(review, catalog, snapshot, current, fixture.Clock.Now).Boarded);
                }
                Assert.Contains(nextHull, PilotSkillReader.UsableTypes(review, catalog, pilotId, [nextHull]));
                Assert.False(supply.TryBuyEarnedPvpShip(review, catalog, snapshot, current, fixture.Clock.Now).Boarded);
                Assert.Equal(paidShipId, review.Pilots.Find(pilotId)!.ActiveShipId);
                Assert.Equal(openingCents, wallet.Cents);
                wallet.Cents = walletAfterPurchase;
                Assert.True(supply.TryBuyEarnedPvpShip(review, catalog, snapshot, current, fixture.Clock.Now).Boarded);
                var upgraded = review.Pilots.Find(pilotId)!.ActiveShipId;
                Assert.Equal(nextHull, review.Ships.Find(upgraded)!.TypeId);
                Assert.Equal(Depot, review.Ships.Find(paidShipId)!.StationId);
                var organization = CapsuleerCorporations.Organization(review, pilotId);
                Assert.Equal(upgraded, organization.EarnedPvpShipId);
                Assert.Equal(workShipId, organization.PvpWorkShipId);
                Assert.Null(organization.PvpFitGoal);
                Assert.True(wallet.Cents >= openingCents);
                review.Commit();
                return;
            }
            var repeated = supply.TryBuyEarnedPvpShip(review, catalog, snapshot, current, fixture.Clock.Now);
            Assert.False(repeated.Boarded);
            Assert.False(repeated.MarketChanged);
            Assert.Equal(walletAfterPurchase, review.Set<PilotWallet>().Find(pilotId)!.Cents);
            var restoredPurchases = review.Filter<MarketExecution>(row => row.PilotId == pilotId).Select(row => row.Id).Order();
            Assert.Equal(purchaseIds, restoredPurchases);
            review.Commit();
        }

        using var human = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
        var maintenance = reopened.GetRequiredService<CapsuleerMaintenance>();
        scheduler.PopulationPrepared(Home);
        int patrolBudget = remoteSupplier ? 1200 : 120;
        for (
            int turn = 0;
            turn < patrolBudget && restored.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.Ratting?.Patrol is null;
            turn++
        )
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
        var launched = Assert.IsType<HaulerObservation>(scheduler.Hauler(pilotId));
        Assert.Equal(PatrolKind.Roam, launched.Work.Ratting?.Patrol?.Kind);
        Assert.Equal(paidShipId, launched.Pilot.ShipId);
        Assert.Equal(Home, launched.Pilot.SystemId);
        Assert.NotNull(launched.Pilot.Position);
        if (remoteSupplier)
        {
            return;
        }

        long capsuleId;
        using (var loss = restored.Economy.Begin())
        {
            capsuleId = DestroyBotShip(loss, catalog, pilotId, fixture.Clock.Now);
            Assert.NotEqual(paidShipId, capsuleId);
            Assert.Equal(capsuleId, loss.Pilots.Find(pilotId)!.ActiveShipId);
            Assert.Equal(Depot, loss.Ships.Find(workShipId)!.StationId);
            loss.Commit();
        }

        bool headedToWorkShip = false;
        for (int turn = 0; turn < 600 && !restored.Economy.ActiveShip(pilotId, workShipId); turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
            if (
                restored.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work
                    is { ShipId: var ship, Journey: { Kind: TrafficJourneyKind.CapsuleEscape, DestinationStationId: Depot } }
                && ship == capsuleId
            )
            {
                headedToWorkShip = true;
            }
        }
        Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
        Assert.True(headedToWorkShip);
        Assert.True(restored.Economy.ActiveShip(pilotId, workShipId));
        restored.Economy.Read(world =>
        {
            var destroyed = Assert.Single(world.Filter<ShipLoss>(row => row.ShipId == paidShipId));
            Assert.Equal(capsuleId, destroyed.CapsuleId);
            Assert.Null(world.Ships.Find(paidShipId));
            var organization = CapsuleerCorporations.Organization(world, pilotId);
            Assert.Null(organization.EarnedPvpShipId);
            Assert.Null(organization.PvpWorkShipId);
            Assert.Empty(world.Filter<SavedPersonalOrder>(row => row.PilotId == pilotId && row.Status == PersonalOrderStatus.Open));
            return 0;
        });
    }
}
