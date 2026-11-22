using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.Catalog.FactionWarfare;

internal sealed record MilitiaDefenderLoot(int WreckTypeId, int FirstTag, int SecondTag, int ThirdTag)
{
    // Build 3662168 empire small wrecks and the three tag grades used by the opposing militia's small weapon offers.
    private static readonly MilitiaDefenderLoot s_amarr = new(27052, 15610, 15992, 16000);
    private static readonly MilitiaDefenderLoot s_caldari = new(27049, 15599, 15600, 15601);
    private static readonly MilitiaDefenderLoot s_gallente = new(27055, 15590, 15994, 16002);
    private static readonly MilitiaDefenderLoot s_minmatar = new(27043, 15628, 15629, 15660);

    public static MilitiaDefenderLoot? For(MilitiaFaction faction) =>
        faction switch
        {
            MilitiaFaction.Amarr => s_amarr,
            MilitiaFaction.Caldari => s_caldari,
            MilitiaFaction.Gallente => s_gallente,
            MilitiaFaction.Minmatar => s_minmatar,
            _ => null,
        };

    public int Tag(uint seed) =>
        (seed % 3) switch
        {
            0 => FirstTag,
            1 => SecondTag,
            _ => ThirdTag,
        };
}
