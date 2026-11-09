using Eden.Gameplay.Agents;

namespace Eden.Data.World.Agents;

public sealed class SavedMission
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long AgentId { get; set; }
    public int MissionId { get; set; }
    public int Level { get; set; }
    public MissionState State { get; set; }
    public bool Storyline { get; set; }
    public long OfferedAt { get; set; }
    public long? AcceptedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? ClosedAt { get; set; }
    public int DestinationStation { get; set; }
    public int SystemId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Wave { get; set; } = -1;
    public bool ObjectiveComplete { get; set; }
    public long IskCents { get; set; }
    public long LoyaltyPoints { get; set; }
    public double CorporationFraction { get; set; }
    public double FactionFraction { get; set; }
}

public sealed class SavedMissionMember
{
    public long NpcId { get; set; }
    public long MissionId { get; set; }
    public int Wave { get; set; }
    public bool Trigger { get; set; }
    public bool CompletesMission { get; set; }
    public bool Required { get; set; }
    public int LootTypeId { get; set; }
    public int LootQuantity { get; set; }
}

public sealed class SavedMissionGateUnlock
{
    public long MissionId { get; set; }
    public long GateObjectId { get; set; }
    public long PilotId { get; set; }
    public long ResetAt { get; set; }
}

public sealed class SavedMissionRoom
{
    public long MissionId { get; set; }
    public int RoomId { get; set; }
    public int Wave { get; set; } = -1;
}

public sealed class SavedMissionProgress
{
    public long PilotId { get; set; }
    public int FactionId { get; set; }
    public int Level { get; set; }
    public int Completed { get; set; }
}

public sealed class SavedAgentDecline
{
    public long PilotId { get; set; }
    public long AgentId { get; set; }
    public long LastFreeDeclineAt { get; set; }
}
