namespace Eden.Gameplay.Exploration;

public static class DedEscalationRules
{
    public const int DroneInfestedMine = 10977;

    public static long Lifetime(int dungeon) => dungeon == DroneInfestedMine ? 8 * TimeSpan.TicksPerHour : TimeSpan.TicksPerDay;

    public const int DestinationSearchJumps = 5;
    public const int ChancePercent = 5;

    // EVE University combat site table, revision 230825. Only routes with a DED destination in Eden are admitted.
    private static readonly IReadOnlyDictionary<string, int> s_destinations = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Guristas Hideaway"] = 2249,
        ["Sansha Hideaway"] = 2251,
        ["Serpentis Hideaway"] = 2244,
        ["Angel Forsaken Hideaway"] = 5278,
        ["Angel Refuge"] = 2264,
        ["Blood Refuge"] = 2254,
        ["Guristas Refuge"] = 2249,
        ["Sansha Refuge"] = 2251,
        ["Serpentis Refuge"] = 2244,
        ["Drone Assembly"] = 2255,
        ["Drone Gathering"] = 2255,
        ["Angel Den"] = 2264,
        ["Blood Den"] = 5240,
        ["Guristas Den"] = 5242,
        ["Sansha Den"] = 5261,
        ["Serpentis Den"] = 5269,
        ["Guristas Hidden Den"] = 2265,
        ["Angel Forsaken Den"] = 2246,
        ["Guristas Forsaken Den"] = 5233,
        ["Serpentis Forsaken Den"] = 5238,
        ["Angel Forlorn Den"] = 2246,
        ["Guristas Forlorn Den"] = 5233,
        ["Angel Yard"] = 2264,
        ["Blood Yard"] = 2245,
        ["Guristas Yard"] = 5242,
        ["Sansha Yard"] = 2243,
        ["Serpentis Yard"] = 2455,
        ["Drone Surveillance"] = 2255,
        ["Angel Rally Point"] = 2477,
        ["Angel Hidden Rally Point"] = 2477,
        ["Blood Rally Point"] = 2245,
        ["Guristas Rally Point"] = 2265,
        ["Sansha Rally Point"] = 2243,
        ["Serpentis Rally Point"] = 2455,
        ["Drone Menagerie"] = 2247,
        ["Drone Herd"] = 2247,
        ["Angel Forsaken Rally Point"] = 2261,
        ["Blood Forsaken Rally Point"] = 5221,
        ["Guristas Forsaken Rally Point"] = 2262,
        ["Sansha Forsaken Rally Point"] = 5235,
        ["Serpentis Forsaken Rally Point"] = 5236,
        ["Blood Forlorn Rally Point"] = 5221,
        ["Guristas Forlorn Rally Point"] = 2262,
        ["Sansha Forlorn Rally Point"] = 5235,
        ["Serpentis Forlorn Rally Point"] = 5236,
        ["Angel Port"] = 2246,
        ["Blood Port"] = 5237,
        ["Guristas Port"] = 5233,
        ["Sansha Port"] = 5239,
        ["Serpentis Port"] = 5238,
        ["Angel Hub"] = 2261,
        ["Blood Hub"] = 5221,
        ["Guristas Hub"] = 2262,
        ["Sansha Hub"] = 5235,
        ["Serpentis Hub"] = 5236,
        ["Drone Squad"] = 2413,
        ["Drone Patrol"] = 2413,
        ["Drone Horde"] = 2413,
        ["Angel Haven"] = 2256,
        ["Blood Haven"] = 2260,
        ["Guristas Haven"] = 2257,
        ["Sansha Haven"] = 2258,
        ["Serpentis Haven"] = 2259,
        ["Blood Sanctum"] = 2260,
        ["Guristas Sanctum"] = 2257,
        ["Sansha Sanctum"] = 2258,
        ["Serpentis Sanctum"] = 2259,
        ["Serpentis Forsaken Sanctum"] = 2259,
    };

    public static int Destination(string anomaly) => s_destinations.GetValueOrDefault(anomaly);

    public static int Destination(string anomaly, uint roll)
    {
        // Equinox introduced a faction-spawn escalation for drone anomalies.
        // Eden allocates one of the five successful rolls to the Mine in these nullsec variants.
        return roll == 0 && anomaly is "Drone Squad" or "Drone Patrol" or "Drone Horde" or "Teeming Drone Horde"
            ? DroneInfestedMine
            : Destination(anomaly);
    }
}
