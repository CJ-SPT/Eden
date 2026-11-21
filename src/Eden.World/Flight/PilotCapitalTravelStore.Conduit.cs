using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Flight;

internal sealed partial class PilotCapitalTravelStore
{
    private sealed record ConduitProfile(int Limit, DogmaAttributeId PassengerAttribute, double Distance);

    private sealed record ConduitPassenger(Pilot Pilot, double Radius, JumpTimers Timers, double Capacitor);

    private FleetPilot[] ConduitMembers(long pilot)
    {
        if (fleets.Identity(pilot) is not { } fleet)
        {
            throw new InvalidDataException("Join a fleet before making a conduit jump.");
        }
        var members = fleets.Read(pilot, fleet.FleetId).Members;
        var anchor = members.Single(member => member.Pilot.Id == pilot).Pilot;
        return members
            .Where(member =>
                member.AcceptsConduitJumps
                && member.Pilot.Id != pilot
                && member.Pilot.SystemId == anchor.SystemId
                && member.Pilot.Position is not null
                && !member.Pilot.InTransit
            )
            .Select(member => member.Pilot)
            .OrderBy(member => member.Id)
            .ToArray();
    }

    private ConduitProfile ReadConduit(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        IReadOnlyDictionary<DogmaAttributeId, double> ship,
        double distance
    )
    {
        if (fleets.Identity(pilot.Id) is null)
        {
            throw new InvalidDataException("Join a fleet before making a conduit jump.");
        }
        int group = catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId;
        int count = checked((int)ship.GetValueOrDefault(DogmaAttributeId.ConduitPassengerCount));
        var required = (DogmaAttributeId)ship.GetValueOrDefault(DogmaAttributeId.ConduitPassengerAttribute);
        if (CapitalTravelRules.CarrierConduitHull(group))
        {
            int skill = CapitalTravelRules.CapitalJumpPortalGenerationSkill;
            int level = PilotSkillReader.Level(world, catalog, pilot.Id, skill);
            var bonus = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.ConduitPassengerSkillBonus);
            count += checked((int)(level * bonus));
        }
        else
        {
            int portal = (HullClass)group switch
            {
                HullClass.BlackOps => CapitalTravelRules.CovertPortalGenerator,
                HullClass.CapitalIndustrial => CapitalTravelRules.IndustrialPortalGenerator,
                _ => 0,
            };
            if (
                portal != 0
                && world
                    .Filter<OwnedItem>(item =>
                        item.LocationId == pilot.ActiveShipId && item.OwnerId == pilot.Id && item.TypeId == portal && item.Online
                    )
                    .AsEnumerable()
                    .Any(item => InventoryDefinitions.IsHighSlot(item.FlagId))
            )
            {
                required = (DogmaAttributeId)
                    catalog.FittingAttributes(portal).GetValueOrDefault(DogmaAttributeId.ConduitPassengerAttribute);
            }
        }
        if (
            count <= 0
            || required
                is not (
                    DogmaAttributeId.CarrierConduitPassenger
                    or DogmaAttributeId.CovertConduitPassenger
                    or DogmaAttributeId.IndustrialConduitPassenger
                )
        )
        {
            throw new InvalidDataException("Use a conduit-capable hull with its required online portal.");
        }
        return new(count, required, distance);
    }

    private IReadOnlyList<ConduitPassenger> PlanConduitPassengers(
        WorldAction world,
        CatalogDbContext catalog,
        JumpPlan anchor,
        int destination,
        int origin,
        IReadOnlyList<FleetPilot> members,
        long now
    )
    {
        if (anchor.Conduit is not { } conduit || members.Count == 0)
        {
            return [];
        }
        var permitted = ConduitMembers(anchor.Pilot.Id).Select(member => (member.Id, member.ShipId)).ToHashSet();
        var anchorPosition = anchor.Pilot.ActiveShip.Position!;
        var point =
            presence.FindOnline(origin, anchor.Pilot.ActiveShipId)?.Position
            ?? new SpacePosition(anchorPosition.X, anchorPosition.Y, anchorPosition.Z);
        double radius = catalog.RequireItem(anchor.Pilot.ActiveShip.TypeId).Radius ?? 0;
        List<ConduitPassenger> passengers = [];
        foreach (var member in members)
        {
            if (passengers.Count == conduit.Limit)
            {
                break;
            }
            if (
                !permitted.Contains((member.Id, member.ShipId))
                || world.Pilots.Find(member.Id) is not { } pilot
                || pilot.ActiveShipId != member.ShipId
                || pilot.ActiveShip.Position is null
                || CrimewatchPersistence.System(catalog, pilot.ActiveShip) != origin
                || presence.IsTravelProtected(origin, member.ShipId)
                || presence.FindOnline(origin, member.ShipId) is not { } contact
            )
            {
                continue;
            }
            var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
            double hullRadius = hull.Radius ?? 0;
            if (point.DistanceTo(contact.Position) > CapitalTravelRules.ConduitRange + radius + hullRadius)
            {
                continue;
            }
            try
            {
                var previous = RequireJumpReady(world, catalog, pilot, origin, destination, now);
                var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
                bool carrier = conduit.PassengerAttribute == DogmaAttributeId.CarrierConduitPassenger;
                double allowed = attributes.GetValueOrDefault(
                    conduit.PassengerAttribute,
                    carrier && CapitalTravelRules.CarrierConduitPassengerHull(hull.GroupId) ? 1 : 0
                );
                if (allowed <= 0)
                {
                    continue;
                }
                RequireLiving(world, pilot, attributes, now);
                passengers.Add(
                    new(
                        pilot,
                        hullRadius,
                        CapitalTravelRules.AfterJump(
                            now,
                            previous?.FatigueUntil ?? 0,
                            conduit.Distance,
                            attributes.GetValueOrDefault(DogmaAttributeId.JumpFatigueMultiplier, 1)
                        ),
                        ShipResources.Capacitor(world, member.ShipId, attributes, now)
                    )
                );
            }
            catch (InvalidDataException)
            {
                // A fleetmate ineligible at settlement stays at the origin.
            }
        }
        return passengers;
    }
}
