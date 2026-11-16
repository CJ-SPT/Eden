using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Information;

public sealed record PublicLocation(long Id, string Name, int? SystemId, SpacePosition Position);

public interface IPublicLocations
{
    IReadOnlyList<PublicLocation> Read(IReadOnlyList<long> ids);
}
