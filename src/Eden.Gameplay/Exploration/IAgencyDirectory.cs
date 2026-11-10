namespace Eden.Gameplay.Exploration;

public sealed record AgencySystemCount(int SystemId, int Count);

public sealed record AgencyIceDirectory(IReadOnlyList<AnomalySystemSites> Known, IReadOnlyList<AgencySystemCount> Available);

public interface IAgencyDirectory
{
    AgencyIceDirectory Ice(long now);
    IReadOnlyList<AgencySystemCount> Signatures(long now);
}
