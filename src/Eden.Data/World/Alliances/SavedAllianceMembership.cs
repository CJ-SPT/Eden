using Eden.Gameplay.Alliances;

namespace Eden.Data.World.Alliances;

public sealed class SavedAllianceApplication
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public int AllianceId { get; set; }
    public string Text { get; set; } = "";
    public AllianceApplicationStatus Status { get; set; }
    public long AppliedAt { get; set; }
    public long? EffectiveAt { get; set; }
}

public sealed class SavedAllianceHistory
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public int? AllianceId { get; set; }
    public long StartedAt { get; set; }
}

public sealed class SavedAllianceChange
{
    public long Id { get; set; }
    public int AllianceId { get; set; }
    public int CorporationId { get; set; }
    public string DataJson { get; set; } = "";
}
