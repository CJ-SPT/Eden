namespace Eden.Gameplay.FactionWarfare;

public readonly record struct MilitiaAdvantageScore(MilitiaFaction Faction, int Contribution, int Floor)
{
    public int Total => Contribution + Floor;
}

public sealed record MilitiaAdvantageState(int SystemId, MilitiaAdvantageScore First, MilitiaAdvantageScore Second)
{
    public MilitiaAdvantageScore Score(MilitiaFaction faction) =>
        faction == First.Faction ? First
        : faction == Second.Faction ? Second
        : throw new ArgumentOutOfRangeException(nameof(faction));

    public int Net(MilitiaFaction faction) => faction == First.Faction ? First.Total - Second.Total : Score(faction).Total - First.Total;
}

public sealed record MilitiaAdvantageContribution(int SystemId, long PilotId, MilitiaFaction Faction, int Amount, long At);

public static class MilitiaAdvantageRules
{
    public const int Maximum = 100_000;
    public const int NeighborFloor = 10_000;
    public const int DailyDecay = 1_000;
    public const int BattlefieldContribution = 15_000;
    public const int RendezvousContribution = 2_000;
    public const int SupplyLoss = -2_000;
    public const int PropagandaContribution = 2_000;
    public const int ListeningLoss = -2_000;

    public static bool IsEmpire(int faction) =>
        faction is (int)MilitiaFaction.Amarr or (int)MilitiaFaction.Minmatar or (int)MilitiaFaction.Caldari or (int)MilitiaFaction.Gallente;

    public static int EmpireIndex(MilitiaFaction faction) =>
        faction switch
        {
            MilitiaFaction.Amarr => 0,
            MilitiaFaction.Minmatar => 1,
            MilitiaFaction.Caldari => 2,
            MilitiaFaction.Gallente => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(faction)),
        };

    public static MilitiaFaction EmpireAt(int index) =>
        index switch
        {
            0 => MilitiaFaction.Amarr,
            1 => MilitiaFaction.Minmatar,
            2 => MilitiaFaction.Caldari,
            3 => MilitiaFaction.Gallente,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public static int Contribution(int saved, long updatedAt, long now, int floor)
    {
        long days = Math.Max(0, now - updatedAt) / TimeSpan.TicksPerDay;
        return (int)Math.Clamp(saved - days * DailyDecay, 0, Maximum - floor);
    }

    public static int AdjustedVictoryPoints(int points, int net) =>
        points == 0 || net <= 0 ? points : points + Math.Sign(points) * Math.Max(1, Math.Abs(points) * net / Maximum);
}
