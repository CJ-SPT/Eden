using System.Security.Cryptography;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerProvisioner
{
    internal CapsuleerProvisioningTurn Provision(
        int systemId,
        CatalogDbContext catalog,
        WorldAction world,
        bool globalStartup = false,
        int maximumFounders = int.MaxValue
    )
    {
        var state = world.Set<SavedCapsuleerProvisioning>().SingleOrDefault();
        if (state is null)
        {
            state = new() { Id = 1, WorldSeed = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue };
            world.Add(state);
        }
        long seed = state.WorldSeed;
        var summary = world.Set<SavedCapsuleerSystem>().Find(systemId);
        var policy = world.Settings.Capsuleers;
        string fingerprint = Fingerprint(database, catalog, seed, policy);
        if (summary?.PolicyVersion == CapsuleerProvisioningRules.Version && summary.CatalogFingerprint == fingerprint)
        {
            return new(1, 0, 0);
        }
        var mining = CapsuleerProvisioningCatalog.Mining(catalog, systemId);
        var traffic = CapsuleerTrafficRoutes.Read(catalog, systemId);
        var origins = CapsuleerProvisioningCatalog.Origins(catalog);
        bool hasRoute = traffic.Routes.Length > 0;
        bool canHaul = hasRoute && (database.Traffic.Profiles.Count > 0 || TrafficProvisioning.HasFallback(catalog));
        int count = canHaul ? CapsuleerEncounterRules.Haulers(seed, systemId, traffic.GateCount, traffic.StationCount, policy) : 0;
        var agents = world
            .Set<SavedCapsuleerAgent>()
            .Where(a => a.CommunitySystemId == systemId)
            .Select(a => new { a.Career, a.Slot })
            .ToArray();
        bool addMiner =
            policy.Enabled
            && policy.Miners
            && origins.Length > 0
            && mining.Eligibility == CapsuleerEligibility.Eligible
            && !agents.Any(a => a.Career == CapsuleerCareer.Mining);
        bool awaitingOre = false;
        if (addMiner)
        {
            AsteroidFieldStore.Populate(world, catalog, systemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            world.SaveChanges();
            addMiner = world
                .Set<WorldAsteroid>()
                .Where(a => a.SystemId == systemId && a.BeltId > 0 && a.Quantity > 0)
                .Select(a => a.TypeId)
                .Distinct()
                .AsEnumerable()
                .Any(id => CapsuleerProvisioningCatalog.SupportedOre(catalog, id));
            if (!addMiner)
            {
                awaitingOre = true;
                mining = mining with { Eligibility = CapsuleerEligibility.NoUsableOre };
            }
        }

        if (summary is null)
        {
            summary = new() { SystemId = systemId };
            world.Add(summary);
        }
        var first = traffic.Routes.FirstOrDefault();
        summary.Eligibility =
            !policy.Enabled ? CapsuleerEligibility.Disabled
            : origins.Length == 0 ? CapsuleerEligibility.MissingOrigin
            : canHaul || mining.Eligibility == CapsuleerEligibility.Eligible ? CapsuleerEligibility.Eligible
            : hasRoute ? CapsuleerEligibility.MissingEquipment
            : CapsuleerEligibility.NoReciprocalRoute;
        summary.HomeStationId = mining.HomeStationId ?? first?.SourceStationId;
        summary.DestinationStationId = first?.DestinationStationId;
        // A depleted saved field must retry after renewal even with an unchanged catalog.
        summary.CatalogFingerprint = awaitingOre ? "" : fingerprint;
        summary.PolicyVersion = CapsuleerProvisioningRules.Version;
        var newSlots = Enumerable
            .Range(0, count)
            .Where(slot => !agents.Any(a => a.Career == CapsuleerCareer.Hauling && a.Slot == slot))
            .ToArray();
        if (origins.Length == 0 || !addMiner && newSlots.Length == 0)
        {
            world.SaveChanges();
            return new(1, 0, awaitingOre ? 1 : 0);
        }
        var population = world.Set<SavedCapsuleerPopulation>().Find(systemId);
        if (population is null)
        {
            world.Add(
                new SavedCapsuleerPopulation
                {
                    SystemId = systemId,
                    HomeStationId = summary.HomeStationId!.Value,
                    Seed = (long)(CapsuleerProvisioningRules.Seed(seed, systemId, CapsuleerCareer.Mining) & long.MaxValue),
                    Version = CapsuleerPopulationStore.PopulationVersion,
                }
            );
        }
        EnsureAccount(world);
        world.SaveChanges();
        bool created = false;
        long? createdPilot = null;
        bool pending = awaitingOre;
        int attempted = 0;
        if (addMiner)
        {
            attempted++;
            long? added = CreateFounder(
                world,
                catalog,
                mining,
                origins,
                CapsuleerCareer.Mining,
                seed,
                0,
                null,
                HaulerEncounterOpening.Docked,
                startDocked: maximumFounders != int.MaxValue
            );
            created |= added is not null;
            pending |= added is null;
            createdPilot = added;
        }
        int offset = (int)(
            CapsuleerProvisioningRules.Seed(seed, systemId, CapsuleerCareer.Hauling) % (ulong)Math.Max(1, traffic.Routes.Length)
        );
        int remaining = Math.Max(0, newSlots.Length - (maximumFounders - attempted));
        foreach (int slot in newSlots.Take(maximumFounders - attempted))
        {
            var plannedOpening = CapsuleerEncounterRules.TrafficOpening(seed, systemId, slot, count);
            var route = SelectRoute(catalog, traffic, systemId, slot, plannedOpening, offset);
            // Incremental live preparation introduces new residents through normal departures.
            var opening = maximumFounders == int.MaxValue ? plannedOpening : HaulerEncounterOpening.Docked;
            var site = mining with { HomeStationId = route.SourceStationId, DestinationStationId = route.DestinationStationId };
            TrafficJourney? journey = globalStartup
                ? new(
                    TrafficJourneyRules.HomeKind(slot, traffic.GateCount, traffic.StationCount),
                    route.SourceStationId,
                    route.SourceStationId,
                    route.DestinationStationId,
                    route.Systems,
                    0
                )
                : null;
            long? added = CreateFounder(
                world,
                catalog,
                site,
                origins,
                CapsuleerCareer.Hauling,
                seed,
                slot,
                route,
                opening,
                initialJourney: journey
            );
            created |= added is not null;
            pending |= added is null;
            createdPilot = added ?? createdPilot;
        }
        if (pending || remaining > 0)
        {
            summary.CatalogFingerprint = "";
        }
        world.SaveChanges();
        return new(1, created ? 1 : 0, pending ? 1 : 0)
        {
            HasMoreWork = remaining > 0,
            CreatedPilotId = maximumFounders == 1 ? createdPilot : null,
        };
    }

    private void EnsureAccount(WorldAction world)
    {
        if (world.Set<Account>().Any(a => a.Id == CapsuleerCommunityStore.MinerAccountId))
        {
            return;
        }
        world.Add(
            new Account
            {
                Id = CapsuleerCommunityStore.MinerAccountId,
                UserName = "Capsuleer Community",
                CreatedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                CloneGrade = AccountCloneGrade.Omega,
            }
        );
        world.SaveChanges();
    }

    private long? CreateFounder(
        WorldAction world,
        CatalogDbContext catalog,
        CapsuleerSite site,
        CapsuleerOrigin[] origins,
        CapsuleerCareer career,
        long seed,
        int slot,
        CapsuleerTrafficRoute? route,
        HaulerEncounterOpening opening,
        WatchedTrafficGate? watchedGate = null,
        bool sourceObserved = false,
        int visitor = 0,
        TrafficJourney? initialJourney = null,
        bool startDocked = false
    )
    {
        // Project IDs before aggregating so the founder transaction never tracks the saved roster.
        long pilot = (world.Pilots.Select(p => (long?)p.Id).Max() ?? 90000000) + 1;
        long ship = InventoryPersistence.NextId(world);
        var trafficProfile =
            career == CapsuleerCareer.Hauling
                ? TrafficProvisioning.Select(
                    database,
                    world,
                    catalog,
                    seed,
                    site.SystemId,
                    slot,
                    route!,
                    reservations: _startupReservations
                )
                : null;
        if (career == CapsuleerCareer.Hauling && trafficProfile is null && !TrafficProvisioning.HasFallback(catalog))
        {
            return null;
        }
        int hull =
            career == CapsuleerCareer.Mining ? CapsuleerCommunityStore.VentureType : trafficProfile?.HullTypeId ?? HaulingRules.BadgerType;
        int station = site.HomeStationId!.Value;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        ulong roleSeed = CapsuleerProvisioningRules.Seed(seed, site.SystemId, career, slot);
        var origin = origins[(int)(roleSeed % (ulong)origins.Length)];
        int attempt = 0;
        string name;
        if (trafficProfile is not null)
        {
            name = trafficProfile.Name;
        }
        else
        {
            do
            {
                name = CapsuleerProvisioningRules.Name(seed, site.SystemId, career, slot, attempt++);
            } while (_startupReservations?.Names.Contains(name) ?? world.HasPilotName(name));
        }
        int gender = (int)(roleSeed % 2);
        CharacterRules.ValidateOrigin(
            new(name, origin.RaceId, origin.BloodlineId, gender, CharacterRules.UnspecifiedAncestry, [], [], origin.SchoolId),
            new(origin.RaceId, origin.RaceId, hull, origin.CorporationId, [station], null)
        );
        world.Add(new InventoryOwner { Id = pilot });
        world.Add(
            new Ship
            {
                Id = ship,
                OwnerId = pilot,
                TypeId = hull,
                StationId = station,
                Name = name,
            }
        );
        world.Add(
            new Pilot
            {
                Id = pilot,
                AccountId = CapsuleerCommunityStore.MinerAccountId,
                Name = name,
                CorporationId = origin.CorporationId,
                ActiveShipId = ship,
                CreatedAt = now,
                RaceId = origin.RaceId,
                BloodlineId = origin.BloodlineId,
                GenderId = gender,
                AncestryId = CharacterRules.UnspecifiedAncestry,
                SchoolId = origin.SchoolId,
            }
        );
        bool blueprint = _startupReservations is not null && (career == CapsuleerCareer.Hauling || _requirePreparedBlueprints);
        if (!blueprint)
        {
            StartingSkills.Grant(world, catalog, pilot, origin.RaceId);
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, hull, []);
        }
        long module = 0;
        long cents = career == CapsuleerCareer.Mining ? CapsuleerCommunityStore.StartingCents : HaulingRules.StartingCents;
        if (career == CapsuleerCareer.Mining)
        {
            module = ship + 1;
            if (!blueprint)
            {
                CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, (int)MiningLaserType.MinerI, []);
                world.Add(
                    new OwnedItem
                    {
                        Id = module,
                        OwnerId = pilot,
                        TypeId = (int)MiningLaserType.MinerI,
                        LocationId = station,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 1,
                    }
                );
            }
            world.Add(
                new SavedCapsuleerCommunity
                {
                    Id = site.SystemId,
                    PilotId = pilot,
                    Seed = (int)(CapsuleerProvisioningRules.Seed(seed, site.SystemId, career) % int.MaxValue),
                    SchoolId = origin.SchoolId,
                    Career = career,
                    HomeStationId = station,
                    FounderShipId = ship,
                    FounderModuleId = module,
                    StartingCents = cents,
                }
            );
        }
        else
        {
            world.Add(
                new SavedCapsuleerHauler
                {
                    Id = slot == 0 ? site.SystemId : Math.Min(0, world.Set<SavedCapsuleerHauler>().Select(h => (int?)h.Id).Min() ?? 0) - 1,
                    RouteSystems = route!.Systems,
                    PilotId = pilot,
                    SourceStationId = station,
                    DestinationStationId = site.DestinationStationId!.Value,
                    FounderShipId = ship,
                    StartingCents = cents,
                }
            );
        }
        if (career == CapsuleerCareer.Hauling && trafficProfile is null && !blueprint)
        {
            const int starterOreUnits = 100;
            int ore = CapsuleerProvisioningCatalog.FreightOre(catalog);
            world.Add(
                new OwnedItem
                {
                    Id = ship + 1,
                    OwnerId = pilot,
                    TypeId = ore,
                    LocationId = ship,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = starterOreUnits,
                }
            );
        }
        world.Add(new SavedHomeStation { PilotId = pilot, StationId = station });
        world.Add(new PilotWallet { PilotId = pilot, Cents = cents });
        world.SaveChanges();
        if (blueprint)
        {
            (_startupBlueprints ??= new(catalog)).Create(
                world,
                catalog,
                origin.RaceId,
                hull,
                trafficProfile,
                pilot,
                ship,
                station,
                now,
                requirePrepared: _requirePreparedBlueprints,
                mining: career == CapsuleerCareer.Mining
            );
        }
        if (trafficProfile is not null)
        {
            if (!blueprint)
            {
                TrafficFitting.Apply(world, catalog, pilot, ship, trafficProfile, now);
            }
            TrafficProvisioning.Record(database, world, pilot, trafficProfile);
        }
        if (career == CapsuleerCareer.Mining && !blueprint)
        {
            PilotFittingStore.Fit(world, catalog, pilot, [module], station, (int)InventoryFlag.AutoFit, now);
            PilotFittingStore.SetOnline(world, catalog, pilot, ship, module, true, now);
        }
        CapsuleerPopulationStore.AddAgent(world, new(pilot, site.SystemId, career, ship, module, cents, slot));
        if (!blueprint)
        {
            CapsuleerSupplyStore.Ensure(world, catalog, pilot, ship);
        }
        long elapsed = world.ActiveTicks;
        var action = CapsuleerAction.Prepare;
        long? inboundDue = null;
        if (career == CapsuleerCareer.Mining)
        {
            var work = new MinerProgress(
                MinerActivity.Docked,
                0,
                0,
                0,
                0,
                module,
                0,
                new NavigationState().Capture(),
                ship,
                ActiveTicks: elapsed,
                ClockOffsetTicks: elapsed
            );
            // New encounters can begin midway through their first shift. This is
            // paid once, in the same transaction that creates their owned ship.
            var rocks = world
                .Set<WorldAsteroid>()
                .Where(a => a.SystemId == site.SystemId && a.BeltId > 0 && a.Quantity > 0)
                .OrderBy(a => a.Id)
                .ToArray();
            var openingPhase = startDocked ? MinerEncounterOpening.Docked : CapsuleerEncounterRules.Opening(seed, site.SystemId);
            if (rocks.Length > 0 && openingPhase != MinerEncounterOpening.Docked)
            {
                var rock = rocks[(int)(CapsuleerProvisioningRules.Seed(seed, site.SystemId, career) % (ulong)rocks.Length)];
                const double miningClearance = 750;
                SqliteWorld.SetPosition(
                    world,
                    catalog,
                    ship,
                    new(rock.X + rock.Radius + miningClearance, rock.Y, rock.Z),
                    site.SystemId,
                    now
                );
                if (openingPhase == MinerEncounterOpening.Returning)
                {
                    work = work with { Activity = MinerActivity.ReadyToReturn };
                }
                else
                {
                    var cycle = PilotMiningStore.Begin(world, catalog, pilot, module, rock.Id, now);
                    int seconds = checked((int)Math.Ceiling(cycle.Laser.DurationMilliseconds / 1000));
                    work = work with
                    {
                        Activity = MinerActivity.Mining,
                        TargetId = rock.Id,
                        CycleSeconds = seconds,
                        DueStamp = seconds,
                        DueTicks = elapsed + TimeSpan.FromSeconds(seconds).Ticks,
                        PaidUntil = now + TimeSpan.FromSeconds(seconds).Ticks,
                    };
                }
            }
            new MinerCheckpoint(pilot, work).Apply(world);
        }
        else
        {
            var docked = new SqliteWorld(database, clock).FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilot)!;
            var work = watchedGate is null
                ? CapsuleerTrafficFlight.Opening(world, catalog, docked, route!, site.SystemId, opening, elapsed, now)
                : CapsuleerTrafficFlight.OpeningInbound(world, catalog, docked, route!, watchedGate, elapsed, now, sourceObserved, visitor);
            if (initialJourney is not null)
            {
                work = work with { Journey = initialJourney };
            }
            action = work.Activity == HaulerActivity.Docked ? CapsuleerAction.Trade : CapsuleerAction.Arrival;
            if (watchedGate is not null)
            {
                inboundDue = work.DueTicks;
                if (work.Activity == HaulerActivity.Inbound)
                {
                    action = CapsuleerAction.GateTransfer;
                    inboundDue = work.DueTicks;
                }
                else if (work.Activity == HaulerActivity.Docked)
                {
                    inboundDue = work.DueTicks;
                }
            }
            new HaulerCheckpoint(pilot, work).Apply(world);
        }
        CapsuleerJobCheckpoint.Apply(
            world,
            new(
                pilot,
                0,
                0,
                inboundDue
                    ?? (
                        action == CapsuleerAction.Arrival
                            ? elapsed + TimeSpan.TicksPerSecond
                            : CapsuleerProvisioningRules.FirstDueTicks(elapsed, seed, site.SystemId, career, slot)
                    ),
                action
            )
        );
        _startupReservations?.Names.Add(name);
        if (trafficProfile is not null)
        {
            _startupReservations?.Characters.Add(trafficProfile.CharacterId);
        }
        return pilot;
    }
}
