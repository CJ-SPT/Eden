using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedMilitiaNavyPursuit
{
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public int SystemId { get; set; }
    public MilitiaFaction Faction { get; set; }
    public long RespondAt { get; set; }
    public long? FrigateId { get; set; }
    public long? CruiserId { get; set; }
}
