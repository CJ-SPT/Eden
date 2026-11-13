namespace Eden.Data.World.Exploration;

public sealed class WorldCombatAnomaly
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public int DungeonId { get; set; } = 2134;
    public int Slot { get; set; }
    public bool IsSignature { get; set; }
    public int DefinitionVersion { get; set; }
    public int Wave { get; set; } = -1;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long? NextWaveAt { get; set; }
    public long? CompletedAt { get; set; }
    public long? ReplaceAt { get; set; }
    public long DedVisitedRooms { get; set; }
    public long? EscalationPilotId { get; set; }
    public int EscalationSourceDungeonId { get; set; }
    public long? EscalationExpiresAt { get; set; }
}

public sealed class WorldAnomalyMember
{
    public long NpcId { get; set; }
    public long SiteId { get; set; }
    public int Wave { get; set; }
    public bool Trigger { get; set; }
    public bool Sentry { get; set; }
}
