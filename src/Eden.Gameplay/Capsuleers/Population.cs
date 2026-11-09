namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerCommunitySummary(int SystemId, int HomeStationId, long Seed, int Version);

public sealed record CapsuleerAgent(
    long PilotId,
    int CommunitySystemId,
    CapsuleerCareer Career,
    long FounderShipId,
    long FounderModuleId,
    long StartingCents,
    int Slot = 0
);

public interface ICapsuleerPopulation
{
    IReadOnlyList<CapsuleerCommunitySummary> Communities();
    IReadOnlyList<CapsuleerAgent> Agents(int communitySystemId);
    IReadOnlyList<CapsuleerSystemSummary> Systems();
    CapsuleerPopulationSummary Summary();
}

public sealed record SelectedHumanPilot(long PilotId, int SystemId);

public sealed record WatchedTrafficGate(int ObserverSystemId, int SourceSystemId, long ArrivalGateId, long DepartureGateId);

public interface ICapsuleerGateWatch
{
    IReadOnlyList<WatchedTrafficGate> Read(HumanActivitySnapshot activity);
}

public sealed record HumanActivitySnapshot(long ElapsedTicks, int SelectedSessions, IReadOnlyList<int> ActiveSystems, long Resumes = 0)
{
    public IReadOnlyList<SelectedHumanPilot> Pilots { get; init; } = [];
    public bool Active => SelectedSessions > 0;
}

public interface IHumanPresence
{
    IHumanPresenceLease Select(long pilotId, int systemId);
    void Update(Guid session, int? systemId);
    HumanActivitySnapshot Read();
}

public interface IHumanPresenceLease : IDisposable
{
    void Transfer(int systemId);
}
