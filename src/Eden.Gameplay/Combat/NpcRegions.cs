using Eden.Gameplay.Content;

namespace Eden.Gameplay.Combat;

// Pinned build 3662168 region IDs. Developer regions and special spaces are
// deliberately absent from the ordinary known-space population mapping.
public enum NpcRegion
{
    Derelik = 10000001,
    TheForge = 10000002,
    ValeOfTheSilent = 10000003,
    Detorid = 10000005,
    WickedCreek = 10000006,
    Cache = 10000007,
    ScaldingPass = 10000008,
    Insmother = 10000009,
    Tribute = 10000010,
    GreatWildlands = 10000011,
    Curse = 10000012,
    Malpais = 10000013,
    Catch = 10000014,
    Venal = 10000015,
    Lonetrek = 10000016,
    TheSpire = 10000018,
    TashMurkon = 10000020,
    OuterPassage = 10000021,
    Stain = 10000022,
    PureBlind = 10000023,
    Immensea = 10000025,
    EtheriumReach = 10000027,
    MoldenHeath = 10000028,
    Geminate = 10000029,
    Heimatar = 10000030,
    Impass = 10000031,
    SinqLaison = 10000032,
    TheCitadel = 10000033,
    TheKalevalaExpanse = 10000034,
    Deklein = 10000035,
    Devoid = 10000036,
    Everyshore = 10000037,
    TheBleakLands = 10000038,
    Esoteria = 10000039,
    Oasa = 10000040,
    Syndicate = 10000041,
    Metropolis = 10000042,
    Domain = 10000043,
    Solitude = 10000044,
    Tenal = 10000045,
    Fade = 10000046,
    Providence = 10000047,
    Placid = 10000048,
    Khanid = 10000049,
    Querious = 10000050,
    CloudRing = 10000051,
    Kador = 10000052,
    CobaltEdge = 10000053,
    Aridia = 10000054,
    Branch = 10000055,
    Feythabolis = 10000056,
    OuterRing = 10000057,
    Fountain = 10000058,
    ParagonSoul = 10000059,
    Delve = 10000060,
    Tenerifis = 10000061,
    Omist = 10000062,
    PeriodBasis = 10000063,
    Essence = 10000064,
    KorAzor = 10000065,
    PerrigenFalls = 10000066,
    Genesis = 10000067,
    VergeVendor = 10000068,
    BlackRise = 10000069,
}

public enum NpcPirateFaction
{
    Guristas = 500010,
    Angels = 500011,
    BloodRaiders = 500012,
    Sansha = 500019,
    Serpentis = 500020,
    RogueDrones = 500025,
}

public static class NpcRegions
{
    public static NpcPirateFaction? Pirates(int regionId) =>
        BuildContentPolicy.Current.Npcs.PirateRegions.TryGetValue(regionId, out var faction) ? faction : null;
}
