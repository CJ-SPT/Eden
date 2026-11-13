using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;

namespace Eden.World.Runtime;

internal sealed record StructureContactView(IReadOnlyList<StaticSpaceObject> Contacts, long Deadline, bool WaitingForAssets);

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, StructureLocation?> _structureNames = new("resident.structureNames", "WorldStructure");
    private readonly ResidentSnapshot<(int System, long Catalog), StructureContactView> _structures = new(
        "resident.structures",
        "WorldStructure",
        "WorldMoonExtraction",
        "WorldShip",
        "WorldItemMembership"
    );

    internal StructureContactView Structures(int system, long catalog, long now, Func<StructureContactView> load) =>
        Read(_structures, (system, catalog), load, now, view => view.Deadline);

    internal StructureLocation? StructureName(long id, Func<StructureLocation?> load) => Read(_structureNames, id, load);

    private void ObserveStructures(IReadOnlyCollection<Combat.CombatRowChange> changes)
    {
        _structures.ObserveValues(
            changes,
            (_, view, change) => change.Table is "WorldStructure" or "WorldMoonExtraction" || view.WaitingForAssets
        );
        _structureNames.Observe(changes, (id, change) => id == change.Id);
    }
}
