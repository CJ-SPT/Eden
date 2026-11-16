using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Sovereignty;

internal sealed record HubDeploymentProfile(long CaptureDuration, double StarDistance, double Separation);

internal sealed record EntosisProfile(double Duration, double Capacitor, double Range, double SpeedLimit, int FuelType, int FuelQuantity);

internal sealed record TerritorialCaptureProfile(long Duration, double Regeneration);

internal static class EntosisCatalog
{
    private static readonly CatalogDefinition<int[]> s_constellationSystems = new(
        "sovereignty-constellation-systems",
        (catalog, id) =>
            catalog
                .Definitions.Systems.Values.Where(row => row.ConstellationId == id)
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .ToArray()
    );

    internal static IReadOnlyList<int> ConstellationSystems(CatalogDbContext catalog, int id) =>
        catalog.Definition(s_constellationSystems, id);

    internal const DogmaAttributeId DurationMultiplier = (DogmaAttributeId)2021;
    internal const DogmaAttributeId CapitalSize = (DogmaAttributeId)1785;
    internal const DogmaAttributeId SpeedLimitAttribute = (DogmaAttributeId)2033;
    private const DogmaAttributeId FuelType = (DogmaAttributeId)713;
    private const DogmaAttributeId FuelQuantity = (DogmaAttributeId)714;

    internal static HubDeploymentProfile Hub(CatalogDbContext catalog)
    {
        var components = catalog.Require(CatalogData.Client.Spacecomponentsbytype, (long)EntosisRules.HubTypeId);
        double Read(string component, string field) =>
            components.GetValueOrDefault(component)?.GetValueOrDefault(field) switch
            {
                ClientSpacecomponentsbytypeValueValueChoiceLong value => value.Value,
                ClientSpacecomponentsbytypeValueValueChoiceDouble value when double.IsFinite(value.Value) => value.Value,
                _ => throw new InvalidDataException($"Missing sovereignty hub {component}.{field}."),
            };
        var result = new HubDeploymentProfile(
            checked((long)(Read("entosisSovereigntyHub", "captureTimeSeconds") * TimeSpan.TicksPerSecond)),
            Read("deploy", "fixedStarDistance"),
            Read("deploy", "minDistanceFromStargatesAndStations")
        );
        if (result.CaptureDuration <= 0 || result.StarDistance <= 0 || result.Separation < 0)
        {
            throw new InvalidDataException("Invalid sovereignty hub deployment profile.");
        }
        return result;
    }

    internal static TerritorialCaptureProfile Defense(CatalogDbContext catalog, bool node)
    {
        var values = catalog.Require(
            CatalogData.Client.Spacecomponentsbytype,
            (long)(node ? SovereigntyWarfareRules.CommandNodeTypeId : EntosisRules.HubTypeId)
        )[node ? "entosisCommandNode" : "entosisSovereigntyHub"];
        double Read(string name) =>
            values[name] switch
            {
                ClientSpacecomponentsbytypeValueValueChoiceLong value => value.Value,
                ClientSpacecomponentsbytypeValueValueChoiceDouble value => value.Value,
                _ => throw new InvalidDataException("Missing territorial capture profile."),
            };
        return new(checked((long)(Read("captureTimeSeconds") * TimeSpan.TicksPerSecond)), Read("defensiveRegenRate"));
    }

    internal static bool Supported(CatalogDbContext catalog, int typeId) =>
        catalog.FittingType(typeId)?.GroupId == EntosisRules.ModuleGroup
        && catalog.Read(CatalogData.Client.Types, (long)typeId)?.Published == 1
        && FittingCatalog.DefaultEffect(catalog, typeId) == EntosisRules.Effect;

    internal static EntosisProfile Module(IReadOnlyDictionary<DogmaAttributeId, double> attributes, double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier < 0)
        {
            throw new InvalidDataException("Invalid Entosis duration multiplier.");
        }
        EntosisProfile profile = new(
            attributes.GetValueOrDefault(DogmaAttributeId.Duration) * (multiplier > 0 ? multiplier : 1),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            attributes.GetValueOrDefault(SpeedLimitAttribute),
            checked((int)attributes.GetValueOrDefault(FuelType)),
            checked((int)attributes.GetValueOrDefault(FuelQuantity))
        );
        if (
            !double.IsFinite(profile.Duration)
            || profile.Duration <= 0
            || !double.IsFinite(profile.Capacitor)
            || profile.Capacitor < 0
            || !double.IsFinite(profile.Range)
            || profile.Range <= 0
            || !double.IsFinite(profile.SpeedLimit)
            || profile.SpeedLimit <= 0
            || profile.FuelType <= 0
            || profile.FuelQuantity <= 0
        )
        {
            throw new InvalidDataException("Invalid Entosis module profile.");
        }
        return profile;
    }
}
