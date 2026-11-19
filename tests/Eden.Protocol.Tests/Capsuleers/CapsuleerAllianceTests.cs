using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Protocol.Tests.Sovereignty;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Alliances;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerAllianceTests
{
    private const int HeadquartersSystem = 30000141;
    private const int ObserverSystem = 30000142;
    private const int WingSystem = 31000001;
    private const int ThirdSystem = 31000002;
    private const int FourthSystem = 31000003;
    private const long Observer = 90000001;
    private const long PersonalContribution = 10000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem(bool expedition)
    {
        const int station = 60015157;
        const int hubType = EntosisRules.HubTypeId;
        const int target = SovereigntyCaptureFixture.SystemId;
        const long neutralHub = StructureRules.FirstStructureId + 1;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var startup = configuration.Read();
        var draft = startup.Active.Draft;
        configuration.Save(
            startup.Generation,
            draft with
            {
                Settings = draft.Settings with
                {
                    Economy = draft.Settings.Economy with
                    {
                        ProgressiveMarkets = false,
                        StandardMarket = false,
                        ChargeServiceCosts = false,
                    },
                    Capsuleers = draft.Settings.Capsuleers with { TrafficMultiplier = 6 },
                },
            },
            "First claim fixture"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock))
        {
            provisioner.EnsureSystem(WingSystem);
        }
        PassiveCatalogFixture.LoadSovereignty(fixture.Database.CatalogPath);
        if (expedition)
        {
            PassiveCatalogFixture.LoadUtilityHull(fixture.Database.CatalogPath, 81008);
        }
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3732,'Empire Control',257);
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3732,275,5);
                DELETE FROM Stargate WHERE SolarSystemId={target};
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                    (55000001,{HeadquartersSystem},16,10,3500,100000000000,0,0),
                    (55000002,{target},16,10,3500,200000000000,0,0);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    (55000001,{HeadquartersSystem},55000002),(55000002,{target},55000001);
                """
            );
        }
        int corporation;
        long sellerPackage;
        long bidId;
        long price;
        long budget;
        long reserve = AllianceBillingRules.Amount(CapsuleerAllianceRules.FoundingCorporations);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var maintenance = host.GetRequiredService<CorporationMaintenance>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            price = CapsuleerIndustry.ReferencePrice(catalog, hubType);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                long ceo;
                (corporation, ceo) = Charter(setup, catalog, HeadquartersSystem, "First Claim Works", "FCWK", fixture.Clock.Now);
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<PilotWallet>().Find(ceo)!.Cents = AllianceRules.CreationFeeCents;
                setup.Add(
                    new PilotSkill
                    {
                        PilotId = ceo,
                        TypeId = AllianceRules.EmpireControlSkill,
                        Points = TrainedSkill.PointsForLevel(5, AllianceRules.RequiredEmpireControlLevel),
                    }
                );
                var alliance = host.GetRequiredService<AllianceStore>()
                    .Create(setup, catalog, ceo, new("Claim Coalition", "CLMC", "", ""), fixture.Clock.Now)
                    .Alliance;
                var partner = Charter(setup, catalog, WingSystem, "Neighbor Works", "NWBK", fixture.Clock.Now);
                setup.Add(
                    new SavedAllianceMember
                    {
                        CorporationId = partner.Id,
                        AllianceId = alliance.Id,
                        JoinedAt = fixture.Clock.Now,
                    }
                );
                setup.Pilots.Find(Observer)!.ActiveShip.StationId = station;
                sellerPackage = setup.NextItemId();
                setup.Add(
                    new OwnedItem
                    {
                        Id = sellerPackage,
                        OwnerId = Observer,
                        TypeId = hubType,
                        Quantity = 1,
                        LocationId = station,
                        FlagId = (int)InventoryFlag.Hangar,
                    }
                );
                budget = price + PilotMarketStore.SupplyBrokerFee(setup, catalog, ceo, station, price, 1) + reserve;
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = budget;
                setup.Add(
                    new SavedStructure
                    {
                        Id = neutralHub,
                        TypeId = hubType,
                        Name = "Neutral Sovereignty Hub",
                        CorporationId = EntosisRules.NeutralCorporationId,
                        SystemId = target,
                        State = StructureState.ShieldVulnerable,
                    }
                );
                setup.Commit();
            }
            maintenance.Tick();
            Assert.Empty(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));

            using var occupied = host.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
            maintenance.Tick();
            Assert.Empty(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));
            using (var unfund = live.Economy.Begin(TransactionParticipants.None))
            {
                Assert.Equal(budget, unfund.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                unfund.Remove(unfund.Set<SavedStructure>().Find(neutralHub)!);
                unfund.Set<SavedCorporation>().Find(corporation)!.BalanceCents = price + reserve - 1;
                unfund.Commit();
            }
            fixture.Clock.Seconds += 16;
            maintenance.Tick();
            Assert.Empty(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));
            using (var fund = live.Economy.Begin(TransactionParticipants.None))
            {
                Assert.Equal(price + reserve - 1, fund.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                fund.Set<SavedCorporation>().Find(corporation)!.BalanceCents = budget;
                fund.Commit();
            }

            fixture.Clock.Seconds += 16;
            maintenance.Tick();
            var order = Assert.Single(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));
            Assert.Equal(
                (corporation, station, hubType, true, 1, price),
                (order.CorporationId, order.StationId, order.TypeId, order.Bid, order.Remaining, order.EscrowCents)
            );
            bidId = order.Id;
            Assert.Equal(reserve, live.Economy.Read(world => world.Set<SavedCorporation>().Find(corporation)!.BalanceCents));
            await live.FlushAsync();
        }

        using (var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            using var observing = reopened.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
            LiveWorld restored = reopened.GetRequiredService<LiveWorld>();
            using (var deposit = restored.Economy.Begin(TransactionParticipants.None))
            {
                Assert.Equal(reserve, deposit.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                // Fund a second hub to prove the open order prevents another purchase.
                deposit.Set<SavedCorporation>().Find(corporation)!.BalanceCents = budget;
                deposit.Commit();
            }
            reopened.GetRequiredService<CorporationMaintenance>().Tick();
            Assert.Equal(bidId, Assert.Single(restored.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray())).Id);
            reopened.GetRequiredService<IPilotMarket>().Sell(Observer, [new(sellerPackage, station, hubType, price, 1)]);
            await restored.FlushAsync();
        }

        using var delivered = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var present = delivered.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
        delivered.GetRequiredService<CorporationMaintenance>().Tick();
        delivered
            .GetRequiredService<LiveWorld>()
            .Economy.Read(world =>
            {
                var bid = Assert.Single(world.Filter<SavedPersonalOrder>(order => order.CorporationId == corporation));
                Assert.Equal(bidId, bid.Id);
                Assert.Equal(0, bid.Remaining);
                Assert.Equal(0, bid.EscrowCents);
                var hub = Assert.Single(world.Filter<OwnedItem>(item => item.OwnerId == bid.PilotId && item.TypeId == hubType));
                Assert.Equal(
                    (hubType, (long)station, (int)InventoryFlag.Hangar, 1),
                    (hub.TypeId, hub.LocationId, hub.FlagId, hub.Quantity)
                );
                Assert.Equal(budget, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                return true;
            });
        if (expedition)
        {
            await ExerciseExpedition(fixture, corporation, station, delivered, present);
        }
    }

    private static async Task ExerciseExpedition(
        CapsuleerHaulerTests.Fixture fixture,
        int corporation,
        int station,
        ServiceProvider delivered,
        IHumanPresenceLease present
    )
    {
        const int squall = CapsuleerAllianceRules.ExpeditionSquall;
        const int strontium = SovereigntyCaptureFixture.Strontium;
        const int link = CapsuleerAllianceRules.ExpeditionLink;
        const int fuel = 4;
        const int target = SovereigntyCaptureFixture.SystemId;
        const long unitPrice = 1_000;
        long courier = 0;
        var deliveryWorld = delivered.GetRequiredService<LiveWorld>();
        List<MarketSale> sales = [];
        long budget;
        using (var setup = deliveryWorld.Economy.Begin(TransactionParticipants.None))
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            setup.Set<PilotWallet>().Find(Observer)!.Cents = 1_000_000;
            budget = setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents;
            foreach (var (type, quantity) in new[] { (squall, 1), (link, 1), (strontium, fuel) })
            {
                var stock = InventoryPersistence.MergeOutput(setup, catalog, Observer, station, (int)InventoryFlag.Hangar, type, quantity);
                sales.Add(new(stock.Item.Id, station, type, unitPrice, quantity));
            }
            setup.Commit();
        }
        delivered.GetRequiredService<IPersonalMarket>().PlaceSell(Observer, sales, 7);
        Advance(
            delivered,
            () => deliveryWorld.HaulerCheckpoints.Values.Any(checkpoint => checkpoint.Work.SovereigntyExpedition is { HubId: null }),
            300
        );
        var checkpoint = Assert.Single(deliveryWorld.HaulerCheckpoints.Values, item => item.Work.SovereigntyExpedition is not null);
        courier = checkpoint.PilotId;
        var underway = checkpoint.Work;
        long shipId = underway.ShipId;
        Assert.Equal(new[] { HeadquartersSystem, target }, underway.Journey!.Systems);
        deliveryWorld.Economy.Read(world =>
        {
            var cargo = Assert.IsType<OwnedItem>(world.Set<OwnedItem>().Find(underway.SovereigntyExpedition!.CargoId));
            Assert.Equal(
                (courier, shipId, EntosisRules.HubTypeId, (int)InventoryFlag.InfrastructureHold),
                (cargo.OwnerId, cargo.LocationId, cargo.TypeId, cargo.FlagId)
            );
            Assert.Null(world.Set<SavedSovereigntyHub>().Find(target));
            Assert.Equal(squall, world.Pilots.Find(courier)!.ActiveShip.TypeId);
            Assert.Equal(budget - unitPrice * (fuel + 2), world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
            Assert.Equal(
                3,
                world
                    .Filter<MarketExecution>(execution => execution.PilotId == courier)
                    .Count(execution => execution.IsBuy && execution.TypeId != EntosisRules.HubTypeId)
            );
            return true;
        });

        await deliveryWorld.FlushAsync();
        present.Dispose();
        delivered.Dispose();
        using var claiming = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var observer = claiming.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
        var claimWorld = claiming.GetRequiredService<LiveWorld>();
        var claims = claiming.GetRequiredService<ISovereignty>();
        bool Paid() => claimWorld.Economy.Read(world => world.Filter<SavedEntosisCycle>(cycle => cycle.PilotId == courier).Any());
        Advance(claiming, Paid, 500);
        Assert.Null(claims.Read(target));
        claimWorld.Economy.Read(world =>
        {
            var hub = Assert.IsType<SavedSovereigntyHub>(world.Set<SavedSovereigntyHub>().Find(target));
            var structure = world.Set<SavedStructure>().Find(hub.HubId)!;
            Assert.Equal(EntosisRules.NeutralCorporationId, structure.CorporationId);
            Assert.Null(world.Set<OwnedItem>().Find(underway.SovereigntyExpedition!.CargoId));
            Assert.Equal(
                fuel - 1,
                world.Filter<OwnedItem>(item => item.OwnerId == courier).Single(item => item.TypeId == strontium).Quantity
            );
            return true;
        });
        observer.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        var neutral = claiming.GetRequiredService<IPilotEntosis>().Hub(target)!;
        Assert.Null(neutral.Claim);
        Assert.Equal(0, neutral.Capture.ProgressTicks);
        using var resumed = claiming.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
        Advance(claiming, () => claims.Read(target) is not null, 1600);
        Assert.Equal(corporation, claims.Read(target)!.CorporationId);
        claimWorld.Economy.Read(world =>
        {
            Assert.Empty(world.Filter<OwnedItem>(item => item.OwnerId == courier).Where(item => item.TypeId == strontium));
            return true;
        });
        Advance(claiming, () => claimWorld.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.SovereigntyExpedition is null, 500);
        ExerciseHomeOperations(fixture, claiming, station, courier, Advance);
        var home = claims.Read(target)!;
        var paid = claimWorld.Economy.Read(world =>
            world.Filter<SavedAllianceBill>(bill => bill.AllianceId == home.AllianceId).Single(bill => bill.PaidAt != null)
        );
        long treasury = claimWorld.Economy.Read(world => world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
        long reagentStock = claimWorld.Economy.Read(world =>
            world.Filter<SavedSovereigntyFuel>(fuel => fuel.HubId == home.HubId).Single().Amount
        );
        await claimWorld.FlushAsync();
        resumed.Dispose();
        claiming.Dispose();
        using var finished = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        finished.GetRequiredService<CorporationMaintenance>().Tick();
        Assert.Equal(home, finished.GetRequiredService<ISovereignty>().Read(target));
        finished
            .GetRequiredService<LiveWorld>()
            .Economy.Read(world =>
            {
                Assert.Equal(treasury, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                Assert.Equal(paid.PaidAt, world.Set<SavedAllianceBill>().Find(paid.Id)!.PaidAt);
                Assert.Equal(reagentStock, world.Filter<SavedSovereigntyFuel>(fuel => fuel.HubId == home.HubId).Single().Amount);
                return true;
            });

        void Advance(ServiceProvider host, Func<bool> ready, int seconds)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(HeadquartersSystem);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (int step = 0; step < seconds && !ready(); step++)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            var observation = scheduler.Hauler(courier);
            var position = observation?.Pilot.Position;
            if (ready())
            {
                return;
            }
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            var waypoint = observation is null
                ? null
                : SystemObjects.Geometry(catalog, observation.Pilot.SystemId).FirstOrDefault(body => body.Id == observation.Work.TargetId);
            Assert.Fail(
                $"{observation?.Pilot.SystemId} {observation?.Work.SovereigntyExpedition} "
                    + $"{observation?.Work.Activity} to {observation?.Work.TargetId} at ({position?.X},{position?.Y},{position?.Z}) "
                    + $"waypoint ({waypoint?.Position.X},{waypoint?.Position.Y},{waypoint?.Position.Z}) "
                    + $"due {observation?.Work.DueTicks} through {observation?.Work.ActiveTicks} "
                    + $"speed {observation?.Pilot.Flight.MaxSpeed}, warp ready {observation?.Work.WarpReadyAt}, "
                    + $"retreating {observation?.Work.Retreating} {observation?.Work.WaitingReason} {scheduler.Read().LastFailure}"
            );
        }
    }

    private static void ExerciseHomeOperations(
        CapsuleerHaulerTests.Fixture fixture,
        ServiceProvider host,
        int station,
        long courier,
        Action<ServiceProvider, Func<bool>, int> advance
    )
    {
        const int target = SovereigntyCaptureFixture.SystemId;
        const int strontium = SovereigntyCaptureFixture.Strontium;
        const int upgradeType = 81615;
        const long price = 100;
        var live = host.GetRequiredService<LiveWorld>();
        var claims = host.GetRequiredService<ISovereignty>();
        var claim = claims.Read(target)!;
        var entosis = host.GetRequiredService<IPilotEntosis>();
        var resources = host.GetRequiredService<ISovereigntyResources>();
        var maintenance = host.GetRequiredService<CorporationMaintenance>();
        var upgrade = resources.Definitions().Single(item => item.TypeId == upgradeType);
        int delivery = checked((int)(upgrade.StartupFuel + 4 * upgrade.HourlyFuel));
        int reagent = upgrade.FuelTypeId;
        List<MarketSale> sales = [];
        using (var setup = live.Economy.Begin(TransactionParticipants.None))
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            var conflict = setup.Set<SavedSovereigntyConflict>().Find(target)!;
            conflict.Value = 0.2;
            conflict.UpdatedAt = fixture.Clock.Now;
            foreach (var (type, quantity) in new[] { (strontium, 12), (reagent, delivery) })
            {
                var stock = InventoryPersistence.MergeOutput(setup, catalog, Observer, station, (int)InventoryFlag.Hangar, type, quantity);
                sales.Add(new(stock.Item.Id, station, type, price, quantity));
            }
            setup.Commit();
        }
        host.GetRequiredService<IPersonalMarket>().PlaceSell(Observer, sales, 7);
        bool PaidDefense(long id) =>
            live.Economy.Read(world =>
                world
                    .Filter<SavedEntosisCycle>(cycle => cycle.HubId == id)
                    .Any(cycle => cycle.AllianceId == claim.AllianceId && cycle.EndsAt > fixture.Clock.Now)
            );
        advance(host, () => PaidDefense(claim.HubId), 1400);
        advance(host, () => entosis.Hub(target)!.Conflict!.Capture.Value == 1, 1200);
        advance(host, () => live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.SovereigntyExpedition is null, 600);
        Assert.Equal(claim, claims.Read(target));

        long node;
        using (var campaign = live.Economy.Begin(TransactionParticipants.None))
        {
            var conflict = campaign.Set<SavedSovereigntyConflict>().Find(target)!;
            conflict.Phase = SovereigntyPhase.Campaign;
            conflict.CampaignAt = fixture.Clock.Now;
            conflict.UpdatedAt = fixture.Clock.Now;
            conflict.DefenderPoints = 100 - SovereigntyWarfareRules.NodePoints;
            conflict.NextSpawnAt = fixture.Clock.Now + TimeSpan.TicksPerHour;
            node = campaign.NextItemId();
            campaign.Add(
                new SavedSovereigntyNode
                {
                    Id = node,
                    SystemId = target,
                    SourceSystemId = target,
                    HubId = claim.HubId,
                    CorporationId = claim.CorporationId,
                    AllianceId = claim.AllianceId,
                    Value = -0.8,
                    X = 300_000_000_000,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            campaign.Commit();
        }
        advance(host, () => PaidDefense(node), 1600);
        advance(host, () => entosis.Hub(target)!.Conflict!.DefenderPoints == 100, 1500);
        advance(host, () => live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.SovereigntyExpedition is null, 600);
        Assert.Empty(entosis.CommandNodes(target));
        Assert.Equal(claim, claims.Read(target));

        long beforeFuel;
        using (var setup = live.Economy.Begin(TransactionParticipants.None))
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            var planet = SovereigntyResourceCatalog.System(catalog, target).Planets.First(planet => planet.Workforce >= upgrade.Workforce);
            long hook = setup.NextItemId();
            setup.Add(
                new SavedStructure
                {
                    Id = hook,
                    TypeId = SovereigntyResourceRules.SkyhookTypeId,
                    Name = "Fixture workforce skyhook",
                    CorporationId = claim.CorporationId,
                    SystemId = target,
                    State = StructureState.ShieldVulnerable,
                }
            );
            setup.Add(
                new SavedSovereigntySkyhook
                {
                    Id = hook,
                    SystemId = target,
                    PlanetId = planet.PlanetId,
                    CorporationId = claim.CorporationId,
                    Active = true,
                    OnlineAt = fixture.Clock.Now,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            setup.Add(
                new SavedSovereigntyUpgrade
                {
                    Id = setup.Next("WorldSovereigntyUpgrade"),
                    HubId = claim.HubId,
                    SystemId = target,
                    TypeId = upgradeType,
                    ConfiguredOnline = true,
                    PowerState = SovereigntyUpgradePower.Pending,
                }
            );
            SovereigntyResourcePersistence.Refresh(setup, catalog, fixture.Clock.Now);
            beforeFuel = setup.Set<SavedCorporation>().Find(claim.CorporationId)!.BalanceCents;
            setup.Commit();
        }
        advance(
            host,
            () =>
                live.Economy.Read(world =>
                    world
                        .Filter<SavedSovereigntyFuel>(fuel => fuel.HubId == claim.HubId)
                        .Any(fuel => fuel.TypeId == reagent && fuel.Amount > 0)
                ),
            1600
        );
        live.Economy.Read(world =>
        {
            Assert.Equal(beforeFuel - price * delivery, world.Set<SavedCorporation>().Find(claim.CorporationId)!.BalanceCents);
            var fuel = Assert.Single(world.Filter<SavedSovereigntyFuel>(fuel => fuel.HubId == claim.HubId));
            Assert.Equal(4 * upgrade.HourlyFuel, fuel.Amount);
            Assert.True(Assert.Single(world.Filter<SavedSovereigntyUpgrade>(row => row.HubId == claim.HubId)).StartupPaid);
            return true;
        });

        maintenance.Tick();
        var bill = live.Economy.Read(world => world.Filter<SavedAllianceBill>(bill => bill.AllianceId == claim.AllianceId).Single());
        long beforeBill = beforeFuel - price * delivery;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(bill.DueAt - fixture.Clock.Now));
        maintenance.Tick();
        maintenance.Tick();
        live.Economy.Read(world =>
        {
            Assert.Equal(beforeBill - bill.AmountCents, world.Set<SavedCorporation>().Find(claim.CorporationId)!.BalanceCents);
            var paid = world.Set<SavedAllianceBill>().Find(bill.Id)!;
            Assert.Equal(claim.CorporationId, paid.PaidByCorporationId);
            Assert.Equal(bill.DueAt, paid.PaidAt);
            return true;
        });
    }

    [Fact]
    public async Task FundedNeighborsFormAndGrowAnAllianceThroughDueApplicationsAcrossRestart()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3732,'Empire Control',257);
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3732,275,5);
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                    (55000001,31000002,16,10,3500,0,0,0),(55000002,31000005,16,10,3500,0,0,0),
                    (55000003,31000003,16,10,3500,0,0,0),(55000004,31000005,16,10,3500,0,0,0);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    (55000001,31000002,55000002),(55000002,31000005,55000001),
                    (55000003,31000003,55000004),(55000004,31000005,55000003);
                """
            );
        }
        var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var startup = configuration.Read();
        var draft = startup.Active.Draft;
        configuration.Save(
            startup.Generation,
            draft with
            {
                Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { TrafficMultiplier = 6 } },
            },
            "Alliance fixture"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock))
        {
            foreach (int system in new[] { WingSystem, ThirdSystem, FourthSystem })
            {
                provisioner.EnsureSystem(system);
            }
        }

        (int Id, long Ceo) founder;
        (int Id, long Ceo) wing;
        (int Id, long Ceo) third;
        (int Id, long Ceo) fourth;
        int allianceId;
        long admission;
        long billDue;
        long reserve = AllianceBillingRules.Amount(CapsuleerAllianceRules.FoundingCorporations);
        long shortfall = AllianceRules.CreationFeeCents - PersonalContribution;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var maintenance = host.GetRequiredService<CorporationMaintenance>();
            var alliances = host.GetRequiredService<IAlliances>();
            var applications = host.GetRequiredService<IAllianceMembership>();
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
                founder = Charter(setup, catalog, HeadquartersSystem, "Manufacturing Fleet", "MFCT", fixture.Clock.Now);
                wing = Charter(setup, catalog, WingSystem, "Regional Patrol", "RPAT", fixture.Clock.Now);
                third = Charter(setup, catalog, ThirdSystem, "Supply Fleet", "SFLT", fixture.Clock.Now);
                fourth = Charter(setup, catalog, FourthSystem, "Route Logistics", "RLGS", fixture.Clock.Now);
                setup.Set<PilotWallet>().Find(founder.Ceo)!.Cents = PersonalContribution;
                setup.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents = shortfall + reserve - 1;
                setup.Commit();
            }

            using (host.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem))
            {
                maintenance.Tick();
                Assert.Empty(alliances.List());
                live.Economy.Read(world =>
                {
                    Assert.Null(world.Set<PilotSkill>().Find(founder.Ceo, AllianceRules.EmpireControlSkill));
                    Assert.Empty(world.Set<SavedServicePayment>());
                    Assert.Equal(PersonalContribution, world.Set<PilotWallet>().Find(founder.Ceo)!.Cents);
                    return true;
                });
            }
            using (var funding = live.Economy.Begin(TransactionParticipants.None))
            {
                funding.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents++;
                funding.Commit();
            }
            fixture.Clock.Seconds += 60;
            maintenance.Tick();
            Assert.Empty(alliances.List());

            using var present = host.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
            maintenance.Tick();
            var alliance = Assert.Single(alliances.List());
            allianceId = alliance.Id;
            Assert.Equal(founder.Id, alliance.ExecutorCorporationId);
            Assert.Equal(founder.Ceo, alliance.CreatorId);
            Assert.Equal(founder.Id, Assert.Single(alliances.Members(allianceId)).CorporationId);
            Assert.False(CapsuleerCorporations.CanFlyTogether(live, founder.Ceo, wing.Ceo));
            var accepted = Assert.Single(applications.Applications(wing.Ceo, forAlliance: false));
            Assert.Equal(AllianceApplicationStatus.Accepted, accepted.Status);
            admission = Assert.IsType<long>(accepted.EffectiveAt);
            Assert.Equal(fixture.Clock.Now + AllianceMembershipRules.AdmissionDelay.Ticks, admission);
            billDue = Assert.Single(host.GetRequiredService<IAllianceBilling>().Bills(founder.Ceo)).DueAt;
            live.Economy.Read(world =>
            {
                Assert.Equal(0, world.Set<PilotWallet>().Find(founder.Ceo)!.Cents);
                Assert.Equal(reserve, world.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents);
                Assert.Equal(-shortfall, Assert.Single(world.Filter<SavedWalletLedger>(row => row.OwnerId == founder.Id)).AmountCents);
                var charge = Assert.Single(world.Set<SavedServicePayment>());
                Assert.Equal((WalletEntryType.AllianceRegistration, -AllianceRules.CreationFeeCents), (charge.Kind, charge.AmountCents));
                long points = world.Set<PilotSkill>().Find(founder.Ceo, AllianceRules.EmpireControlSkill)!.Points;
                Assert.Equal(TrainedSkill.PointsForLevel(5, AllianceRules.RequiredEmpireControlLevel), points);
                return true;
            });
            maintenance.Tick();
            Assert.Equal(accepted, Assert.Single(applications.Applications(wing.Ceo, forAlliance: false)));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var worker = reopened.GetRequiredService<CorporationMaintenance>();
        var roster = reopened.GetRequiredService<IAlliances>();
        var membership = reopened.GetRequiredService<IAllianceMembership>();
        using var occupied = reopened.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(admission - 1);
        worker.Tick();
        Assert.Null(roster.Affiliation(wing.Id));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        worker.Tick();
        Assert.Equal(allianceId, roster.PilotAffiliation(wing.Ceo));
        Assert.True(CapsuleerCorporations.CanFlyTogether(restored, founder.Ceo, wing.Ceo));
        Assert.Equal(admission, roster.Members(allianceId).Single(member => member.CorporationId == wing.Id).JoinedAt);

        var manual = membership.Apply(third.Ceo, allianceId, "Awaiting the directors' vote.");
        using (var funding = restored.Economy.Begin(TransactionParticipants.None))
        {
            funding.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents = AllianceBillingRules.Amount(
                CapsuleerAllianceRules.TargetCorporations
            );
            funding.Add(new SavedCapsuleerSuspension { PilotId = fourth.Ceo });
            funding.Commit();
        }
        fixture.Clock.Seconds += 60;
        worker.Tick();
        Assert.Equal(manual, Assert.Single(membership.Applications(third.Ceo, forAlliance: false)));
        membership.Withdraw(third.Ceo, allianceId);
        fixture.Clock.Seconds += 60;
        worker.Tick();
        var thirdApplication = Assert.Single(membership.Applications(third.Ceo, forAlliance: false));
        Assert.Equal(AllianceApplicationStatus.Accepted, thirdApplication.Status);
        using (var enable = restored.Economy.Begin(TransactionParticipants.None))
        {
            enable.Remove(enable.Set<SavedCapsuleerSuspension>().Find(fourth.Ceo)!);
            enable.Commit();
        }
        fixture.Clock.Seconds += 60;
        worker.Tick();
        Assert.Empty(membership.Applications(fourth.Ceo, forAlliance: false));
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(thirdApplication.EffectiveAt!.Value);
        worker.Tick();
        Assert.Equal(new[] { founder.Id, wing.Id, third.Id }, roster.Members(allianceId).Select(member => member.CorporationId));

        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(billDue);
        worker.Tick();
        worker.Tick();
        var bills = reopened.GetRequiredService<IAllianceBilling>().Bills(founder.Ceo);
        Assert.Equal(founder.Id, bills[0].PaidByCorporationId);
        Assert.Equal(AllianceBillingRules.Amount(CapsuleerAllianceRules.TargetCorporations), bills[1].AmountCents);
        restored.Economy.Read(world =>
        {
            Assert.Single(world.Set<SavedServicePayment>(), charge => charge.Kind == WalletEntryType.AllianceRegistration);
            Assert.Single(
                world.Filter<SavedWalletLedger>(row => row.OwnerId == founder.Id),
                row => row.EntryType == WalletEntryType.AllianceMaintenance
            );
            return true;
        });
        await restored.FlushAsync();
    }

    private static (int Id, long Ceo) Charter(
        EconomyTransaction world,
        CatalogDbContext catalog,
        int system,
        string name,
        string ticker,
        long now
    )
    {
        var pilots = world
            .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == system)
            .OrderBy(agent => agent.PilotId)
            .Take(CapsuleerCorporationRules.FoundingRoster)
            .Select(agent => agent.PilotId)
            .ToArray();
        Assert.Equal(CapsuleerCorporationRules.FoundingRoster, pilots.Length);
        long ceo = pilots[0];
        var founder = world.Pilots.Find(ceo)!;
        if (world.Positions.Find(founder.ActiveShipId) is { } position)
        {
            world.Remove(position);
        }
        founder.ActiveShip.StationId = catalog.Definitions.StationsInSystem(system)[0].Id;
        world.Add(
            new PilotSkill
            {
                PilotId = ceo,
                TypeId = CorporationRules.ManagementSkill,
                Points = 250,
            }
        );
        world.Set<PilotWallet>().Find(ceo)!.Cents += CorporationRules.CreationFeeCents;
        int id = PlayerCorporationStore.Create(world, catalog, ceo, PlayerCorporationTests.Details(name, ticker), now).Corporation.Id;
        world.Set<SavedCorporation>().Find(id)!.CapsuleerFoundingSystemId = system;
        foreach (long pilot in pilots.Skip(1))
        {
            CorporationMembershipStore.Move(world, world.Pilots.Find(pilot)!, id, now);
        }
        return (id, ceo);
    }
}
