using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Structures;

public static class StructureRules
{
    public const long FirstStructureId = 1_000_000_000_000;
    public const int CategoryId = 65;
    public const int ServiceCategoryId = 66;
    public const int FirstServiceSlot = 164;
    public const int MaximumServiceSlots = 8;
    public const int FuelBayFlag = 172;
    public const int CoreRoomFlag = 180;
    public const int MaximumNameLength = 100;
    public const int MaximumDescriptionLength = 1000;
    public const double PlacementRange = 800_000;
    public const double Separation = 1_000_000;
    public const double HighSecurityBoundary = 0.45;
    public const long AnchoringDuration = TimeSpan.TicksPerDay;
    public const long RepairDuration = 15 * TimeSpan.TicksPerMinute;
    public const long FlexAnchoringDuration = 45 * TimeSpan.TicksPerMinute;
    public const long UnanchoringDuration = 7 * TimeSpan.TicksPerDay;
    public const long MoonDrillUnanchoringDuration = 3 * TimeSpan.TicksPerDay;
    public const long FuelInterval = TimeSpan.TicksPerHour;
    public const long AbandonmentDuration = 7 * TimeSpan.TicksPerDay;
    public const double FuelBayCapacity = 30_000;

    public static bool ServiceImplemented(StructureService service) =>
        service
            is StructureService.Docking
                or StructureService.Fitting
                or StructureService.Repair
                or StructureService.Insurance
                or StructureService.Reprocessing
                or StructureService.Industry
                or StructureService.Manufacturing
                or StructureService.BasicManufacturing
                or StructureService.CapitalManufacturing
                or StructureService.Laboratory
                or StructureService.TimeResearch
                or StructureService.MaterialResearch
                or StructureService.Copying
                or StructureService.Invention;

    public static int FuelAmount(StructureFamily family, IReadOnlyList<StructureService> services, double roleBonus, int amount)
    {
        bool matching = family switch
        {
            StructureFamily.Citadel => services.Any(s => s is StructureService.Market or StructureService.Medical),
            StructureFamily.EngineeringComplex => services.Any(s =>
                s
                    is StructureService.BasicManufacturing
                        or StructureService.CapitalManufacturing
                        or StructureService.SupercapitalManufacturing
                        or StructureService.Copying
                        or StructureService.MaterialResearch
                        or StructureService.TimeResearch
                        or StructureService.Invention
            ),
            StructureFamily.Refinery => services.Any(s =>
                s
                    is StructureService.Reprocessing
                        or StructureService.MoonMining
                        or StructureService.CompositeReactions
                        or StructureService.BiochemicalReactions
                        or StructureService.HybridReactions
            ),
            _ => false,
        };
        double multiplier = matching ? 1 + roleBonus / 100 : 1;
        return checked((int)Math.Ceiling(amount * multiplier));
    }

    public static bool Operational(StructureState state) =>
        state is StructureState.ShieldVulnerable or StructureState.ArmorReinforced or StructureState.ArmorVulnerable;

    public static bool CanDock(StructureProfile profile, StructureState state, int shipGroupId)
    {
        if (!profile.Dockable || state is StructureState.Unanchored or StructureState.Anchoring or StructureState.Destroyed)
        {
            return false;
        }
        // Freighters remain dockable as subcapitals. Only Keepstar-family citadels accept supercapitals.
        var hull = (HullClass)shipGroupId;
        bool supercapital = hull is HullClass.Titan or HullClass.Supercarrier;
        bool capital =
            supercapital
            || hull
                is HullClass.Dreadnought
                    or HullClass.Carrier
                    or HullClass.ForceAuxiliary
                    or HullClass.CapitalIndustrial
                    or HullClass.LancerDreadnought
                    or HullClass.CommandCarrier;
        return (profile.Family, profile.Size) switch
        {
            (StructureFamily.Citadel, StructureSize.ExtraLarge) => true,
            (StructureFamily.Citadel, StructureSize.Large) => !supercapital,
            (StructureFamily.EngineeringComplex, StructureSize.ExtraLarge) => !supercapital,
            (StructureFamily.Refinery, StructureSize.Large) => hull == HullClass.CapitalIndustrial || !capital,
            _ => !capital,
        };
    }

    public static void CheckServiceFit(StructureProfile structure, StructureServiceProfile module, int slot)
    {
        if (
            slot < FirstServiceSlot
            || slot >= FirstServiceSlot + structure.ServiceSlots
            || (module.AllowedGroups.Count > 0 || module.AllowedTypes.Count > 0)
                && !module.AllowedGroups.Contains(structure.GroupId)
                && !module.AllowedTypes.Contains(structure.TypeId)
        )
        {
            throw new InvalidDataException("The service module does not fit this structure or slot.");
        }
    }

    public static long AnchorDuration(StructureProfile profile) => profile.Dockable ? AnchoringDuration : FlexAnchoringDuration;

    public static long Duration(long ticks, decimal speed)
    {
        Configuration.WorldTuningRules.ValidateRate(speed);
        return checked((long)decimal.Ceiling(ticks / speed));
    }

    public static Flight.SpacePosition DeploymentPosition(Flight.SpacePosition ship, double x, double z)
    {
        var absolute = new Flight.SpacePosition(x, ship.Y, z);
        double relativeDistance = Math.Sqrt(x * x + z * z);
        // The pinned UI has relative-offset and absolute-coordinate deployment paths.
        // Match evejs's range-based disambiguation, preferring relative when both are plausible.
        return absolute.DistanceTo(ship) <= PlacementRange && relativeDistance > PlacementRange
            ? absolute
            : new(ship.X + x, ship.Y, ship.Z + z);
    }

    public static long UnanchorDuration(StructureProfile profile) =>
        profile.Family == StructureFamily.MoonDrill ? MoonDrillUnanchoringDuration
        : profile.Dockable ? UnanchoringDuration
        : FlexAnchoringDuration;

    public static string Name(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaximumNameLength || name.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a structure name of 1 to 100 printable characters.");
        }
        return name;
    }
}
