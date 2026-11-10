using Eden.Gameplay.Flight;

namespace Eden.World.Runtime;

internal sealed record StructureContactView(IReadOnlyList<StaticSpaceObject> Contacts, long Deadline, bool WaitingForAssets);

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<(int System, long Catalog), StructureContactView> _structures = new(
        "resident.structures",
        "WorldStructure",
        "WorldShip",
        "WorldItemMembership"
    );

    internal StructureContactView Structures(int system, long catalog, long now, Func<StructureContactView> load) =>
        Read(_structures, (system, catalog), load, now, view => view.Deadline);

    private void ObserveStructures(IReadOnlyCollection<Combat.CombatRowChange> changes) =>
        _structures.ObserveValues(changes, (_, view, change) => change.Table == "WorldStructure" || view.WaitingForAssets);
}
