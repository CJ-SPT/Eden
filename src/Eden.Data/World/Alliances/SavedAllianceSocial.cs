namespace Eden.Data.World.Alliances;

public sealed class SavedAllianceSocial
{
    public int AllianceId { get; set; }
    public long Revision { get; set; }
    public string DataJson { get; set; } = "";
}
