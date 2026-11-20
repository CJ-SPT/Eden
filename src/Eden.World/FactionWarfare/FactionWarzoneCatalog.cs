using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Gameplay.FactionWarfare;

namespace Eden.World.FactionWarfare;

internal static class FactionWarzoneCatalog
{
    // The two empire warzones each have a pirate faction in the native occupation object.
    internal static readonly ImmutableArray<WarzoneDefinition> Warzones =
    [
        new(WarzoneId.AmarrMinmatar, MilitiaFaction.Amarr, MilitiaFaction.Minmatar, 500011), // Angel Cartel
        new(WarzoneId.CaldariGallente, MilitiaFaction.Caldari, MilitiaFaction.Gallente, 500010), // Guristas
    ];

    // CCP Havoc expansion notes, resolved to system IDs in the pinned catalog.
    internal static readonly ImmutableArray<MilitiaHeadquarters> Headquarters =
    [
        new(MilitiaFaction.Caldari, 30045324), // Onnamon
        new(MilitiaFaction.Minmatar, 30002055), // Amo
        new(MilitiaFaction.Amarr, 30002974), // Mehatoor
        new(MilitiaFaction.Gallente, 30003788), // Intaki
    ];

    private static readonly FrozenDictionary<MilitiaFaction, WarzoneDefinition> ByFaction = Warzones
        .SelectMany(zone => new[] { (zone.FirstFaction, zone), (zone.SecondFaction, zone) })
        .ToFrozenDictionary(entry => entry.Item1, entry => entry.zone);

    internal static readonly ImmutableArray<WarzoneSeed> Roster = ReadRoster();
    internal static readonly FrozenDictionary<int, WarzoneSeed> Systems = Roster.ToFrozenDictionary(seed => seed.SystemId);

    internal static WarzoneDefinition Warzone(MilitiaFaction originalOccupier) => ByFaction[originalOccupier];

    private static ImmutableArray<WarzoneSeed> ReadRoster()
    {
        using var stream =
            typeof(WarzoneSeed).Assembly.GetManifestResourceStream("Eden.Data.WarzoneRoster.json")
            ?? throw new InvalidDataException("The pinned faction warfare roster is missing.");
        var roster = JsonSerializer.Deserialize<ImmutableArray<WarzoneSeed>>(stream, JsonSerializerOptions.Web);
        if (roster.IsDefaultOrEmpty)
        {
            throw new InvalidDataException("The pinned faction warfare roster is empty.");
        }
        return roster;
    }
}
