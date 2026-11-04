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
        (NpcRegion)regionId switch
        {
            NpcRegion.TheForge
            or NpcRegion.ValeOfTheSilent
            or NpcRegion.Tribute
            or NpcRegion.Venal
            or NpcRegion.Lonetrek
            or NpcRegion.PureBlind
            or NpcRegion.Geminate
            or NpcRegion.TheCitadel
            or NpcRegion.Deklein
            or NpcRegion.Tenal
            or NpcRegion.Branch
            or NpcRegion.BlackRise => NpcPirateFaction.Guristas,
            NpcRegion.Detorid
            or NpcRegion.WickedCreek
            or NpcRegion.Cache
            or NpcRegion.ScaldingPass
            or NpcRegion.Insmother
            or NpcRegion.GreatWildlands
            or NpcRegion.Curse
            or NpcRegion.Immensea
            or NpcRegion.MoldenHeath
            or NpcRegion.Heimatar
            or NpcRegion.Impass
            or NpcRegion.Metropolis
            or NpcRegion.Feythabolis
            or NpcRegion.Tenerifis
            or NpcRegion.Omist => NpcPirateFaction.Angels,
            NpcRegion.TheBleakLands
            or NpcRegion.Khanid
            or NpcRegion.Querious
            or NpcRegion.Kador
            or NpcRegion.Aridia
            or NpcRegion.Delve
            or NpcRegion.PeriodBasis
            or NpcRegion.KorAzor
            or NpcRegion.Genesis => NpcPirateFaction.BloodRaiders,
            NpcRegion.Derelik
            or NpcRegion.Catch
            or NpcRegion.TashMurkon
            or NpcRegion.Stain
            or NpcRegion.Devoid
            or NpcRegion.Esoteria
            or NpcRegion.Domain
            or NpcRegion.Providence
            or NpcRegion.ParagonSoul => NpcPirateFaction.Sansha,
            NpcRegion.SinqLaison
            or NpcRegion.Everyshore
            or NpcRegion.Syndicate
            or NpcRegion.Solitude
            or NpcRegion.Fade
            or NpcRegion.Placid
            or NpcRegion.CloudRing
            or NpcRegion.OuterRing
            or NpcRegion.Fountain
            or NpcRegion.Essence
            or NpcRegion.VergeVendor => NpcPirateFaction.Serpentis,
            NpcRegion.Malpais
            or NpcRegion.TheSpire
            or NpcRegion.OuterPassage
            or NpcRegion.EtheriumReach
            or NpcRegion.TheKalevalaExpanse
            or NpcRegion.Oasa
            or NpcRegion.CobaltEdge
            or NpcRegion.PerrigenFalls => NpcPirateFaction.RogueDrones,
            _ => null,
        };
}
