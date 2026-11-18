using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private IEnumerable<MinerProgress> Crewmates()
    {
        if (crew.Count < 2 || Live is not { } live)
        {
            yield break;
        }
        foreach (long member in crew)
        {
            if (
                member != pilotId
                && !suspended(member)
                && (live.MinerCheckpoints.GetValueOrDefault(member) ?? live.ProjectedMiners.GetValueOrDefault(member)) is { Work: var work }
            )
            {
                yield return work;
            }
        }
    }

    private bool CrewDanger(long elapsed) =>
        crew.Count > 1
        && Work.WorkingBelt > 0
        && Crewmates().Any(work => work.AvoidedBelts.Any(belt => belt.BeltId == Work.WorkingBelt && belt.Until > elapsed));

    private StaticSpaceObject? SelectRock(IReadOnlyList<StaticSpaceObject> available)
    {
        var claimed = Crewmates().Where(CapsuleerMiningRules.ClaimsRock).Select(work => work.TargetId).ToHashSet();
        bool useBelt = Work.WorkingBelt > 0 && Live is not null;
        return available
            .Where(rock =>
                (!useBelt || rock.AsteroidBeltId == Work.WorkingBelt)
                && Distance(rock) <= AsteroidDefinitions.VisibilityRange
                && Enum.IsDefined((MiningResourceGroup)rock.GroupId)
            )
            .MinBy(rock => (claimed.Contains(rock.Id), Distance(rock), rock.Id));
    }

    private double MiningReach(WorldAction world, CatalogDbContext catalog, InventoryItem laser)
    {
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        var module = PilotMiningStore.ModuleAttributes(world, catalog, pilotId, laser);
        // Account for the final subwarp step and lock before reaching the edge of the laser's range.
        double reach = Math.Min(
            ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange),
            module.GetValueOrDefault(DogmaAttributeId.MaxRange)
        );
        return reach > 0 ? reach * 0.8 : ApproachRange * 2;
    }
}
