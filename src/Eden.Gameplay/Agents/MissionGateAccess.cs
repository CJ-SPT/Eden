using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Agents;

public sealed record MissionGateKey(int TypeId, int Quantity = 1, bool Consume = false);

public sealed record MissionGateAccess(
    InventoryTypeList? AllowedShips,
    IReadOnlySet<long> AllowedRaces,
    InventoryTypeList? ForbiddenEquipment,
    MissionGateKey? Key
)
{
    public static MissionGateAccess Default { get; } = new(null, new HashSet<long>(), null, null);
}

public sealed record MissionGateActivation(IReadOnlyList<InventoryChange> Items);

public static class MissionGateAccessRules
{
    private const int DowntimeHourUtc = 11;

    public static DateTimeOffset NextDowntime(DateTimeOffset now)
    {
        var next = new DateTimeOffset(now.UtcDateTime.Date.AddHours(DowntimeHourUtc), TimeSpan.Zero);
        return now >= next ? next.AddDays(1) : next;
    }

    public static void ValidateShip(MissionGateAccess access, InventoryItem ship, int raceId, IReadOnlyList<InventoryItem> fitted)
    {
        bool explicitShips = access.AllowedShips is not null || access.AllowedRaces.Count > 0;
        if (
            access.AllowedShips is not null && !access.AllowedShips.Contains(ship)
            || access.AllowedRaces.Count > 0 && raceId != 0 && !access.AllowedRaces.Contains(raceId)
            || !explicitShips && MissionGateRules.DefaultRestricted(ship.GroupId)
        )
        {
            throw new InvalidDataException("This ship is not permitted through this acceleration gate.");
        }
        if (access.ForbiddenEquipment is not null && fitted.Any(access.ForbiddenEquipment.Contains))
        {
            throw new InvalidDataException("Remove the prohibited fitted equipment before activating this acceleration gate.");
        }
    }

    public static bool RequiresKey(MissionGateKey? key, bool unlocked)
    {
        if (key is null)
        {
            return false;
        }
        if (key.TypeId <= 0 || key.Quantity <= 0)
        {
            throw new InvalidDataException("Invalid mission acceleration gate key requirement.");
        }
        return !key.Consume || !unlocked;
    }
}
