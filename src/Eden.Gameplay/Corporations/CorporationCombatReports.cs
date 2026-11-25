using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Corporations;

public interface ICorporationCombatReports
{
    IReadOnlyList<ShipCombatReport> Recent(long pilotId, bool losses, int count, long? beforeId);
}
