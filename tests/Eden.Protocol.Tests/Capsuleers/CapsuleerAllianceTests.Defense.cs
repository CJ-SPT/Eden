using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.Sovereignty;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Alliances;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAllianceTests
{
    [Fact]
    public async Task UnattendedDefendersProtectLaterClaimAndDivideCampaignNodesAcrossRestart()
    {
        const int oldestSystem = 30003990;
        const int laterSystem = SovereigntyCaptureFixture.SystemId;
        const int fuel = 16;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 16, 18, 15, 0, TimeSpan.Zero);
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
                    Capsuleers = draft.Settings.Capsuleers with
                    {
                        TrafficMultiplier = 6,
                        AutomaticAlliances = false,
                        RunWithoutPlayers = true,
                        NewSovereigntyClaims = false,
                        RegionalConflicts = false,
                    },
                },
            },
            "Multiple held claims fixture"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        PassiveCatalogFixture.LoadSovereignty(fixture.Database.CatalogPath);
        PassiveCatalogFixture.LoadUtilityHull(fixture.Database.CatalogPath, CapsuleerAllianceRules.ExpeditionSquall);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3732,'Empire Control',257);
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3732,275,5);
                DELETE FROM Stargate WHERE SolarSystemId IN ({oldestSystem},{laterSystem});
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                    (55000001,{HeadquartersSystem},16,10,3500,100000000000,0,0),
                    (55000002,{laterSystem},16,10,3500,200000000000,0,0),
                    (55000003,{HeadquartersSystem},16,10,3500,200000000000,0,0),
                    (55000004,{oldestSystem},16,10,3500,200000000000,0,0);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    (55000001,{HeadquartersSystem},55000002),(55000002,{laterSystem},55000001),
                    (55000003,{HeadquartersSystem},55000004),(55000004,{oldestSystem},55000003);
                """
            );
        }

        int corporation;
        int alliance;
        int headquarters;
        long first;
        long second;
        SovereigntyClaim oldest;
        SovereigntyClaim later;
        long firstNode;
        long secondNode;
        int firstBeforeCampaign;
        SovereigntyExpedition reserved;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var claims = host.GetRequiredService<ISovereignty>();
            var entosis = host.GetRequiredService<IPilotEntosis>();
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            {
                var charter = Charter(setup, catalog, HeadquartersSystem, "Border Defense Works", "BDCW", fixture.Clock.Now);
                corporation = charter.Id;
                headquarters = setup.Set<SavedCorporation>().Find(corporation)!.StationId;
                setup.Set<PilotWallet>().Find(charter.Ceo)!.Cents = AllianceRules.CreationFeeCents;
                setup.Add(
                    new PilotSkill
                    {
                        PilotId = charter.Ceo,
                        TypeId = AllianceRules.EmpireControlSkill,
                        Points = TrainedSkill.PointsForLevel(5, AllianceRules.RequiredEmpireControlLevel),
                    }
                );
                alliance = host.GetRequiredService<AllianceStore>()
                    .Create(setup, catalog, charter.Ceo, new("Border Defense", "BDFX", "", ""), fixture.Clock.Now)
                    .Alliance.Id;
                var couriers = setup
                    .Set<SavedCapsuleerHauler>()
                    .AsEnumerable()
                    .Where(row => setup.Pilots.Find(row.PilotId)!.CorporationId == corporation)
                    .OrderBy(row => row.PilotId)
                    .Take(2)
                    .ToArray();
                Assert.Equal(2, couriers.Length);
                first = couriers[0].PilotId;
                second = couriers[1].PilotId;
                foreach (var courier in couriers)
                {
                    Fit(setup, catalog, courier);
                }
                foreach (var agent in setup.Set<SavedCapsuleerAgent>().Where(agent => agent.PilotId != first).ToArray())
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                AddHub(setup, catalog, oldestSystem, fixture.Clock.Now - TimeSpan.TicksPerDay);
                AddHub(setup, catalog, laterSystem, fixture.Clock.Now);
                setup.Commit();
            }

            oldest = Assert.IsType<SovereigntyClaim>(claims.Read(oldestSystem));
            later = Assert.IsType<SovereigntyClaim>(claims.Read(laterSystem));
            Assert.True(oldest.ClaimedAt < later.ClaimedAt);
            Assert.Equal(1d, entosis.Hub(oldestSystem)!.Conflict!.Capture.Value);
            Assert.Equal(1d, entosis.Hub(laterSystem)!.Conflict!.Capture.Value);
            using (var disruption = live.Economy.Begin(TransactionParticipants.None))
            {
                // Start from a persisted, disrupted secondary claim. The recovery and fuel spend use native Entosis.
                var conflict = disruption.Set<SavedSovereigntyConflict>().Find(laterSystem)!;
                conflict.Value = 0.2;
                conflict.UpdatedAt = fixture.Clock.Now;
                disruption.Commit();
            }

            StartDefense(host);
            Advance(host, 600, () => ActiveCycle(host, first)?.HubId == later.HubId);
            var hubCycle = Assert.IsType<EntosisCycle>(ActiveCycle(host, first));
            var hubDefense = Assert.IsType<SovereigntyExpedition>(ActiveDefense(live, first));
            Assert.Equal(
                (SovereigntyDuty.Defend, (int?)laterSystem, laterSystem),
                (hubDefense.Duty, hubDefense.HomeSystemId, hubDefense.TargetSystemId)
            );
            Assert.Equal(fuel - 1, Fuel(live, hubCycle.ShipId));
            Advance(host, 1400, () => entosis.Hub(laterSystem)!.Conflict!.Capture.Value == 1);
            Assert.Equal(1d, entosis.Hub(oldestSystem)!.Conflict!.Capture.Value);
            Assert.Equal(oldest, claims.Read(oldestSystem));
            Assert.Equal(later, claims.Read(laterSystem));
            WaitForReturn(host, [first]);
            firstBeforeCampaign = Fuel(live, hubCycle.ShipId);
            Assert.InRange(firstBeforeCampaign, fuel - 3, fuel - 2);

            using (var campaign = live.Economy.Begin(TransactionParticipants.None))
            {
                // Start two captures from the same source claim. One node is physically in the other held system.
                var conflict = campaign.Set<SavedSovereigntyConflict>().Find(laterSystem)!;
                conflict.Phase = SovereigntyPhase.Campaign;
                conflict.CampaignAt = fixture.Clock.Now;
                conflict.UpdatedAt = fixture.Clock.Now;
                conflict.DefenderPoints = 100 - 2 * SovereigntyWarfareRules.NodePoints;
                conflict.NextSpawnAt = fixture.Clock.Now + TimeSpan.TicksPerHour;
                firstNode = AddNode(campaign, later, oldestSystem, -0.8);
                secondNode = AddNode(campaign, later, laterSystem, -0.7);
                campaign.Commit();
            }

            Advance(host, 180, () => ActiveDefense(live, first) is { Returning: false });
            reserved = Assert.IsType<SovereigntyExpedition>(ActiveDefense(live, first));
            Assert.Equal(
                ((long?)firstNode, (int?)laterSystem, oldestSystem),
                (reserved.HubId, reserved.HomeSystemId, reserved.TargetSystemId)
            );
            Assert.Empty(entosis.Cycles(first));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var restoredClaims = reopened.GetRequiredService<ISovereignty>();
        var restoredEntosis = reopened.GetRequiredService<IPilotEntosis>();
        using (var enable = restored.Economy.Begin(TransactionParticipants.None))
        {
            var employer = enable.Set<SavedCorporation>().Find(corporation)!;
            Assert.Equal(reserved, Assert.Single(CapsuleerAlliances.Expeditions(enable, employer, exceptPilot: null)));
            enable.Remove(enable.Set<SavedCapsuleerSuspension>().Find(second)!);
            enable.Commit();
        }
        StartDefense(reopened);
        Advance(reopened, 1200, () => ActiveCycle(reopened, first) is not null && ActiveCycle(reopened, second) is not null);
        var firstPaid = Assert.IsType<EntosisCycle>(ActiveCycle(reopened, first));
        var secondPaid = Assert.IsType<EntosisCycle>(ActiveCycle(reopened, second));
        Assert.Equal((firstNode, secondNode), (firstPaid.HubId, secondPaid.HubId));
        var secondDefense = Assert.IsType<SovereigntyExpedition>(ActiveDefense(restored, second));
        Assert.Equal(
            (SovereigntyDuty.Defend, (int?)laterSystem, laterSystem, (long?)secondNode),
            (secondDefense.Duty, secondDefense.HomeSystemId, secondDefense.TargetSystemId, secondDefense.HubId)
        );
        Assert.True(Fuel(restored, firstPaid.ShipId) < firstBeforeCampaign);
        Assert.True(Fuel(restored, secondPaid.ShipId) < fuel);
        Advance(
            reopened,
            1400,
            () =>
            {
                var conflict = restoredEntosis.Hub(laterSystem)!.Conflict!;
                return conflict.Phase == SovereigntyPhase.Secure && conflict.DefenderPoints == 100;
            }
        );
        WaitForReturn(reopened, [first, second]);
        Assert.Empty(restoredEntosis.CommandNodes(oldestSystem));
        Assert.Empty(restoredEntosis.CommandNodes(laterSystem));
        Assert.Equal(oldest, restoredClaims.Read(oldestSystem));
        Assert.Equal(later, restoredClaims.Read(laterSystem));
        DeliverSecondaryReagent();
        AssertUnattended(reopened);

        void DeliverSecondaryReagent()
        {
            const int upgradeType = 81615;
            var upgrade = reopened.GetRequiredService<ISovereigntyResources>().Definitions().Single(item => item.TypeId == upgradeType);
            int quantity = checked((int)(upgrade.StartupFuel + 4 * upgrade.HourlyFuel));
            using (var supply = restored.Economy.Begin(TransactionParticipants.None))
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            {
                var planet = SovereigntyResourceCatalog
                    .System(catalog, laterSystem)
                    .Planets.First(planet => planet.Workforce >= upgrade.Workforce);
                long hook = supply.NextItemId();
                supply.Add(
                    new SavedStructure
                    {
                        Id = hook,
                        TypeId = SovereigntyResourceRules.SkyhookTypeId,
                        Name = "Secondary claim workforce skyhook",
                        CorporationId = corporation,
                        SystemId = laterSystem,
                        State = StructureState.ShieldVulnerable,
                    }
                );
                supply.Add(
                    new SavedSovereigntySkyhook
                    {
                        Id = hook,
                        SystemId = laterSystem,
                        PlanetId = planet.PlanetId,
                        CorporationId = corporation,
                        Active = true,
                        OnlineAt = fixture.Clock.Now,
                        UpdatedAt = fixture.Clock.Now,
                    }
                );
                supply.Add(
                    new SavedSovereigntyUpgrade
                    {
                        Id = supply.Next("WorldSovereigntyUpgrade"),
                        HubId = later.HubId,
                        SystemId = laterSystem,
                        TypeId = upgradeType,
                        ConfiguredOnline = true,
                        PowerState = SovereigntyUpgradePower.Pending,
                    }
                );
                SovereigntyResourcePersistence.Refresh(supply, catalog, fixture.Clock.Now);
                foreach (long pilot in new[] { first, second })
                {
                    InventoryPersistence.MergeOutput(
                        supply,
                        catalog,
                        pilot,
                        headquarters,
                        (int)InventoryFlag.Hangar,
                        upgrade.FuelTypeId,
                        quantity
                    );
                }
                supply.Commit();
            }

            Advance(reopened, 700, () => Deliveries().Any());
            var delivery = Assert.Single(Deliveries());
            var trip = Assert.IsType<SovereigntyExpedition>(delivery.Work.SovereigntyExpedition);
            Assert.Equal(
                ((long?)later.HubId, (int?)laterSystem, laterSystem, quantity),
                (trip.HubId, trip.HomeSystemId, trip.TargetSystemId, trip.CargoQuantity)
            );
            restored.Economy.Read(world =>
            {
                var cargo = Assert.IsType<OwnedItem>(world.Set<OwnedItem>().Find(trip.CargoId));
                Assert.Equal(
                    (delivery.PilotId, delivery.Work.ShipId, upgrade.FuelTypeId, (int)InventoryFlag.Cargo, quantity),
                    (cargo.OwnerId, cargo.LocationId, cargo.TypeId, cargo.FlagId, cargo.Quantity)
                );
                return true;
            });

            Advance(
                reopened,
                1200,
                () =>
                    restored.Economy.Read(world =>
                        world.Filter<SavedSovereigntyFuel>(row => row.HubId == later.HubId).Any(row => row.Amount > 0)
                    )
            );
            restored.Economy.Read(world =>
            {
                Assert.Null(world.Set<OwnedItem>().Find(trip.CargoId));
                Assert.Empty(world.Filter<SavedSovereigntyFuel>(row => row.HubId == oldest.HubId));
                var supplied = Assert.Single(world.Filter<SavedSovereigntyFuel>(row => row.HubId == later.HubId));
                Assert.Equal((upgrade.FuelTypeId, 4 * upgrade.HourlyFuel), (supplied.TypeId, supplied.Amount));
                Assert.True(Assert.Single(world.Filter<SavedSovereigntyUpgrade>(row => row.HubId == later.HubId)).StartupPaid);
                return true;
            });
            WaitForReturn(reopened, [delivery.PilotId]);
            Assert.Equal(oldest, restoredClaims.Read(oldestSystem));
            Assert.Equal(later, restoredClaims.Read(laterSystem));

            IEnumerable<HaulerCheckpoint> Deliveries() =>
                restored.HaulerCheckpoints.Values.Where(checkpoint =>
                    checkpoint.Work.SovereigntyExpedition is { Duty: SovereigntyDuty.ReagentDelivery, Returning: false }
                );
        }

        void Fit(EconomyTransaction world, CatalogDbContext catalog, SavedCapsuleerHauler courier)
        {
            var ship = world.Pilots.Find(courier.PilotId)!.ActiveShip;
            if (world.Positions.Find(ship.Id) is { } position)
            {
                world.Remove(position);
            }
            ship.StationId = headquarters;
            courier.Progress = null;
            world.RemoveRange(world.Filter<OwnedItem>(item => item.LocationId == ship.Id).ToArray());
            TrafficFitting.Apply(
                world,
                catalog,
                courier.PilotId,
                ship.Id,
                TrafficSnapshotTests.Profile() with
                {
                    HullTypeId = CapsuleerAllianceRules.ExpeditionSquall,
                    Equipment = [new(CapsuleerAllianceRules.ExpeditionLink, (int)InventoryFlag.HighSlot0, 1)],
                },
                fixture.Clock.Now
            );
            InventoryPersistence.MergeOutput(
                world,
                catalog,
                courier.PilotId,
                ship.Id,
                (int)InventoryFlag.Cargo,
                SovereigntyCaptureFixture.Strontium,
                fuel
            );
        }

        void AddHub(EconomyTransaction world, CatalogDbContext catalog, int system, long claimedAt)
        {
            long id = Math.Max(world.NextItemId(), StructureRules.FirstStructureId + (system == oldestSystem ? 1 : 2));
            var profile = EntosisCatalog.Hub(catalog);
            world.Add(
                new SavedStructure
                {
                    Id = id,
                    TypeId = EntosisRules.HubTypeId,
                    CorporationId = corporation,
                    SystemId = system,
                    State = StructureState.ShieldVulnerable,
                    StateStartedAt = claimedAt,
                    X = profile.StarDistance,
                }
            );
            world.Add(
                new SavedSovereigntyHub
                {
                    SystemId = system,
                    HubId = id,
                    CaptureDuration = profile.CaptureDuration,
                    UpdatedAt = claimedAt,
                }
            );
            world.Add(
                new SavedSovereigntyClaim
                {
                    SystemId = system,
                    HubId = id,
                    CorporationId = corporation,
                    AllianceId = alliance,
                    ClaimedAt = claimedAt,
                }
            );
        }

        long AddNode(EconomyTransaction world, SovereigntyClaim source, int system, double value)
        {
            long id = world.NextItemId();
            var position = SovereigntyWarfareRules.NodePosition(id, []);
            world.Add(
                new SavedSovereigntyNode
                {
                    Id = id,
                    SystemId = system,
                    SourceSystemId = source.SystemId,
                    HubId = source.HubId,
                    CorporationId = source.CorporationId,
                    AllianceId = source.AllianceId,
                    Value = value,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            return id;
        }

        EntosisCycle? ActiveCycle(ServiceProvider host, long pilot) =>
            host.GetRequiredService<IPilotEntosis>()
                .Cycles(pilot)
                .SingleOrDefault(cycle => !cycle.Stopping && cycle.EndsAt > fixture.Clock.Now);

        void WaitForReturn(ServiceProvider host, long[] pilots)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var entosis = host.GetRequiredService<IPilotEntosis>();
            HashSet<long> returning = [.. pilots];
            Advance(
                host,
                1300,
                () =>
                {
                    returning.RemoveWhere(pilot =>
                        scheduler.Hauler(pilot) is { Pilot.Position: null } worker && worker.Pilot.DockedLocationId == headquarters
                    );
                    return returning.Count == 0
                        && pilots.All(pilot =>
                            scheduler.Hauler(pilot) is { Work.SovereigntyExpedition: null } && entosis.Cycles(pilot).Count == 0
                        );
                }
            );
        }

        void Advance(ServiceProvider host, int seconds, Func<bool> ready)
        {
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (int step = 0; step < seconds && !ready(); step++)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            AssertUnattended(host);
            if (!ready())
            {
                var scheduler = host.GetRequiredService<CapsuleerScheduler>();
                var firstWorker = scheduler.Hauler(first);
                var secondWorker = scheduler.Hauler(second);
                Assert.Fail(
                    $"Timed out at {fixture.Clock.UtcNow} with scheduler failure {scheduler.Read().LastFailure}. "
                        + $"First in {firstWorker?.Pilot.SystemId} {firstWorker?.Work.Activity} {firstWorker?.Work.SovereigntyExpedition}. "
                        + $"Second in {secondWorker?.Pilot.SystemId} {secondWorker?.Work.Activity} "
                        + $"{secondWorker?.Work.SovereigntyExpedition}."
                );
            }
        }

        static void StartDefense(ServiceProvider host)
        {
            StartAllianceActivity(host, unattended: true);
            host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(HeadquartersSystem);
        }

        static SovereigntyExpedition? ActiveDefense(LiveWorld live, long pilot) =>
            live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work.SovereigntyExpedition;

        static int Fuel(LiveWorld live, long ship) =>
            live.Economy.Read(world =>
                world
                    .Filter<OwnedItem>(item => item.LocationId == ship)
                    .Where(item => item.TypeId == SovereigntyCaptureFixture.Strontium && item.FlagId == (int)InventoryFlag.Cargo)
                    .Sum(item => item.Quantity)
            );
    }
}
