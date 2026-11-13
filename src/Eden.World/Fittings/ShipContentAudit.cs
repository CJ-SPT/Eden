using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Content;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Defenses;
using Eden.World.Drones;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

// Offline diagnostics only. Admission checks describe current production support,
// not native-client acceptance, supply availability or proof of every gameplay path.
internal static class ShipContentAudit
{
    private const int FormatVersion = 1;
    private const int AbyssalMetaGroup = 15;
    private const int OverloadEffectCategory = 5;

    internal static IReadOnlySet<InventoryCategory> Categories { get; } =
        new HashSet<InventoryCategory>
        {
            InventoryCategory.Ship,
            InventoryCategory.Module,
            InventoryCategory.Charge,
            InventoryCategory.Drone,
            InventoryCategory.Subsystem,
            InventoryCategory.StructureModule,
            InventoryCategory.Fighter,
        };

    internal static ShipContentCoverageReport Read(CatalogDbContext catalog)
    {
        using var scope = new CatalogReadScope().Begin();
        var categories = Categories.Select(category => (int)category).ToArray();
        var entries = catalog
            .Set<ItemType>()
            .Include(type => type.Group)
            .Where(type => categories.Contains(type.Group.CategoryId))
            .OrderBy(type => type.Id)
            .ToArray()
            .Where(type => catalog.Read(CatalogData.Client.Types, (long)type.Id)?.Published == 1)
            .Select(type => ReadEntry(catalog, type))
            .ToArray();
        var totals = entries
            .GroupBy(entry => new { entry.Category, entry.State })
            .OrderBy(group => group.Key.Category)
            .ThenBy(group => group.Key.State)
            .Select(group => new ContentCoverageTotal(group.Key.Category, group.Key.State, group.Count()))
            .ToArray();
        return new(FormatVersion, BuildContentPolicy.PinnedBuild, catalog.Definitions.Identity.SourceFingerprint, totals, entries);
    }

