using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;

namespace Eden.World.Skills;

internal sealed record ExpertSystemProfile(long DurationDays, bool Activatable, IReadOnlyList<ExpertVirtualSkill> Skills);

internal static class ExpertSystemCatalog
{
    private static readonly CatalogDefinition<ExpertSystemProfile?> s_profile = new("expert-system-profile", Build);

    internal static ExpertSystemProfile? Read(CatalogDbContext catalog, int typeId) => catalog.Definition(s_profile, typeId);

    private static ExpertSystemProfile? Build(CatalogDbContext catalog, int typeId)
    {
        if (
            catalog.Read(CatalogData.Client.Expertsystems, (long)typeId)
                is not { DurationDays: long days, SkillsGranted: { } grants } source
            || days <= 0
        )
        {
            return null;
        }
        List<ExpertVirtualSkill> skills = [];
        foreach (var (key, level) in grants)
        {
            if (
                level is >= 1 and <= 5
                && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int skillId)
                && catalog.FittingType(skillId) is { CategoryId: (int)InventoryCategory.Skill }
            )
            {
                double rank = catalog.FittingAttributes(skillId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
                skills.Add(new(skillId, (int)level, rank));
            }
        }
        return skills.Count == 0 ? null : new(days, source.EsHidden is not > 0 && source.EsRetired is not > 0, skills.ToArray());
    }
}
