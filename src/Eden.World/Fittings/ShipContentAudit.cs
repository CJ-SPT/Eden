using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Drones;
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
            roles[role] = false;
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
            checks.Add(new(ContentOperation.Overheating, false, ShipContentMilestone.Overheating, "Native overload routes have no owner."));
        }
        AddDroneConsumers(effects, checks);
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
        if (rig && milestone is ShipContentMilestone.Exploration or ShipContentMilestone.InformationAndSalvage)
        {
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    false,
                    milestone,
                    "Rig fitting/modifiers work; complete scanning, hacking or salvage consumers remain unfinished."
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
            checks.Add(
                new(
                    ContentOperation.ConsumerCoverage,
                    false,
                    ShipContentMilestone.Overheating,
                    "Ancillary repair fuel is implemented; repair of overheated modules remains unfinished."
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
