using Eden.Gameplay.FactionWarfare;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.FactionWarfare;

internal sealed record WarzoneDirectoryReply(IReadOnlyList<WarzoneDefinition> Warzones) : BlueModel
{
    public override object ToBlue() => Warzones.ToDictionary(zone => (long)zone.Id, zone => (object?)WarzoneOccupation.Factions(zone));
}

internal sealed record MilitiaHeadquartersReply(IReadOnlyList<MilitiaHeadquarters> Headquarters) : BlueModel
{
    public override object ToBlue() => Headquarters.ToDictionary(hq => (long)hq.Faction, hq => (object?)hq.SystemId);
}

internal sealed record WarzoneOccupationsReply(IReadOnlyList<WarzoneDefinition> Warzones, IReadOnlyList<WarzoneSystem> Systems) : BlueModel
{
    public override object ToBlue()
    {
        var systemsByZone = Systems.ToLookup(system => system.Warzone.Id);
        return Warzones.ToDictionary(
            zone => (long)zone.Id,
            zone =>
                (object?)
                    systemsByZone[zone.Id].ToDictionary(system => (long)system.SystemId, system => (object?)new WarzoneOccupation(system))
        );
    }
}

internal sealed record WarzoneVictoryPointsReply(IReadOnlyList<WarzoneSystem> Systems) : BlueModel
{
    public override object ToBlue() =>
        Systems.ToDictionary(system => (long)system.SystemId, system => (object?)new WarzoneVictoryPoints(system));
}

internal sealed record LocalOccupationReply(int SystemId, WarzoneSystem? State) : BlueModel
{
    public override object ToBlue() => new object?[] { SystemId, State is null ? null : new WarzoneOccupation(State) };
}

internal sealed record LocalVictoryPointsReply(int SystemId, WarzoneSystem? State) : BlueModel
{
    public override object ToBlue() => new object?[] { SystemId, State is null ? null : new WarzoneVictoryPoints(State) };
}

internal sealed record WarzoneOccupation(WarzoneSystem System) : BlueModel
{
    internal static List<object?> Factions(WarzoneDefinition zone) => [(int)zone.FirstFaction, (int)zone.SecondFaction];

    public override object ToBlue() =>
        new BlueConstructor(
            BlueConstructor.FactionOccupationClass,
            [(int)System.Occupier, Adjacency(System.Adjacency), Factions(System.Warzone), System.Warzone.PirateFactionId, System.CapturedAt]
        );

    // The 3662168 OccupationState constructor accepts the client adjacency strings.
    private static string Adjacency(WarzoneAdjacency adjacency) =>
        adjacency switch
        {
            WarzoneAdjacency.Frontline => "FL",
            WarzoneAdjacency.CommandOperations => "SL",
            WarzoneAdjacency.Rearguard => "BL",
            _ => throw new InvalidDataException("Unknown faction warfare adjacency."),
        };
}

internal sealed record WarzoneVictoryPoints(WarzoneSystem System) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(BlueConstructor.FactionVictoryPointClass, [System.VictoryPoints, FactionWarzoneRules.VictoryPointThreshold]);
}
