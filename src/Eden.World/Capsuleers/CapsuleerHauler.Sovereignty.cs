using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Gameplay.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const double HubWarpStandOff = 5000;
    private static readonly int[] s_hubHolds =
    [
        (int)InventoryFlag.InfrastructureHold,
        (int)InventoryFlag.FleetHangar,
        (int)InventoryFlag.Cargo,
    ];

    private bool TrySovereigntyExpedition(bool prepare = true)
    {
        if (_operation.World is not EconomyTransaction world)
        {
            return false;
        }
        var catalog = _operation.Catalog;
        if (Work.SovereigntyExpedition is { Returning: true } returned)
        {
            if (InventoryPersistence.OwnedStack(world, pilotId, returned.CargoId) is { } remaining && remaining.LocationId == Pilot.ShipId)
            {
                PilotInventoryStore.Move(
                    world,
                    catalog,
                    pilotId,
                    Pilot.StationId,
                    new(
                        [remaining.Id],
                        Pilot.ShipId,
                        (int)InventoryFlag.Hangar,
                        returned.CargoQuantity > 0 ? Math.Min(remaining.Quantity, returned.CargoQuantity) : null
                    ),
                    Now
                );
            }
            _work = Work with { SovereigntyExpedition = null, Journey = null, Retreating = false };
        }
        if (
            InActiveGang
            || Work.RegionalEscort is not null
            || Work.CorporationSupply is not null
            || Work.Freight is { Stacks.Length: > 0 } incoming && incoming.Destination != Pilot.StationId
            || Work.Ratting is not null
            || Work.Retreating
            || CapsuleerIndustry.Employer(world, pilotId, Pilot.CorpId) is not { } corporation
            || corporation.StationId != Pilot.StationId
            || world.Set<SavedAllianceMember>().Find(corporation.Id) is not { } membership
            || world.Set<SavedAlliance>().Find(membership.AllianceId) is not { ClosedAt: null } alliance
            || alliance.ExecutorCorporationId != corporation.Id
            || world.Set<SavedCapsuleerAgent>().Find(alliance.CreatorId) is null
            || Work.SovereigntyExpedition is not null
        )
        {
            return false;
        }
        var home = world
            .Filter<SavedSovereigntyClaim>(claim => claim.AllianceId == alliance.Id)
            .OrderBy(claim => claim.ClaimedAt)
            .ThenBy(claim => claim.SystemId)
            .FirstOrDefault();
        if (home is not null)
        {
            SettleSovereigntyDockFreight(world);
            return (
                    !CapsuleerAlliances.ExpeditionActive(world, corporation, pilotId)
                    && TryProtectSovereignty(world, catalog, corporation, home, prepare)
                ) || StandByAtSovereignty(world, catalog, corporation, home);
        }
        if (
            !world.Settings.Capsuleers.NewSovereigntyClaims
            || CapsuleerAlliances.ExpeditionActive(world, corporation, pilotId)
            || world.Filter<SavedAllianceMember>(member => member.AllianceId == alliance.Id).Count()
                < CapsuleerAllianceRules.FoundingCorporations
            || CapsuleerAlliances.HeadquartersHub(world, corporation) is not { } package
        )
        {
            return false;
        }
        SettleSovereigntyDockFreight(world);

        var pilot = world.Pilots.Find(pilotId)!;
        var ownedHub = PilotInventoryStore.Read(catalog, package);
        int hold = s_hubHolds.FirstOrDefault(flag =>
            ShipHoldRules.Definition(flag)!.Allows(ownedHub)
            && PilotInventoryStore.HoldCapacity(world, catalog, pilotId, Pilot.ShipId, flag)
                - InventoryPersistence.UsedVolume(world, catalog, pilotId, Pilot.ShipId, flag)
                >= ownedHub.Volume
        );
        long duration = EntosisCatalog.Hub(catalog).CaptureDuration;
        long? module = ReadyEntosisModule(world, catalog, duration);
        bool qualified = ownedHub.Volume > 0 && hold != 0 && module.HasValue;

        var avoided = AvoidedSupplierSystems();
        var route = CapsuleerAlliances
            .UnclaimedSites(world, catalog, Pilot.SystemId)
            .Select(system =>
                CapsuleerTrafficRoutes.ToClaimSystem(
                    catalog,
                    Pilot.StationId,
                    system,
                    qualified ? (HullClass)Pilot.ShipGroupId : HullClass.Hauler,
                    avoided,
                    CapsuleerAlliances.FirstClaimJumpLimit(world),
                    policy: world.Settings.Capsuleers
                )
            )
            .FirstOrDefault(candidate => candidate is not null);
        if (route is null)
        {
            return false;
        }
        if (!qualified)
        {
            return prepare && PrepareSovereigntyFit(world, catalog, corporation, alliance.Id, duration);
        }
        if (!CorporationRoleStore.AssignBotStationManager(world, corporation, pilotId, Now))
        {
            return false;
        }

        package.Quantity--;
        if (package.Quantity == 0)
        {
            world.Remove(package);
        }
        var cargo = InventoryPersistence.MergeOutput(world, catalog, pilotId, pilot.ActiveShipId, hold, EntosisRules.HubTypeId, 1);
        LaunchSovereignty(route, new(alliance.Id, corporation.Id, route.Systems[^1], cargo.Item.Id, module!.Value));
        return true;
    }

    private void SettleSovereigntyDockFreight(WorldAction world)
    {
        if (Work.Freight is { } arrived)
        {
            CapsuleerFreight.Deliver(world, pilotId, Pilot.ShipId, arrived);
            _work = Work with { Freight = null };
        }
    }

    private void LaunchSovereignty(CapsuleerTrafficRoute route, SovereigntyExpedition expedition)
    {
        _route = route;
        _work = Work with
        {
            Journey = new(TrafficJourneyKind.SovereigntyExpedition, Pilot.StationId, Pilot.StationId, Pilot.StationId, route.Systems, 0),
            RouteIndex = 0,
            RouteDirection = 1,
            TrafficLane = null,
            SovereigntyExpedition = expedition,
        };
        LaunchActivity();
        BeginTravel();
    }

    internal static int RequiredExpeditionCycles(long duration, EntosisProfile profile) =>
        checked(1 + (int)Math.Ceiling(duration / (profile.Duration * TimeSpan.TicksPerMillisecond)));

    private void BeginSovereigntyGrid(SovereigntyExpedition expedition)
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        if (!CanContinueSovereignty(world, expedition))
        {
            ReturnFromSovereignty(expedition);
            return;
        }
        CrimewatchPersistence.CheckTravel(world, catalog, world.Pilots.Find(pilotId)!, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the sovereignty expedition from aligning.");
        }
        if (expedition.HubId is null)
        {
            GateCloakPersistence.Cancel(world, Pilot.ShipId);
            var deployed = entosis.Deploy(world, catalog, pilotId, expedition.CargoId, Now);
            expedition = expedition with { HubId = deployed.Structure.Id };
        }
        long hubId = expedition.HubId.Value;
        _target = PilotEntosisStore.ExpeditionTarget(world, catalog, expedition.TargetSystemId, hubId);
        if (_target is null)
        {
            ReturnFromSovereignty(expedition);
            return;
        }
        _work = Work with
        {
            SovereigntyExpedition = expedition,
            TargetId = hubId,
            Activity = HaulerActivity.Flying,
            WarpReadyAt = CapsuleerWarpRules.Begin(_navigation, WarpOrder(_target), Work.ActiveTicks),
        };
    }

    private void AdvanceSovereignty(SovereigntyExpedition expedition)
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        long hubId = expedition.HubId!.Value;
        if (expedition.Duty != SovereigntyDuty.ReagentDelivery)
        {
            entosis.Advance(world, catalog, expedition.TargetSystemId, Now);
        }
        if (
            !CanContinueSovereignty(world, expedition)
            || PilotEntosisStore.ExpeditionTarget(world, catalog, expedition.TargetSystemId, hubId) is not { } target
        )
        {
            ReturnFromSovereignty(expedition);
            return;
        }
        if (expedition.Duty == SovereigntyDuty.ReagentDelivery)
        {
            DeliverSovereigntyReagent(expedition, target);
            return;
        }

        if (world.Set<SavedEntosisCycle>().Find(expedition.ModuleId) is { } cycle)
        {
            if (cycle.EndsAt > Now)
            {
                WaitAtSovereignty(cycle.EndsAt - Now);
                return;
            }
            if (entosis.Renew(world, catalog, pilotId, expedition.ModuleId, Now) is { } next)
            {
                WaitAtSovereignty(next.Cycle.EndsAt - Now);
                return;
            }
        }

        var profile = ExpeditionProfile(world, catalog, expedition.ModuleId);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, world.Pilots.Find(pilotId)!);
        double range = Math.Min(profile.Range, attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange));
        if (Pilot.Position!.DistanceTo(target.Position) - Pilot.Flight.ShipRadius - target.Radius > range)
        {
            _target = target;
            _work = Work with { Activity = HaulerActivity.Flying, SovereigntyExpedition = expedition with { LockReadyAt = 0 } };
            Follow();
            return;
        }
        _navigation.SetAtRest();
        if (!entosis.HasLock(Pilot.ShipId, hubId))
        {
            if (expedition.LockReadyAt == 0)
            {
                double signature = PilotFittingStore.SignatureRadius(world, catalog, hubId, target.TypeId);
                double seconds = TargetingDefinitions.LockSeconds(attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution), signature);
                if (!double.IsFinite(seconds) || seconds < 0)
                {
                    ReturnFromSovereignty(expedition);
                    return;
                }
                long delay = (long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond);
                _work = Work with { SovereigntyExpedition = expedition with { LockReadyAt = checked(Work.ActiveTicks + delay) } };
                WaitAtSovereignty(delay);
                return;
            }
            if (expedition.LockReadyAt <= Work.ActiveTicks)
            {
                entosis.LockAfterCommit(world, Pilot.ShipId, hubId);
            }
            WaitAtSovereignty(expedition.LockReadyAt - Work.ActiveTicks);
            return;
        }
        long fuel = ExpeditionFuel(world, profile);
        if (fuel == 0 || Live?.CapsuleerActivity.Read() is not { Active: true } activity)
        {
            ReturnFromSovereignty(expedition);
            return;
        }
        var activation = entosis.Activate(
            world,
            catalog,
            pilotId,
            Pilot.ShipId,
            expedition.ModuleId,
            hubId,
            Math.Min(int.MaxValue, fuel - 1),
            Now,
            activity.Resumes
        );
        WaitAtSovereignty(activation.Cycle.EndsAt - Now);
    }

    private void ReturnFromSovereignty(SovereigntyExpedition expedition)
    {
        var world = _operation.World;
        if (expedition.HubId is long hubId)
        {
            entosis.UnlockAfterCommit(world, Pilot.ShipId, hubId);
        }
        _work = Work with { SovereigntyExpedition = expedition with { Returning = true }, RouteDirection = -1, Retreating = true };
        ContinueSovereigntyReturn(expedition);
    }

    private void ContinueSovereigntyReturn(SovereigntyExpedition expedition)
    {
        var world = _operation.World;
        if (world.Set<SavedEntosisCycle>().Find(expedition.ModuleId) is { } cycle)
        {
            entosis.Advance(world, _operation.Catalog, cycle.SystemId, Now);
            if (cycle.EndsAt > Now)
            {
                WaitAtSovereignty(cycle.EndsAt - Now);
                return;
            }
        }
        BeginTravel();
    }

    private void WaitAtSovereignty(long delay) =>
        _work = Work with
        {
            Activity = HaulerActivity.Waiting,
            DueTicks = checked(Work.ActiveTicks + Math.Max(TimeSpan.TicksPerSecond, delay)),
        };

    private bool HasExpeditionAuthority(WorldAction world, SovereigntyExpedition expedition) =>
        Pilot.CorpId == expedition.CorporationId
        && world.Set<SavedAllianceMember>().Find(expedition.CorporationId)?.AllianceId == expedition.AllianceId
        && world.Set<SavedAlliance>().Find(expedition.AllianceId) is { ClosedAt: null } alliance
        && alliance.ExecutorCorporationId == expedition.CorporationId;

    private EntosisProfile ExpeditionProfile(WorldAction world, CatalogDbContext catalog, long moduleId)
    {
        var pilot = world.Pilots.Find(pilotId)!;
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: fitting);
        return EntosisCatalog.Module(fitting.Graph.Attributes(moduleId), attributes.GetValueOrDefault(EntosisCatalog.DurationMultiplier));
    }

    private long ExpeditionFuel(WorldAction world, EntosisProfile profile) =>
        world
            .Filter<OwnedItem>(row => row.LocationId == Pilot.ShipId)
            .Where(row =>
                row.OwnerId == pilotId
                && row.FlagId == (int)InventoryFlag.Cargo
                && row.TypeId == profile.FuelType
                && !row.Singleton
                && row.Quantity > 0
            )
            .Sum(row => (long)row.Quantity) / profile.FuelQuantity;
}
