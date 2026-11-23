namespace Eden.Gameplay.Agents;

public sealed record EpicArcProgress(int ArcId, int MissionId, long? AcceptedAt, long? CompletedAt, long? QuitAt, int NameId);

public sealed record EpicArcMessage(int MissionId, string Kind, int MessageId);

public sealed record EpicArcChoice(long OfferId, int MissionId, int NameId, int BriefingId, string Name);
