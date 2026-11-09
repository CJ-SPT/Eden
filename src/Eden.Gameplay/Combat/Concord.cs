namespace Eden.Gameplay.Combat;

public interface IConcord
{
    NpcShot? Advance(long pilotId, long now);
    void Sweep(long now);
}

public static class ConcordRules
{
    // Build 3662168 client/types: the criminal-response Captain and Commander,
    // not the similarly named player hulls or ordinary faction police.
    public const int CaptainType = 3885;
    public const int CommanderType = 11125;
    public const int Corporation = 1000125;
    public const int Faction = 500006;
    public const int CaptainCount = 2;
    public const double FormationSpacing = 2000;
    public static readonly TimeSpan CommanderDelay = TimeSpan.FromSeconds(15);

    // UniWiki's approximate fresh-spawn times are an explicit deterministic
    // Eden policy. Existing squads are not yet pulled between crime scenes.
    public static TimeSpan ResponseDelay(double security)
    {
        if (!double.IsFinite(security) || security < CrimewatchRules.HighSecurity)
        {
            throw new InvalidDataException("CONCORD responds only in high-security space.");
        }
        int displayed = (int)Math.Round(security * 10, MidpointRounding.AwayFromZero);
        return TimeSpan.FromSeconds(
            displayed switch
            {
                <= 5 => 19,
                6 => 14,
                7 => 10,
                8 => 7,
                _ => 6,
            }
        );
    }
}
