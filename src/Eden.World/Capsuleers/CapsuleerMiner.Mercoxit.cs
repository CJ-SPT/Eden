using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private MercoxitExpedition? PrepareMercoxitExpedition(IReadOnlyList<InventoryItem> aboard)
    {
        var catalog = _operation.Catalog;
        if (
            _operation.World is not EconomyTransaction world
            || Live is not { } live
            || !world.Settings.Capsuleers.Miners
            || Pilot.ShipTypeId != (int)CapsuleerMiningHull.Venture
            || world.Set<SavedCapsuleerAgent>().Find(pilotId) is not { Slot: 0 } agent
            || agent.CommunitySystemId != Pilot.SystemId
            || !CapsuleerTrafficRoutes.MercoxitRoutes(catalog.Definitions).TryGetValue(Pilot.SystemId, out var routes)
            || !StationCapabilities.Has(catalog, Pilot.OperationId, StationService.Market)
        )
        {
            return null;
        }

        var sale = industry.OfferMercoxit(world, catalog, Pilot, Now);
        if (sale.HasValue)
        {
            _operation.MarketChanged |= sale.Value;
            // Existing stock or an open sale supplies this station. Resume ordinary belt work in the meantime.
            return null;
        }
        var route = routes.FirstOrDefault(path =>
            world.Settings.Capsuleers.CanWork(catalog.Definitions, path[^1])
            && live.MercoxitDepartureAvailable(pilotId, path[^1])
            && CanMineMercoxit(world, path[^1])
        );
        if (route is null)
        {
            return null;
        }
        int laser = (int)MiningLaserType.DeepCoreMiningLaserI;
        bool equipped =
            aboard.Any(item => item.TypeId == laser && InventoryDefinitions.IsHighSlot(item.FlagId))
            || PilotInventoryStore
                .List(world, catalog, pilotId, Pilot.StationId)
                .Any(item => item.TypeId == laser && item.FlagId == (int)InventoryFlag.Hangar);
        if (!equipped)
        {
            if (Work.DeepCoreProvisioned || agent.FounderShipId != Pilot.ShipId)
            {
                return null;
            }
            // The specialist receives one starter tool on its original ship. The ordinary fitting and training rules still apply.
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilotId, laser, []);
            InventoryPersistence.MergeOutput(world, catalog, pilotId, Pilot.StationId, (int)InventoryFlag.Hangar, laser, 1);
        }
        _work = Work with { DeepCoreProvisioned = true };
        return new(Pilot.StationId, route);
    }

    private bool CanMineMercoxit(EconomyTransaction world, int destination)
    {
        var catalog = _operation.Catalog;
        var (stripped, avoided) = BeltMemories(Crewmates().ToArray());
        long elapsed = Work.ActiveTicks;
        var candidates = catalog
            .Definitions.BeltAnchors(destination)
            .Where(belt =>
                CapsuleerTrafficRoutes.MercoxitBelt(catalog, belt.Id)
                && !stripped.Any(memory => memory.BeltId == belt.Id && memory.Until > elapsed)
                && !avoided.Any(memory => memory.BeltId == belt.Id && memory.Until > elapsed)
            )
            .Select(belt => belt.Id)
            .ToHashSet();
        if (candidates.Count == 0)
        {
            return false;
        }
        HashSet<int> populated = [];
        foreach (var rock in world.AsteroidsInSystem(destination))
        {
            if (!candidates.Contains(rock.BeltId))
            {
                continue;
            }
            if (rock.Quantity > 0 && catalog.FittingType(rock.TypeId)?.GroupId == MiningDefinitions.MercoxitGroup)
            {
                return true;
            }
            populated.Add(rock.BeltId);
        }
        // A never-visited belt can still be populated on arrival. A saved belt needs unmined Mercoxit.
        return !candidates.IsSubsetOf(populated);
    }

    private void ContinueExpedition(MercoxitExpedition expedition)
    {
        int index = Array.IndexOf(expedition.Systems, Pilot.SystemId);
        if (index < 0)
        {
            throw new InvalidDataException("The miner has left its expedition route.");
        }
        _work = Work with { Mercoxit = expedition, WorkingBelt = 0 };
        int next = index + (expedition.Returning ? -1 : 1);
        if (next >= 0 && next < expedition.Systems.Length)
        {
            var gate =
                _objects.FirstOrDefault(body => body.DestinationSystemId == expedition.Systems[next])
                ?? throw new InvalidDataException("A gate on the mining expedition route is unavailable.");
            StartCourse(gate, MinerActivity.FlyingToGate);
        }
        else if (expedition.Returning)
        {
            StartCourse(_objects.Single(body => body.Id == expedition.StationId), MinerActivity.Returning);
        }
        else if (expedition.RefineryStationId > 0)
        {
            StartCourse(_objects.Single(body => body.Id == expedition.RefineryStationId), MinerActivity.Returning);
        }
        else if (CanWorkHere && NextBelt(out _, out _) is { } field)
        {
            _work = Work with { WorkingBelt = field.Id };
            StartCourse(field, MinerActivity.FlyingToBelt);
        }
        else
        {
            ContinueExpedition(expedition with { Returning = true });
        }
    }

    private void ArriveAtExpeditionGate()
    {
        var gate = _target ?? throw new InvalidDataException("The miner has lost its route gate.");
        if (!GateTravelRules.InRange(Pilot.Position!, Pilot.Flight.ShipRadius, gate))
        {
            Follow(gate);
            return;
        }
        _navigation.SetAtRest();
        NpcCombatStore.Disengage(_operation.World, _operation.Catalog, Pilot.ShipId);
        _work = Work with
        {
            Activity = MinerActivity.Jumping,
            DueStamp = Work.Stamp + GateTravelRules.DurationTicks,
            DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(GateTravelRules.DurationTicks).Ticks,
        };
    }

    private void CompleteExpeditionJump()
    {
        if (_target?.DestinationGateId is not int exit)
        {
            throw new InvalidDataException("The miner's jump has no reciprocal gate.");
        }
        var world = _operation.World;
        var transfer = PilotGateTravelStore.TransferDetailed(world, _operation.Catalog, pilotId, Pilot.ShipId, _target.Id, exit, Now);
        _pilot = Pilot with { Location = new(transfer.Position, transfer.System) };
        using var destination = Live?.UseSystem(Pilot.SystemId);
        _navigation.AdoptHull();
        GateCloakPersistence.Cancel(world, Pilot.ShipId);
        _operation.TravelChanged = true;
        RefreshObjects();
        if (Work.Mercoxit is { } expedition)
        {
            ContinueExpedition(expedition);
        }
        else if (!ContinueCorporationRelocation(world))
        {
            throw new InvalidDataException("The miner no longer has an active route.");
        }
    }
}
