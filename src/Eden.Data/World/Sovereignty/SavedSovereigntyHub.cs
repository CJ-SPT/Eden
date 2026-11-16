namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyHub
{
    public int SystemId { get; set; }
    public long HubId { get; set; }
    public long CaptureDuration { get; set; }
    public int? ControllingAllianceId { get; set; }
    public long ProgressTicks { get; set; }
    public long UpdatedAt { get; set; }
    public int? ScoringAllianceId { get; set; }
    public int Rate { get; set; }
    public bool Blocked { get; set; }
}

public sealed class SavedEntosisCycle
{
    public long ModuleId { get; set; }
    public int TypeId { get; set; }
    public long PilotId { get; set; }
    public long ShipId { get; set; }

    // The paid target may be a hub or a campaign command node.
    public long HubId { get; set; }
    public int SystemId { get; set; }
    public int CorporationId { get; set; }
    public int AllianceId { get; set; }
    public long StartedAt { get; set; }
    public long EndsAt { get; set; }
    public long WarmupUntil { get; set; }
    public double Range { get; set; }
    public double SpeedLimit { get; set; }
    public bool Capital { get; set; }
    public long Repeats { get; set; }
    public bool Stopping { get; set; }
    public bool Contributing { get; set; }
}
