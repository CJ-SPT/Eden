using Eden.Gameplay.Dogma;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;

namespace Eden.World.Structures;

internal static class StructureCatalog
{
    private const int RigSize = 1547;
    private const int CpuOutput = 48;
    private const int PowerOutput = 11;
    private const int ServiceSlots = 2056;
    private const int CoreType = 3101;
    private const int PrefitCoreType = 5701;
    private const int PrefitServiceType = 2792;
    private const int DisallowHighSecurity = 1970;
    private const int FuelAmount = 2109;
    private const int OnlineFuelAmount = 2110;
    private const int FuelGroup = 2108;
    private const int MaximumFitted = 2431;
    private static readonly int[] s_groupRestrictions = [1298, 1299, 1300, 1301];
    private static readonly int[] s_typeRestrictions = [1302, 1303, 1304, 1305, 1944, 2103, 2463, 2486, 2487, 2488, 2758];
    private static readonly CatalogDefinition<StructureProfile?> s_profiles = new("upwell-structure", Load);
    private static readonly CatalogDefinition<StructureServiceProfile?> s_services = new("upwell-service", LoadService);

    internal static StructureProfile Require(CatalogDbContext catalog, int typeId) =>
        catalog.Definition(s_profiles, typeId) ?? throw new InvalidDataException("This is not a published player Upwell structure.");

    internal static StructureServiceProfile Service(CatalogDbContext catalog, int typeId) =>
        catalog.Definition(s_services, typeId) ?? throw new InvalidDataException("This is not a supported structure service module.");

    internal static IEnumerable<StructureProfile> All(CatalogDbContext catalog) =>
        catalog
            .Definitions.Items.Values.Where(t => t.CategoryId == StructureRules.CategoryId)
            .Select(t => catalog.Definition(s_profiles, t.Id))
            .OfType<StructureProfile>();

    private static StructureProfile? Load(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.FittingType(typeId);
        if (
            type is null
            || type.CategoryId != StructureRules.CategoryId
            || catalog.Read(CatalogData.Client.Types, (long)typeId)?.Published != 1
        )
        {
            return null;
        }
        StructureFamily? family = type.GroupId switch
        {
            1657 => StructureFamily.Citadel,
            1404 => StructureFamily.EngineeringComplex,
            1406 => StructureFamily.Refinery,
            1408 => StructureFamily.JumpBridge,
            2017 => StructureFamily.CynoBeacon,
            2016 => StructureFamily.CynoJammer,
            4744 => StructureFamily.MoonDrill,
            _ => null,
        };
        if (family is null)
        {
            return null;
        }
        var a = catalog.FittingAttributes(typeId);
        double Value(int id) => a.GetValueOrDefault((DogmaAttributeId)id);
        int? Type(int id) => Value(id) > 0 ? checked((int)Value(id)) : null;
        bool dockable = family is StructureFamily.Citadel or StructureFamily.EngineeringComplex or StructureFamily.Refinery;
        var size = dockable ? (StructureSize)checked((int)Value(RigSize) - 1) : StructureSize.Flex;
        return new(
            typeId,
            type.Name,
            type.GroupId,
            family.Value,
            size,
            type.Radius ?? 0,
            Type(CoreType),
            Type(PrefitServiceType),
            checked((int)Value(ServiceSlots)),
            Value(CpuOutput),
            Value(PowerOutput),
            StructureRules.FuelBayCapacity,
            Value((int)DogmaAttributeId.ShieldCapacity),
            Value((int)DogmaAttributeId.ArmorHitPoints),
            Value((int)DogmaAttributeId.StructureHitPoints),
            Value(DisallowHighSecurity) != 0
        )
        {
            BuiltinCoreTypeId = Type(PrefitCoreType),
        };
    }

    private static StructureServiceProfile? LoadService(CatalogDbContext catalog, int typeId)
    {
        StructureService[] services = typeId switch
        {
            35892 => [StructureService.Market],
            35894 => [StructureService.Medical, StructureService.JumpClone],
            35899 => [StructureService.Reprocessing],
            35891 or 45550 => [StructureService.TimeResearch, StructureService.MaterialResearch, StructureService.Copying],
            35886 => [StructureService.Invention],
            35878 => [StructureService.BasicManufacturing],
            35881 => [StructureService.CapitalManufacturing],
            35877 => [StructureService.SupercapitalManufacturing, StructureService.CapitalManufacturing],
            45538 => [StructureService.HybridReactions],
            45537 => [StructureService.CompositeReactions],
            45539 => [StructureService.BiochemicalReactions],
            45009 => [StructureService.MoonMining],
            35913 => [StructureService.JumpBridge],
            35912 => [StructureService.CynoBeacon],
            35914 => [StructureService.CynoJammer],
            82941 => [StructureService.AutomaticMoonMining],
            78330 => [StructureService.LoyaltyStore],
            _ => [],
        };
        if (services.Length == 0 || catalog.Read(CatalogData.Client.Types, (long)typeId)?.Published != 1)
        {
            return null;
        }
        var a = catalog.FittingAttributes(typeId);
        double Value(int id) => a.GetValueOrDefault((DogmaAttributeId)id);
        return new(
            typeId,
            services,
            s_groupRestrictions.Select(id => checked((int)Value(id))).Where(id => id > 0).ToArray(),
            s_typeRestrictions.Select(id => checked((int)Value(id))).Where(id => id > 0).ToArray(),
            Value((int)DogmaAttributeId.Cpu),
            Value((int)DogmaAttributeId.Power),
            checked((int)Value(FuelGroup)),
            checked((int)Value(FuelAmount)),
            checked((int)Value(OnlineFuelAmount)),
            checked((int)Value(MaximumFitted))
        );
    }
}
