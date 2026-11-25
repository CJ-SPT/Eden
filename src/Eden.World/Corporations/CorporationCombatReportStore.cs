using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationCombatReportStore(WorldDatabase database) : ICorporationCombatReports
{
    public IReadOnlyList<ShipCombatReport> Recent(long pilotId, bool losses, int count, long? beforeId)
    {
        if (count is < 1 or > InteractionHistoryRules.MaximumCombatPageSize || beforeId is <= 0)
        {
            throw new InvalidDataException("Choose a valid corporation combat history page.");
        }
        var world = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation combat history requires a running world.");
        return world.Economy.CorporationCombatReports(pilotId, losses, count, beforeId);
    }
}
