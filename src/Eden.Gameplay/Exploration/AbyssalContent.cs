namespace Eden.Gameplay.Exploration;

public sealed record AbyssalFilament(int TypeId, int Tier, AbyssalWeather Weather);

public sealed record AbyssalReward(int TypeId, int Quantity);

public static class AbyssalContent
{
    // Pinned build 3662168 filament types, ordered by tier within each weather.
    private static readonly int[][] s_filaments =
    [
        [56132, 47762, 47892, 47893, 47894, 47895, 56140],
        [56131, 47765, 47904, 47905, 47906, 47907, 56139],
        [56133, 47761, 47888, 47889, 47890, 47891, 56141],
        [56134, 47763, 47896, 47897, 47898, 47899, 56142],
        [56136, 47764, 47900, 47901, 47902, 47903, 56143],
    ];

    public static IEnumerable<AbyssalFilament> Filaments =>
        s_filaments.SelectMany((types, weather) => types.Select((type, tier) => new AbyssalFilament(type, tier, (AbyssalWeather)weather)));

    public static AbyssalFilament RequireFilament(int type) =>
        Filaments.SingleOrDefault(f => f.TypeId == type) ?? throw new InvalidDataException("This item is not an Abyssal filament.");

    public static int Capacity(AbyssalMode mode) =>
        mode switch
        {
            AbyssalMode.Cruiser => 1,
            AbyssalMode.Frigates => 3,
            AbyssalMode.Destroyers => 2,
            _ => throw new InvalidDataException("Unknown Abyssal game mode."),
        };

    public static long ShipList(AbyssalMode mode) =>
        mode switch
        {
            AbyssalMode.Cruiser => 57,
            AbyssalMode.Frigates => 92,
            AbyssalMode.Destroyers => 233,
            _ => throw new InvalidDataException("Unknown Abyssal game mode."),
        };

    public static int Nebula(AbyssalWeather weather, int room) =>
        weather switch
        {
            AbyssalWeather.Dark => AbyssalRules.Scene(room).NebulaGraphicId,
            AbyssalWeather.Electrical => room == 0 ? 22048 : 22206 + room,
            AbyssalWeather.Exotic => 22045 + room,
            AbyssalWeather.Firestorm => 22042 + room,
            AbyssalWeather.Gamma => 22212 + room,
            _ => throw new ArgumentOutOfRangeException(nameof(weather)),
        };

    public static int RoomAt(Eden.Gameplay.Flight.SpacePosition position) =>
        Math.Clamp((int)Math.Round(position.X / AbyssalRules.RoomSeparation), 0, AbyssalRules.RoomCount - 1);

    public static AbyssalFieldMask Disabled(int fields, int room) => (AbyssalFieldMask)((fields >> (room * 5)) & 31);

    // Eden encounter/reward distributions are authored policies, not retail drop-rate claims.
    public static int HostileCount(int tier, int room, int seed) => 1 + tier + (seed % 2 + room) % 2;

    public static int Overmind(int tier) =>
        tier switch
        {
            1 => 47876,
            2 => 47877,
            3 => 47878,
            4 => 47879,
            5 => 47880,
            6 => 56213,
            _ => throw new ArgumentOutOfRangeException(nameof(tier)),
        };

    public static IReadOnlyList<AbyssalReward> Rewards(int tier, AbyssalMode mode, AbyssalWeather weather, int room, int seed)
    {
        int scale = Capacity(mode) * (tier + 1);
        int next = Math.Min(6, tier + (room == 2 ? 1 : 0));
        int filament = s_filaments[(int)weather][next];
        List<AbyssalReward> rewards = [new(48121, scale), new(48112, 2 * scale), new(filament, Capacity(mode))];
        // Decayed/gravid/unstable stasis-webifier mutaplasmids; all qualities remain attainable.
        if ((seed % 3 + room) % 3 == 0)
        {
            int quality =
                tier >= 4 ? 47700
                : tier >= 2 ? 47701
                : 47699;
            rewards.Add(new(quality, Capacity(mode)));
        }
        return rewards;
    }

    public static int? DataSiteFilament(int seed) => seed % 4 == 0 ? s_filaments[(seed / 4) % 5][0] : null;
}
