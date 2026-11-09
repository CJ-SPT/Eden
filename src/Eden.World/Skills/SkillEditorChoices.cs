using System.Globalization;
using System.Text.RegularExpressions;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;

namespace Eden.World.Skills;

public sealed partial class SkillEditorStore
{
    // Build 3662168 Mining/Astrogeology percentage input (miningAmountBonus).
    private const int MiningYieldBonusAttribute = 434;

    private static bool SupportedEffect(int id, ClientDogmaeffects effect) =>
        id == SkillLevelEffect
        || effect.EffectCategory == (int)PassiveEffectPhase.Fitted
            && effect.ModifierInfo is { Length: > 0 } modifiers
            && modifiers.All(value =>
                PassiveCatalog.Parse(value, effect.EffectCategory.Value) is { Domain: ModifierDomain.Ship or ModifierDomain.Item }
            );

    private static SkillEditorChoices LoadChoices(WorldDatabase database)
    {
        using var scope = new CatalogConnectionScope().Begin();
        var catalog = CatalogProvider.Open(database.OriginalCatalogPath);
        var attributes = catalog.Pin(CatalogData.Client.Dogmaattributes);
        var effects = catalog.Pin(CatalogData.Client.Dogmaeffects);
        var markets = catalog.Pin(CatalogData.Client.Marketgroups);
        var icons = catalog.Pin(CatalogData.Client.Iconids);
        var units = catalog.Pin(CatalogData.Client.Dogmaunits);
        var skills = catalog.Items.Values.Where(item => item.CategoryId == SkillCategory).ToArray();
        var groupIds = skills.Select(skill => (long)skill.GroupId).Distinct().ToArray();
        var groups = catalog.FindMany(CatalogData.Client.Groups, groupIds);
        var allGroups = catalog.Pin(CatalogData.Client.Groups);
        var types = catalog.FindMany(CatalogData.Client.Types, skills.Select(skill => (long)skill.Id));
        var texts = catalog.FindMany(
            CatalogData.Client.LocalizationFsdEnUs,
            attributes
                .Values.SelectMany(value => new[] { value.DisplayNameID, value.TooltipDescriptionID })
                .Concat(effects.Values.SelectMany(value => new[] { value.DisplayNameID, value.DescriptionID }))
                .Concat(markets.Values.SelectMany(value => new[] { value.NameID, value.DescriptionID }))
                .Concat(units.Values.SelectMany(value => new[] { value.DisplayNameID, value.DescriptionID }))
                .Concat(allGroups.Values.Select(value => value.GroupNameID))
                .OfType<long>()
        );
        string Text(long? id) => id is long key ? texts.GetValueOrDefault(key)?.Text ?? "" : "";
        string Name(long? id, string? fallback) => Text(id) is { Length: > 0 } text ? text : Humanize(fallback);
        string AttributeName(long? id) =>
            id == (int)DogmaAttributeId.MiningAmount ? "Ore mining yield"
            : id is long key && attributes.TryGetValue(key, out var value) ? Name(value.DisplayNameID, value.Name)
            : "Unknown attribute";
        static string GameplayTerms(long? id) =>
            id switch
            {
                (int)DogmaAttributeId.MiningAmount or MiningYieldBonusAttribute =>
                    "ore mining yield miner laser strip volume bonus percent per level",
                (int)DogmaAttributeId.MaxVelocity => "speed velocity movement navigation",
                (int)DogmaAttributeId.Duration => "cycle duration time activation speed",
                (int)DogmaAttributeId.CapacitorNeed => "capacitor energy cost activation use",
                _ => "",
            };
        string Target(ClientDogmaeffectsModifierInfoItem modifier) =>
            modifier.Func switch
            {
                "LocationRequiredSkillModifier" => "fitted items requiring "
                    + (catalog.Items.GetValueOrDefault((int)(modifier.SkillTypeID ?? 0))?.Name ?? "the selected skill"),
                "LocationGroupModifier" =>
                    $"fitted items in {Name(allGroups.GetValueOrDefault(modifier.GroupID ?? 0)?.GroupNameID, $"group {modifier.GroupID}")}",
                "ItemModifier" when modifier.Domain == "itemID" => "this skill",
                "ItemModifier" when modifier.Domain == "shipID" => "your ship",
                _ => $"{modifier.Domain} / {modifier.Func}",
            };
        SkillEditorModifier Describe(ClientDogmaeffectsModifierInfoItem modifier)
        {
            string source =
                modifier.ModifyingAttributeID == (int)DogmaAttributeId.SkillLevel
                    ? "trained skill level"
                    : AttributeName(modifier.ModifyingAttributeID);
            string target = $"{AttributeName(modifier.ModifiedAttributeID)} on {Target(modifier)}";
            string description = (DogmaOperation?)modifier.Operation switch
            {
                DogmaOperation.PreMultiply or DogmaOperation.PostMultiply => $"Multiply {target} by {source}.",
                DogmaOperation.Add => $"Add {source} to {target}.",
                DogmaOperation.PostPercent => $"Change {target} by {source} percent (25 means +25%; -25 means -25%).",
                _ => $"{source} changes {target}; this operation cannot be edited here.",
            };
            return new(
                (int)(modifier.ModifyingAttributeID ?? 0),
                (int)(modifier.ModifiedAttributeID ?? 0),
                (DogmaOperation)(modifier.Operation ?? 0),
                modifier.Domain == "itemID" && modifier.Func == "ItemModifier",
                description,
                modifier.Domain == "shipID" && modifier.Func == "LocationRequiredSkillModifier" ? (int?)modifier.SkillTypeID : null
            );
        }
        string MarketPath(long id)
        {
            List<string> names = [];
            HashSet<long> visited = [];
            while (visited.Add(id) && markets.TryGetValue(id, out var group))
            {
                names.Add(Name(group.NameID, $"Market group {id}"));
                if (group.ParentGroupID is not long parent)
                {
                    break;
                }
                id = parent;
            }
            names.Reverse();
            return string.Join(" / ", names);
        }
        var iconSkills = types
            .Where(pair => pair.Value.IconID.HasValue)
            .GroupBy(pair => pair.Value.IconID!.Value)
            .ToDictionary(group => group.Key, group => group.Select(pair => catalog.Items[(int)pair.Key].Name).Order().ToArray());
        static SkillEditorOption[] Sort(IEnumerable<SkillEditorOption> values) => values.OrderBy(value => value.Name).ToArray();
        return new(
            Sort(
                groupIds.Select(id => new SkillEditorOption((int)id, Name(groups.GetValueOrDefault(id)?.GroupNameID, $"Skill group {id}")))
            ),
            Sort(
                attributes
                    .Where(pair => !ManagedAttribute((int)pair.Key))
                    .Select(pair =>
                    {
                        var value = pair.Value;
                        var unit = value.UnitID is long id ? units.GetValueOrDefault(id) : null;
                        string unitText = unit is null ? "" : $"Unit: {Name(unit.DisplayNameID, unit.Name)}. {Text(unit.DescriptionID)}";
                        return new SkillEditorOption(
                            (int)pair.Key,
                            Name(value.DisplayNameID, value.Name),
                            $"{value.Name}. {Text(value.TooltipDescriptionID)} {value.Description} {unitText}".Trim(),
                            SearchTerms: GameplayTerms(pair.Key)
                        )
                        {
                            DefaultValue = value.DefaultValue ?? 0,
                        };
                    })
            ),
            Sort(
                effects.Select(pair => new SkillEditorOption(
                    (int)pair.Key,
                    pair.Value.ModifierInfo is { Length: > 0 } modifiers
                        ? AttributeName(modifiers[0].ModifiedAttributeID)
                            + (
                                modifiers[0].ModifyingAttributeID == (int)DogmaAttributeId.SkillLevel ? " · scale by skill level"
                                : (DogmaOperation?)modifiers[0].Operation == DogmaOperation.PostPercent
                                    ? " · percent · " + Target(modifiers[0])
                                : " · " + Target(modifiers[0])
                            )
                        : Name(pair.Value.DisplayNameID, pair.Value.EffectName),
                    pair.Key == SkillLevelEffect
                        ? "Skill level marker; no ship or module bonus."
                        : string.Join(
                            " ",
                            new[] { pair.Value.EffectName, Text(pair.Value.DescriptionID) }.Concat(
                                (pair.Value.ModifierInfo ?? []).Select(modifier => Describe(modifier).Description)
                            )
                        ),
                    SupportedEffect((int)pair.Key, pair.Value),
                    (pair.Value.EffectName ?? "")
                        + " "
                        + string.Join(
                            " ",
                            (pair.Value.ModifierInfo ?? []).Select(modifier =>
                                GameplayTerms(modifier.ModifiedAttributeID) + " " + GameplayTerms(modifier.ModifyingAttributeID)
                            )
                        )
                )
                {
                    Phase = (PassiveEffectPhase?)pair.Value.EffectCategory,
                    Modifiers = (pair.Value.ModifierInfo ?? []).Select(Describe).ToArray(),
                })
            ),
            icons
                .OrderByDescending(pair => iconSkills.ContainsKey(pair.Key))
                .ThenBy(pair => pair.Value.IconFile)
                .Select(pair => new SkillEditorOption(
                    (int)pair.Key,
                    iconSkills.TryGetValue(pair.Key, out var names)
                        ? $"Skill icon · {string.Join(", ", names.Take(3))}"
                        : pair.Value.IconFile ?? $"Icon {pair.Key}",
                    pair.Value.IconFile ?? "Existing client icon",
                    SearchTerms: string.Join(", ", iconSkills.GetValueOrDefault(pair.Key) ?? [])
                ))
                .ToArray(),
            Sort(markets.Select(pair => new SkillEditorOption((int)pair.Key, MarketPath(pair.Key), Text(pair.Value.DescriptionID))))
        );
    }

    private static string Humanize(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? "Unnamed catalog entry"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(name.Replace('_', ' '), "([a-z0-9])([A-Z])", "$1 $2"));
}
