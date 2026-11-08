namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerCommunitySummary(int SystemId, int HomeStationId, long Seed, int Version);

public sealed record CapsuleerAgent(
    long PilotId,
    int CommunitySystemId,
    CapsuleerCareer Career,
    long FounderShipId,
    long FounderModuleId,
    long StartingCents
);

public interface ICapsuleerPopulation
{
    IReadOnlyList<CapsuleerCommunitySummary> Communities();
    IReadOnlyList<CapsuleerAgent> Agents(int communitySystemId);
}

public sealed record HumanActivitySnapshot(long ElapsedTicks, int SelectedSessions, IReadOnlyList<int> ActiveSystems, long Resumes = 0)
{
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
