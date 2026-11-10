using System.Collections.Frozen;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record NpcSceneContacts(IReadOnlyList<NpcContact> Visible, IReadOnlySet<long> Warping)
{
    public static NpcSceneContacts Empty { get; } = new([], FrozenSet<long>.Empty);

    public static NpcSceneContacts From(IReadOnlyList<NpcContact> contacts, int systemId, SpacePosition position) =>
        new(
            contacts
                .Where(n => n.SystemId == systemId && position.DistanceTo(n.Position) <= NpcEncounterDefinitions.VisibilityRange)
                .ToArray(),
            contacts.Where(n => n.SystemId == systemId && n.Warp is not null).Select(n => n.Id).ToFrozenSet()
        );
}
