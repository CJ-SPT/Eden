using Eden.Gameplay.Combat;
using Eden.Gameplay.Information;

namespace Eden.Gameplay.Exploration;

public sealed record ExplorationLootStack(int TypeId, int Quantity);

public static class ExplorationLootRules
{
    public const int Rolls = 2;
    public const int MaximumQuantity = 3;
    public const int SpecialPoolDivisor = 3;

    // Initial Eden distribution, rather than a claim of recovered retail drop weights.
    public static IReadOnlyList<ExplorationLootStack> Roll(
        int seed,
        ExplorationKind kind,
        NpcPirateFaction faction,
        int securityBand,
        IReadOnlyList<int> datacores,
        IReadOnlyList<int> decryptors
    )
    {
        IReadOnlyList<int> pool;
        if (kind == ExplorationKind.Data)
        {
            pool = seed % SpecialPoolDivisor == 0 ? decryptors : datacores;
        }
        else
        {
            var family = faction switch
            {
                NpcPirateFaction.Guristas => SalvageFamily.Caldari,
                NpcPirateFaction.Angels => SalvageFamily.Minmatar,
                NpcPirateFaction.Serpentis => SalvageFamily.Gallente,
                NpcPirateFaction.Sansha => SalvageFamily.Sansha,
                NpcPirateFaction.BloodRaiders => SalvageFamily.BloodRaider,
                _ => throw new InvalidDataException("Unsupported pirate exploration reward faction."),
            };
            pool = SalvageLootRules
                .Pool(family, advanced: securityBand == 2 && seed % SpecialPoolDivisor == 0)
                .Select(t => (int)t)
                .ToArray();
        }
        if (pool.Count == 0)
        {
            throw new InvalidDataException("The exploration reward catalog is unavailable.");
        }
        Random random = new(seed);
        return Enumerable
            .Range(0, Rolls)
            .Select(_ => new ExplorationLootStack(pool[random.Next(pool.Count)], random.Next(1, MaximumQuantity + 1)))
            .ToArray();
    }
}
