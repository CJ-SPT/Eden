using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Agents;

public sealed record MissionShipType(int TypeId, int GroupId, int RaceId);

public sealed record MissionShipRestrictions(
    IReadOnlyList<int> AllowedShipTypes,
    IReadOnlyList<int> RestrictedShipTypes,
    bool NonDefaultShipRestrictions
);

public static class MissionShipRestrictionRules
{
    public static MissionShipRestrictions Describe(IReadOnlyList<MissionShipType> ships, IReadOnlyList<MissionGateAccess> gates)
    {
        List<int> allowed = [];
        List<int> restricted = [];
        foreach (var ship in ships.OrderBy(s => s.TypeId))
        {
            bool admitted = gates.All(g =>
                MissionGateAccessRules.AllowsShip(g, ship.TypeId, ship.GroupId, (int)InventoryCategory.Ship, ship.RaceId)
            );
            (admitted ? allowed : restricted).Add(ship.TypeId);
        }
        return new(allowed.AsReadOnly(), restricted.AsReadOnly(), gates.Any(g => g.AllowedShips is not null || g.AllowedRaces.Count > 0));
    }
}
