using System.Collections.Immutable;

namespace Eden.Gameplay.FactionWarfare;

public enum MilitiaFaction
{
    Caldari = 500001,
    Minmatar = 500002,
    Amarr = 500003,
    Gallente = 500004,
}

public enum WarzoneId
{
    AmarrMinmatar = 1,
    CaldariGallente = 2,
}

public enum WarzoneAdjacency
{
    Frontline,
    CommandOperations,
    Rearguard,
}

public sealed record WarzoneDefinition(WarzoneId Id, MilitiaFaction FirstFaction, MilitiaFaction SecondFaction, int PirateFactionId);

public sealed record MilitiaHeadquarters(MilitiaFaction Faction, int SystemId);

public sealed record WarzoneSystem(
    int SystemId,
    WarzoneDefinition Warzone,
    MilitiaFaction Occupier,
    WarzoneAdjacency Adjacency,
    int VictoryPoints,
    long? CapturedAt
);

public interface IFactionWarzones
{
    ImmutableArray<WarzoneDefinition> Warzones { get; }
    ImmutableArray<MilitiaHeadquarters> Headquarters { get; }
    WarzoneSystem? System(int systemId);
    ImmutableArray<WarzoneSystem> Systems();
}

public static class FactionWarzoneRules
{
    public const int VictoryPointThreshold = 75_000;
    public const int VictoryPointBuffer = 500;

    public static WarzoneAdjacency Adjacency(int systemId, Func<int, MilitiaFaction> occupier, Func<int, IReadOnlyList<int>> neighbors)
    {
        if (IsFrontline(systemId))
        {
            return WarzoneAdjacency.Frontline;
        }
        return neighbors(systemId).Any(IsFrontline) ? WarzoneAdjacency.CommandOperations : WarzoneAdjacency.Rearguard;

        bool IsFrontline(int system)
        {
            var faction = occupier(system);
            return neighbors(system).Any(neighbor => faction != occupier(neighbor));
        }
    }
}
