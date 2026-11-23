using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Propulsion;

internal sealed partial class PilotSpatialModuleStore
{
    private static IReadOnlyList<MicroJumpArrival> CompleteField(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        OwnedItem item,
        SpatialNavigationState state,
        SpacePosition origin,
        SpacePosition offset,
        IReadOnlyList<MicroJumpPassenger> candidates,
        long now
    )
    {
        var effect = SpatialModuleRules.JumpEffect(item.TypeId);
        bool capitalField = effect == DogmaEffect.CapitalMicroJumpField;
        var values = capitalField
            ? FittingCalculation.Read(world, catalog, source).Graph.Attributes(item.Id)
            : catalog.FittingAttributes(item.TypeId);
        var field = SpatialModuleRules.Define(item.Id, item.TypeId, effect, values).Field!;
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        double sourceRadius = catalog.RequireItem(source.ActiveShip.TypeId).Radius ?? 0;
        long scrambleUntil = capitalField ? SpatialModuleRules.Deadline(now, field.ScrambleMilliseconds) : 0;
        int capitals = field.CapitalLimit;
        int subcapitals = field.PassengerLimit;
        List<MicroJumpArrival> arrivals = [];
        foreach (var candidate in candidates.OrderBy(c => origin.DistanceTo(c.Position)).ThenBy(c => c.ShipId).DistinctBy(c => c.ShipId))
        {
            if (capitals == 0 && subcapitals == 0)
            {
                break;
            }
            var victim = world.Pilots.Find(candidate.PilotId);
            if (
                victim is null
                || victim.Id == source.Id
                || victim.ActiveShipId != candidate.ShipId
                || victim.ActiveShip.Position is not { } position
                || CrimewatchPersistence.System(catalog, victim.ActiveShip) != system
                || world.Set<ShipLoss>().Find(candidate.ShipId) is not null
                || Scrambled(world, candidate.ShipId, now)
                || world.TetherProtected(candidate.ShipId, now)
                || ReadStatus(world, catalog, candidate.ShipId).Cloaked(now)
            )
            {
                continue;
            }
            int type = victim.ActiveShip.TypeId;
            var hull = catalog.RequireItem(type);
            bool capital =
                catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.IsCapitalSize) > 0
                || hull.GroupId == (int)HullClass.IndustrialCommand;
            var at = new SpacePosition(position.X, position.Y, position.Z);
            if ((capital ? capitals : subcapitals) == 0 || at.DistanceTo(origin) - sourceRadius - (hull.Radius ?? 0) > field.Radius)
            {
                continue;
            }
            AggressionDecision? aggression = null;
            if (capitalField)
            {
                try
                {
                    aggression = CrimewatchPersistence.CheckPlayer(world, catalog, source, victim, system, now);
                }
                catch (InvalidDataException)
                {
                    // Preserve the capital field's existing Crimewatch behavior.
                    continue;
                }
            }
            var arrival = at + offset;
            (position.X, position.Y, position.Z) = (arrival.X, arrival.Y, arrival.Z);
            if (aggression is { } decision)
            {
                State(world, candidate.ShipId).FieldScrambledUntil = scrambleUntil;
                CrimewatchPersistence.Player(world, catalog, source.Id, victim.Id, system, decision, now, now);
            }
            arrivals.Add(new(candidate.ShipId, arrival));
            if (capital)
            {
                capitals--;
            }
            else
            {
                subcapitals--;
            }
        }
        if (capitalField)
        {
            state.FieldScrambledUntil = scrambleUntil;
        }
        return arrivals;
    }
}
