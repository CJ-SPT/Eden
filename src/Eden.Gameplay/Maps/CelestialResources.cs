namespace Eden.Gameplay.Maps;

public sealed record PlanetReagentResource(
    int TypeId,
    long AmountPerCycle,
    long CycleSeconds,
    long SecuredCapacity,
    long UnsecuredCapacity
);

public sealed record PlanetResourceDefinition(long PlanetId, long Power, long Workforce, PlanetReagentResource? Reagent);

public sealed record StarResourceDefinition(long StarId, long Power);

public sealed record CelestialResourceSnapshot(string Revision, IReadOnlyList<PlanetResourceDefinition> Planets)
{
    public IReadOnlyList<StarResourceDefinition> Stars { get; init; } = [];
}

public interface ICelestialResourceDirectory
{
    CelestialResourceSnapshot Read();
}