    private static ShipContentCoverageEntry ReadEntry(CatalogDbContext catalog, ItemType type)
    {
        var source = catalog.Require(CatalogData.Client.Types, (long)type.Id);
        var category = (InventoryCategory)type.Group.CategoryId;
        var roadmap = ShipContentRoadmap.Current;
        var milestone = roadmap.ForGroup(type.GroupId);
        List<ContentCapabilityCheck> checks = [];
        List<ContentCoverageIssue> issues = [];
        bool admitted = false;
        var effects = FittingCatalog.Effects(catalog, type.Id).Select(effect => (int)effect).Order().ToArray();
        if (category == InventoryCategory.Module && effects.Contains((int)DogmaEffect.AssaultDamageControl))
        {
            milestone = ShipContentMilestone.AdvancedDefenses;
        }
        if (category == InventoryCategory.StructureModule || roadmap.ExternalGroups.Contains(type.GroupId))
        {
            issues.Add(new(ContentSupportState.ExternalDependency, ShipContentMilestone.StaticCatalogClosure, ExternalReason(category)));
        }
        else if (source.MetaGroupID == AbyssalMetaGroup || roadmap.SpecialGroups.Contains(type.GroupId))
        {
            issues.Add(
                new(
                    ContentSupportState.DeferredSpecial,
                    ShipContentMilestone.StaticCatalogClosure,
                    "Mutations or festival content are deferred."
                )
            );
        }
        else if (roadmap.NonfunctionalTypes.Contains(type.Id))
        {
            issues.Add(
                new(
                    ContentSupportState.Nonfunctional,
                    ShipContentMilestone.StaticCatalogClosure,
                    "Lux entry lacks normal fitting/firing definitions; see the energy-turret milestone 16 in docs/protocol/fitting.md."
                )
            );
        }
        else if (roadmap.UnverifiedGroups.Contains(type.GroupId) || category != InventoryCategory.Ship && milestone is null)
        {
            issues.Add(
                new(
                    ContentSupportState.EvidenceBlocked,
                    ShipContentMilestone.StaticCatalogClosure,
                    "No verified operational family classification. Review this entry; do not infer support from publication."
                )
            );
        }
        else
        {
            try
            {
                admitted = category switch
                {
                    InventoryCategory.Ship => Hull(
                        catalog,
                        type,
                        source.TechLevel is long tech ? checked((int)tech) : null,
                        checks,
                        issues
                    ),
                    InventoryCategory.Module => Module(catalog, type, milestone!.Value, checks),
                    InventoryCategory.Charge => Charge(catalog, type, milestone!.Value, checks),
                    InventoryCategory.Drone => Drone(catalog, type, milestone!.Value, checks),
                    _ => false,
                };
                const int reactiveArmorHardenerGroup = 1150;
                if (category == InventoryCategory.Module && type.GroupId == reactiveArmorHardenerGroup)
                {
                    issues.Add(
                        new(
                            ContentSupportState.EvidenceBlocked,
                            ShipContentMilestone.AdvancedDefenses,
                            "Pinned reactive-hardener damage sampling and adaptation timing remain unverified; "
                                + "fitting simulation is not server evidence."
                        )
                    );
                }
                if (checks.Count == 0)
                {
                    checks.Add(
                        new(ContentOperation.Operation, false, milestone!.Value, "No production subsystem or fighter operation owner.")
                    );
                }
                if (effects.Length != PassiveCatalog.Effects(catalog, type.Id).Count)
                {
                    issues.Add(
                        new(
                            ContentSupportState.EvidenceBlocked,
                            milestone ?? ShipContentMilestone.FittingCapabilities,
                            "One or more declared dogma effects have no catalog definition."
                        )
                    );
                }
            }
            catch (InvalidDataException error)
            {
                issues.Add(new(ContentSupportState.EvidenceBlocked, milestone ?? ShipContentMilestone.FittingCapabilities, error.Message));
            }
        }
        // A later role/bonus error must not erase an earlier successful admission check.
        admitted =
            checks
                .FirstOrDefault(check =>
                    check.Operation is ContentOperation.Assembly or ContentOperation.Fitting or ContentOperation.ChargeLoading
                )
                ?.Implemented
            ?? admitted;
        return new(
            type.Id,
            type.Name,
            type.GroupId,
            type.Group.Name,
            category,
            source.TechLevel is long level ? checked((int)level) : null,
            source.MetaGroupID is long meta ? checked((int)meta) : null,
            admitted,
            State(checks, issues),
            effects,
            checks,
            issues
        );
    }

    private static ContentSupportState State(IReadOnlyList<ContentCapabilityCheck> checks, IReadOnlyList<ContentCoverageIssue> issues)
    {
        if (issues.Count != 0)
        {
            return issues[0].State;
        }
        return checks.All(check => check.Implemented) ? ContentSupportState.Complete
            : checks.Any(check => check.Implemented) ? ContentSupportState.Partial
            : ContentSupportState.Planned;
    }

    private static string ExternalReason(InventoryCategory category) =>
        category switch
        {
            InventoryCategory.StructureModule => "Player-structure equipment is outside the ship-role program.",
            InventoryCategory.Fighter => "Structure fighter operations require the separate player-structure program.",
            _ => "Structure, moon-survey or sovereignty outcomes require separate world systems.",
        };

