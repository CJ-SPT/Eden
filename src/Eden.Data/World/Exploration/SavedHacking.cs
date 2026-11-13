namespace Eden.Data.World.Exploration;

public sealed class SavedExplorationSite
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public int Kind { get; set; }
    public int DungeonId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long ExpiresAt { get; set; }
    public long? ReplaceAt { get; set; }
    public long? ResponseAt { get; set; }
    public bool ResponseSpawned { get; set; }
    public bool Detonated { get; set; }
}

public sealed class SavedHackingContainer
{
    public long Id { get; set; }
    public long SiteId { get; set; }
    public int Index { get; set; }
    public int Failures { get; set; }
    public bool Hacked { get; set; }
    public bool Destroyed { get; set; }
    public bool Empty { get; set; }
    public long OwnerId { get; set; }
    public int LootSeed { get; set; }
}

public sealed class SavedHackingAttempt
{
    public long PilotId { get; set; }
    public long ContainerId { get; set; }
    public string ContactJson { get; set; } = string.Empty;
}
