namespace Eden.Gameplay.Planetary;

public static class PlanetaryResources
{
    // inventorycommon.const planet type IDs and raw commodities. The eight ordinary
    // planet distributions are also documented by UniWiki's Planetary Commodities.
    public const int Temperate = 11;
    public const int Ice = 12;
    public const int Gas = 13;
    public const int Oceanic = 2014;
    public const int Lava = 2015;
    public const int Barren = 2016;
    public const int Storm = 2017;
    public const int Plasma = 2063;
    public const int Microorganisms = 2073;
    public const int BaseMetals = 2267;
    public const int AqueousLiquids = 2268;
    public const int NobleMetals = 2270;
    public const int HeavyMetals = 2272;
    public const int PlankticColonies = 2286;
    public const int ComplexOrganisms = 2287;
    public const int CarbonCompounds = 2288;
    public const int Autotrophs = 2305;
    public const int NonCsCrystals = 2306;
    public const int FelsicMagma = 2307;
    public const int SuspendedPlasma = 2308;
    public const int IonicSolutions = 2309;
    public const int NobleGas = 2310;
    public const int ReactiveGas = 2311;
    public const int MaximumBands = 30;
    private const double ConstantHarmonic = 0.28209479177387814;
    private const double LinearHarmonic = 0.4886025119029199;

    public static int Proximity(int remoteSensing, PlanetarySurveyAccess access)
    {
        if (remoteSensing is < 1 or > 5)
        {
            throw new InvalidDataException("Train Remote Sensing before surveying planetary resources.");
        }
        if (access.LocalOrOwned)
        {
            return 4;
        }
        if (!access.RemoteAllowed || access.Observer is null || access.Target is null)
        {
            throw new InvalidDataException("This planet cannot be surveyed remotely from your current system.");
        }
        const double LightYearMeters = 9_460_730_472_580_800;
        double distance = access.Observer.DistanceTo(access.Target) / LightYearMeters;
        if (distance > 2 * remoteSensing - 1)
        {
            throw new InvalidDataException("This planet is outside your Remote Sensing range.");
        }
        double[] ranges = [9, 7, 5, 3, 1];
        return Array.FindLastIndex(ranges, range => distance <= range);
    }

    public static PlanetaryResourceField Survey(
        PlanetaryPlanet planet,
        int typeId,
        int bands,
        PlanetarySurveySkills skills,
        PlanetarySurveyAccess access
    )
    {
        if (
            bands is < 1 or > MaximumBands
            || !Types(planet.TypeId).Contains(typeId)
            || skills.Planetology is < 0 or > 5
            || skills.AdvancedPlanetology is < 0 or > 5
        )
        {
            throw new InvalidDataException("Choose a resource native to this planet and a valid scan resolution.");
        }
        int proximity = Proximity(skills.RemoteSensing, access);
        // Pinned appConst.planetResourceProximityLimits; skills add one/two bands.
        int[] minimum = [2, 4, 6, 10, 15];
        int[] maximum = [6, 10, 15, 20, 30];
        int resolution = minimum[proximity] + skills.Planetology + 2 * skills.AdvancedPlanetology;
        int actualBands = Math.Min(bands, Math.Min(maximum[proximity], resolution));
        return new(actualBands, proximity, Coefficients(planet.Id, typeId, actualBands));
    }

    public static IReadOnlyList<int> Types(int planetType) =>
        planetType switch
        {
            Temperate => [AqueousLiquids, Autotrophs, CarbonCompounds, ComplexOrganisms, Microorganisms],
            Ice => [AqueousLiquids, HeavyMetals, Microorganisms, NobleGas, PlankticColonies],
            Gas => [AqueousLiquids, BaseMetals, IonicSolutions, NobleGas, ReactiveGas],
            Oceanic => [AqueousLiquids, CarbonCompounds, ComplexOrganisms, Microorganisms, PlankticColonies],
            Lava => [BaseMetals, FelsicMagma, HeavyMetals, NonCsCrystals, SuspendedPlasma],
            Barren => [AqueousLiquids, BaseMetals, CarbonCompounds, Microorganisms, NobleMetals],
            Storm => [AqueousLiquids, BaseMetals, IonicSolutions, NobleGas, SuspendedPlasma],
            Plasma => [BaseMetals, HeavyMetals, NobleMetals, NonCsCrystals, SuspendedPlasma],
            _ => [],
        };

    // Eden's reproducible resource field uses real spherical harmonics. These exact
    // float32 coefficients drive both the native scan bytes and authoritative yield.
    // No proprietary Tranquility resource distribution or depletion model is claimed.
    public static float[] Coefficients(int planetId, int typeId, int bands)
    {
        if (bands is < 1 or > MaximumBands)
        {
            throw new InvalidDataException("Choose between one and thirty resource bands.");
        }
        var values = new float[bands * bands];
        uint seed = unchecked((uint)planetId * 2654435761u ^ (uint)typeId * 2246822519u);
        values[0] = (float)((0.45 + (seed % 101) / 1000.0) / ConstantHarmonic);
        for (int i = 1; i < Math.Min(4, values.Length); i++)
        {
            seed = unchecked(seed * 1664525u + 1013904223u);
            values[i] = (float)(((seed % 2001) / 1000.0 - 1) * 0.22);
        }
        return values;
    }

    public static double Density(int planetId, int typeId, double latitude, double longitude)
    {
        var values = Coefficients(planetId, typeId, 2);
        double sin = Math.Sin(latitude);
        return Math.Clamp(
            values[0] * ConstantHarmonic
                + LinearHarmonic
                    * (-values[1] * sin * Math.Sin(longitude) + values[2] * Math.Cos(latitude) - values[3] * sin * Math.Cos(longitude)),
            0,
            1
        );
    }

    public static double AngularDistance(double latitude, double longitude, double otherLatitude, double otherLongitude) =>
        Math.Acos(
            Math.Clamp(
                Math.Cos(latitude) * Math.Cos(otherLatitude)
                    + Math.Sin(latitude) * Math.Sin(otherLatitude) * Math.Cos(longitude - otherLongitude),
                -1,
                1
            )
        );
}