    private static bool Hull(
        CatalogDbContext catalog,
        ItemType type,
        int? tech,
        List<ContentCapabilityCheck> checks,
        List<ContentCoverageIssue> issues
    )
    {
        var hullClass = (HullClass)type.GroupId;
        var milestone = ShipContentRoadmap.ForHull(hullClass, tech);
        bool admitted = HullCatalog.CanAssemble(catalog, type, type.Group.CategoryId);
        checks.Add(new(ContentOperation.Assembly, admitted, milestone, "HullCatalog.CanAssemble / HullSupportRules.CanAssemble"));
        bool bonuses = HullCatalog.UsesCatalogBonuses(catalog, type.Id);
        checks.Add(
            new(ContentOperation.Bonuses, bonuses, milestone, "HullCatalog.UsesCatalogBonuses; tech-level admission remains explicit.")
        );
        var capabilities = HullCatalog.Capabilities(catalog, type.Id);
        var roles = capabilities
            .GroupBy(effect => effect.Capability)
            .ToDictionary(group => group.Key, group => bonuses && group.All(effect => effect.Operational));
        foreach (var role in HullBonusRules.DeferredRoles(type.Id, hullClass))
        {
            roles[role] = role == HullCapability.BreacherPods && admitted;
        }
        var attributes = catalog.FittingAttributes(type.Id);
        if (attributes.GetValueOrDefault(DogmaAttributeId.DroneCapacity) > 0)
        {
            roles[HullCapability.CombatDrones] =
                DroneRules.SupportsHull(type.Id) && (!roles.TryGetValue(HullCapability.CombatDrones, out bool implemented) || implemented);
        }
        AddBay(DogmaAttributeId.ShipMaintenanceBayCapacity, HullCapability.FleetRefitting);
        AddBay(DogmaAttributeId.FleetHangarCapacity, HullCapability.FleetAccess);
        AddBay(DogmaAttributeId.FrigateEscapeBayCapacity, HullCapability.FrigateEscapeRecovery);
        foreach (var (role, implemented) in roles.OrderBy(pair => pair.Key))
        {
            if (role == HullCapability.Unclassified)
            {
                string ids = string.Join(
                    ", ",
                    capabilities.Where(effect => effect.Capability == role).Select(effect => effect.EffectId).Order()
                );
                issues.Add(
                    new(
                        ContentSupportState.EvidenceBlocked,
                        ShipContentMilestone.FittingCapabilities,
                        "Unclassified hull modifier effects: " + ids + ". Their operational owners need evidence."
                    )
                );
            }
            checks.Add(
                new(
                    ContentOperation.Role,
                    implemented,
                    ShipContentRoadmap.ForRole(role),
                    "HullCatalog.Capabilities / HullBonusRules.DeferredRoles / DroneRules.SupportsHull / catalog bay attributes",
                    role
                )
            );
        }
        if (bonuses)
        {
            _ = HullCatalog.Modifiers(catalog, type.Id);
        }
        return admitted;

        void AddBay(DogmaAttributeId attribute, HullCapability role)
        {
            if (attributes.GetValueOrDefault(attribute) > 0)
            {
                roles[role] = false;
            }
        }
    }

