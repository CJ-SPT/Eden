using Eden.Gameplay.Combat;
using Eden.Gameplay.Information;

namespace Eden.Gameplay.Exploration;

public sealed record ExplorationLootStack(int TypeId, int Quantity);

public static class ExplorationLootRules
{
    public const int Rolls = 2;
    public const int MaximumQuantity = 3;
    public const int SpecialPoolDivisor = 3;

    private const int EncryptionSkillbookRollDivisor = 12;
    private const int CondenserRollDivisor = 8;
    private const int MinimumCondenserQuantity = 4;
    private const int MaximumCondenserQuantity = 8;
    private const int LimeMalachiteCondenser = 57445;
    private const int AmberGoldenCondenser = 57446;
    private const int CeladonViridianCondenser = 57447;
    private const int AzureVermillionCondenser = 57448;

    // The four pirate encryption books use the same saved container seed as its other rewards.
    public static int? EncryptionSkillbook(int seed, NpcPirateFaction faction) =>
        seed % EncryptionSkillbookRollDivisor == 0
            ? faction switch
            {
                NpcPirateFaction.BloodRaiders or NpcPirateFaction.Sansha => 23087, // Amarr Encryption Methods
                NpcPirateFaction.Guristas => 21790, // Caldari Encryption Methods
                NpcPirateFaction.Serpentis => 23121, // Gallente Encryption Methods
                NpcPirateFaction.Angels => 21791, // Minmatar Encryption Methods
                _ => null,
            }
            : null;

    // Build 3662168 pirate Info Shards.
    private static bool IsInfoShard(int containerType) => containerType is 33188 or 33241 or 33249 or 33258 or 33266;

    public static ExplorationLootStack? MolecularCondenser(int seed, NpcPirateFaction faction, int containerType)
    {
        if (!IsInfoShard(containerType) || seed % CondenserRollDivisor != 0)
        {
            return null;
        }
        Random random = new(seed ^ LimeMalachiteCondenser);
        bool first = random.Next(2) == 0;
        int? type = faction switch
        {
            NpcPirateFaction.BloodRaiders => LimeMalachiteCondenser,
            NpcPirateFaction.Sansha => first ? AmberGoldenCondenser : LimeMalachiteCondenser,
            NpcPirateFaction.Guristas => first ? AmberGoldenCondenser : CeladonViridianCondenser,
            NpcPirateFaction.Angels or NpcPirateFaction.Serpentis => first ? AzureVermillionCondenser : CeladonViridianCondenser,
            _ => null,
        };
        return type is int id ? new(id, random.Next(MinimumCondenserQuantity, MaximumCondenserQuantity + 1)) : null;
    }

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
