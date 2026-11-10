using System.Collections.Immutable;
using Eden.Gameplay.Content;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Ships;

public enum HullCohort
{
    Special,
    TechTwoSubcapital,
    Small,
    Combat,
    Utility,
}

public sealed record HullAdmissionCohort(int TechLevel, HullCohort Cohort, ImmutableHashSet<int> Classes, ImmutableHashSet<int> Types);

public static class HullSupportRules
{
    public static bool CanAssemble(HullAssemblyProfile hull) => CanAssemble(hull, BuildContentPolicy.Current.Hulls.Cohorts);

    public static bool CanAssemble(HullAssemblyProfile hull, IReadOnlyList<HullAdmissionCohort> cohorts) =>
        hull.Category == InventoryCategory.Ship
        && hull.Published
        && cohorts.Any(cohort =>
            cohort.TechLevel == hull.TechLevel && (cohort.Types.Contains(hull.TypeId) || cohort.Classes.Contains((int)hull.Class))
        );

    public static bool IsTechTwoSubcapital(int typeId) =>
        BuildContentPolicy.Current.Hulls.Cohorts.Any(c => c.Cohort == HullCohort.TechTwoSubcapital && c.Types.Contains(typeId));

    public static bool IsClass(HullClass hullClass, HullCohort cohort, int techLevel) =>
        BuildContentPolicy.Current.Hulls.Cohorts.Any(entry =>
            entry.TechLevel == techLevel && entry.Cohort == cohort && entry.Classes.Contains((int)hullClass)
        );
}
