namespace Eden.Data.World.Exploration;

public sealed class SavedScanProbe
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long ExpiresAt { get; set; }
    public string ProfileJson { get; set; } = "";
}

public sealed class SavedProbeScan
{
    public long PilotId { get; set; }
    public long StartedAt { get; set; }
    public long ReadyAt { get; set; }
    public string ScanJson { get; set; } = "";
}

public sealed class SavedResolvedSignature
{
    public long PilotId { get; set; }
    public long SiteId { get; set; }
    public int SystemId { get; set; }
}
