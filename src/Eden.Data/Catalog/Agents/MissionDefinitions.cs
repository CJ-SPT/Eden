using System.Collections.Immutable;

namespace Eden.Data.Catalog.Agents;

public sealed record MissionSpawnGroup(
    ImmutableArray<int> Types,
    int Count,
    double Range,
    bool Trigger = false,
    bool CompletesMission = false,
    bool Required = true
);

public sealed record MissionWave(ImmutableArray<MissionSpawnGroup> Groups, int RoomId = 0);

public sealed record MissionRoomDefinition(int RoomId, long EntryObjectId);

public sealed record MissionGateDefinition(long FromObjectId, int DestinationRoomId, bool RequiresClear = false);

public sealed record MissionDefinition(
    int MissionId,
    int Level,
    int Difficulty,
    bool Storyline,
    string Source,
    string SourceRevision,
    ImmutableArray<MissionWave> Waves
)
{
    public ImmutableArray<MissionRoomDefinition> Rooms { get; init; } = [];
    public ImmutableArray<MissionGateDefinition> Gates { get; init; } = [];
}

public sealed record MissionContent(int Build, ImmutableArray<MissionDefinition> Missions);