    private static bool Module(CatalogDbContext catalog, ItemType type, ShipContentMilestone milestone, List<ContentCapabilityCheck> checks)
    {
        bool admitted = FittingCatalog.Definition(catalog, type.Id) is not null;
        checks.Add(new(ContentOperation.Fitting, admitted, milestone, "FittingCatalog.Definition; invokes the owning family validator."));
        if (!admitted)
        {
            return false;
        }
        if (
            type.GroupId == InventoryDefinitions.ModulatedMinerGroup
            && InventoryDefinitions.RequiredSkills.Any(p =>
                catalog.FittingAttributes(type.Id).GetValueOrDefault(p.Skill) == MiningDefinitions.DeepCoreMiningSkill
            )
        )
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    false,
                    ShipContentMilestone.Mining,
                    "Ordinary ore and crystals work; Mercoxit extraction and cloud hazards remain unavailable.",
                    HullCapability.DeepCoreMining
                )
            );
        }
        var effects = PassiveCatalog.Effects(catalog, type.Id);
        bool rig = RigRules.Groups.Contains(type.GroupId);
        checks.Add(
            new(
                ContentOperation.Operation,
                true,
                milestone,
                rig
                    ? "RigCatalog.Supported / PassiveCatalog.Parse; fitted modifiers admitted."
                    : "FittingCatalog.Definition admits an implemented active family or fully parsed passive profile."
            )
        );
        if (effects.Any(effect => effect.EffectCategory == OverloadEffectCategory))
        {
            string evidence = "Unverified overload calculation profile.";
            bool overheating = OverloadCatalog.Definition(catalog, type.Id) is not null;
            if (overheating)
            {
                evidence = type.GroupId switch
                {
                    SmartbombRules.Group => "Each paid untargeted pulse registers its shortened heat cycle.",
                    CombatDefinitions.EnergyWeaponGroup or CombatDefinitions.HybridWeaponGroup or CombatDefinitions.ProjectileWeaponGroup =>
                        "Single and grouped turrets pay the native overload rate or damage with their rack heat.",
                    CapacitorBoosterDefinitions.Group => "Injection spends a booster charge and registers its shortened heat cycle.",
                    HardenerDefinitions.ShieldGroup or HardenerDefinitions.ArmorGroup =>
                        "Paid overload modifies active resistances until the hardener stops or its deadline expires.",
                    _ when SupportModuleDefinitions.Effect(type.GroupId) is not null =>
                        "The paid local support cycle applies overload to the active fitting graph until stop or expiry.",
                    _ when MissileDefinitions.LauncherGroups.Contains(type.GroupId) =>
                        "Single and banked missile launches commit ammunition, cooldowns and rack heat together.",
                    _ => "The fitted module's overload program validates.",
                };
            }
            checks.Add(
                new(
                    ContentOperation.Overheating,
                    overheating,
                    ShipContentMilestone.Overheating,
                    evidence
                        + (
                            overheating
                                ? " Native controls, paid cycles, resident heat damage and burnout are implemented."
                                : " Overheating remains unavailable for this unverified profile."
                        )
                )
            );
        }
        AddDroneConsumers(effects, checks);
        var moduleAttributes = catalog.FittingAttributes(type.Id);
        if (
            type.GroupId == (int)MissileLauncherGroup.RapidTorpedo
            || CombatDefinitions.IsSupportedAmmoTurret(type.GroupId, moduleAttributes)
                && (
                    CombatDefinitions.IsDreadnoughtWeapon(moduleAttributes)
                    || moduleAttributes.GetValueOrDefault(DogmaAttributeId.CanFitShipType1) == CombatDefinitions.ZirnitraType
                )
        )
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    false,
                    ShipContentMilestone.CapitalModes,
                    "Fitting and firing are implemented. Eligible dreadnought hulls await capital hull admission."
                )
            );
        }
        if (effects.Any(e => e.EffectID == (int)DogmaEffect.AssaultDamageControl))
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    AssaultDamageControlCatalog.ConsumersAdmitted(catalog),
                    ShipContentMilestone.TechTwoSubcapitals,
                    "AssaultDamageControlCatalog.ConsumersAdmitted checks every published eligible assault frigate/cruiser and Herald."
                )
            );
        }
        if (type.GroupId == CapacitorWarfareRules.NeutralizerGroup)
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    false,
                    ShipContentMilestone.EnergyAndRemoteSupport,
                    "Player/NPC capacitor drains work; the NPC repair-suppression formula remains evidence-blocked."
                )
            );
        }
        if (type.GroupId == (int)InformationGroup.ShipScanner)
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    false,
                    ShipContentMilestone.InformationAndSalvage,
                    "Paid optimal-range ship scans work; exact server falloff and hardware sampling remain evidence-blocked."
                )
            );
        }
        if (
            type.GroupId == (int)InformationGroup.TractorBeam
            && InventoryDefinitions.RequiresSpecificShip(catalog.FittingAttributes(type.Id))
        )
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    false,
                    ShipContentMilestone.IndustrialCommand,
                    "Capital tractor profiles validate; their required capital industrial hull cohort remains unavailable."
                )
            );
        }
        if (rig && milestone is ShipContentMilestone.Exploration or ShipContentMilestone.InformationAndSalvage)
        {
            const int salvageAccessEffect = 2794;
            bool salvage = effects.Any(e => e.EffectID == salvageAccessEffect);
            bool probes = effects
                .SelectMany(e => e.ModifierInfo ?? [])
                .Any(m => m.ModifiedAttributeID is (int)DogmaAttributeId.ProbeStrength or (int)DogmaAttributeId.ProbeDeviation);
            bool hacking = effects
                .SelectMany(e => e.ModifierInfo ?? [])
                .Any(m => m.ModifiedAttributeID is (int)DogmaAttributeId.VirusCoherence or (int)DogmaAttributeId.VirusStrength);
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    salvage || probes || hacking,
                    milestone == ShipContentMilestone.InformationAndSalvage && !salvage ? ShipContentMilestone.Mining : milestone,
                    salvage ? "Salvage Tackle feeds the paid salvager's additive access chance."
                        : probes ? "Scanning rig modifiers feed authoritative probe strength and deviation."
                        : hacking ? "Analyzer rigs feed the authoritative hacking virus profile."
                        : "Rig fitting/modifiers work; the owning mining consumer remains unfinished."
                )
            );
        }
        return true;
    }

    private static void AddDroneConsumers(IReadOnlyList<ClientDogmaeffects> effects, List<ContentCapabilityCheck> checks)
    {
        HashSet<ShipContentMilestone> consumers = [];
        foreach (var source in effects.SelectMany(effect => effect.ModifierInfo ?? []))
        {
            if (source.Domain != "charID" || source.ModifiedAttributeID is not long attribute)
            {
                continue;
            }
            var selector = source.Func switch
            {
                "OwnerRequiredSkillModifier" => ModifierSelector.OwnerRequiredSkill,
                "ItemModifier" => ModifierSelector.Item,
                _ => (ModifierSelector?)null,
            };
            if (selector is null)
            {
                continue;
            }
            var capability = HullBonusRules.Capability(
                new(ModifierDomain.Character, selector.Value, (int?)source.SkillTypeID, (DogmaAttributeId)attribute)
            );
            ShipContentMilestone? consumer =
                source.SkillTypeID == DroneEquipmentRules.FighterOperationSkill
                    ? ShipContentMilestone.Fighters
                    : capability switch
                    {
                        HullCapability.CombatDrones => ShipContentMilestone.CombatDrones,
                        HullCapability.MiningDrones => ShipContentMilestone.Mining,
                        HullCapability.UtilityDrones => ShipContentMilestone.UtilityDrones,
                        _ => null,
                    };
            // The stasis rig selects utility drones through Drone Navigation.
            if (source.SkillTypeID == (int)DroneSkill.DroneNavigation && attribute == (int)DogmaAttributeId.SpeedFactor)
            {
                consumer = ShipContentMilestone.UtilityDrones;
            }
            if (consumer is { } stage)
            {
                consumers.Add(stage);
            }
        }
        foreach (var stage in consumers.Order())
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    stage == ShipContentMilestone.CombatDrones,
                    stage,
                    stage == ShipContentMilestone.CombatDrones
                        ? "DroneCatalog.Skilled / FittingCalculation; combat-drone attributes and control limits consume these effects."
                        : "Declared mining, utility-drone or fighter selectors retain their unfinished consumer milestone."
                )
            );
        }
    }

    private static bool Charge(CatalogDbContext catalog, ItemType type, ShipContentMilestone milestone, List<ContentCapabilityCheck> checks)
    {
        bool admitted = AmmunitionCatalog.Supported(catalog, type.Id, type.GroupId);
        checks.Add(new(ContentOperation.ChargeLoading, admitted, milestone, "AmmunitionCatalog.Supported; shared with real loading."));
        if (type.GroupId == AncillaryRepairRules.PasteGroup && admitted)
        {
            bool repair = NaniteRepairCatalog.PastePrice(catalog, type.Id) is not null;
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    repair,
                    ShipContentMilestone.Overheating,
                    repair
                        ? "PilotNaniteRepairStore spends cargo paste on module repairs. Ancillary armor repairers consume loaded paste."
                        : "Ancillary fuel is admitted. Paid module repair requires the recognized paste type and a positive catalog price."
                )
            );
        }
        return admitted;
    }

    private static bool Drone(CatalogDbContext catalog, ItemType type, ShipContentMilestone milestone, List<ContentCapabilityCheck> checks)
    {
        bool admitted;
        try
        {
            _ = DroneCatalog.Profile(catalog, type.Id);
            admitted = true;
        }
        catch (InvalidDataException)
        {
            admitted = false;
        }
        checks.Add(
            new(ContentOperation.Operation, admitted, milestone, "DroneCatalog.Profile; launch still requires an admitted drone hull.")
        );
        return admitted;
    }

    internal static string Serialize(ShipContentCoverageReport report)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(report, options) + "\n";
    }

    internal static void Export(string catalogPath, string output)
    {
        if (string.Equals(Path.GetFullPath(catalogPath), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Coverage output must not overwrite the catalog.");
        }
        using CatalogDbContext catalog = new(catalogPath);
        CatalogSources.CheckBuild(catalog);
        File.WriteAllText(output, Serialize(Read(catalog)));
    }
}
