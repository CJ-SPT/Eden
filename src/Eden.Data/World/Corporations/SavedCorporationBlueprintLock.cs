namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationBlueprintLock
{
    public long ItemId { get; set; }
    public int CorporationId { get; set; }
    public long OfficeId { get; set; }
    public int StationId { get; set; }
    public int TypeId { get; set; }
    public int FlagId { get; set; }
    public long VoteId { get; set; }
    public bool Permanent { get; set; }
}
