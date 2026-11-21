using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedMilitiaRank
{
    public long PilotId { get; set; }
    public MilitiaFaction Faction { get; set; }
    public int HighestRank { get; set; }
}

public sealed class SavedMilitiaStatistics
{
    public MilitiaStatisticOwner Scope { get; set; }
    public long OwnerId { get; set; }
    public long Kills { get; set; }
    public long VictoryPoints { get; set; }
    public string DaysJson { get; set; } = "[]";
}

public sealed class SavedMilitiaLoyaltyAward
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public int IssuerId { get; set; }
    public long Points { get; set; }
    public long Tax { get; set; }
    public long Time { get; set; }
    public long SourceId { get; set; }
    public MilitiaLoyaltySource Source { get; set; }
}
