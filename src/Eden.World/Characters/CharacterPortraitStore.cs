using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class CharacterPortraitStore(WorldDatabase database, CharacterDollCatalog dolls) : ICharacterPortraits
{
    public IReadOnlyList<CharacterPortraitSource> Read(IReadOnlyList<long> characterIds)
    {
        if (characterIds.Count > ICharacterPortraits.MaximumBatchSize || characterIds.Any(id => id <= 0))
        {
            throw new InvalidDataException("Invalid character portrait batch.");
        }
        if (characterIds.Count == 0)
        {
            return [];
        }
        using WorldDbContext world = new(database.Path);
        var pilots = world
            .Pilots.AsNoTracking()
            .Where(p =>
                characterIds.Contains(p.Id) && !world.Set<SavedCharacterDeletion>().Any(d => d.PilotId == p.Id && d.DeletedAt != null)
            )
            .Select(p => new
            {
                p.Id,
                p.Appearance,
                p.Portrait,
                p.BloodlineId,
                p.GenderId,
            })
            .ToArray();
        return pilots
            .Select(p => new CharacterPortraitSource(
                p.Id,
                p.Appearance,
                p.Portrait,
                p.Appearance is null ? dolls.Find(p.BloodlineId, p.GenderId) : null
            ))
            .ToArray();
    }
}

[Injectable(InjectionType.Singleton)]
internal sealed class CharacterDollCatalog(CatalogProvider catalog)
{
    private readonly Lazy<IReadOnlyDictionary<(long Bloodline, long Gender), CharacterDoll>> _defaults = new(() => Load(catalog));

    public CharacterDoll? Find(int bloodline, int gender) => _defaults.Value.GetValueOrDefault((bloodline, gender));

    private static IReadOnlyDictionary<(long, long), CharacterDoll> Load(CatalogProvider catalog)
    {
        Dictionary<(long, long), CharacterDoll> result = [];
        // Legacy seed pilots and simulated capsuleers have no creator submission.
        // Select a stable native doll of the same bloodline and gender, without
        // changing their saved profile or replacing a creator-authored appearance.
        foreach (var (id, npc) in catalog.Pin(CatalogData.Client.Npccharacters).OrderBy(entry => entry.Key))
        {
            if (npc.BloodlineID is not { } bloodline || npc.Gender is not { } gender || result.ContainsKey((bloodline, gender)))
            {
                continue;
            }
            if (catalog.Find(CatalogData.Client.Paperdolls, id) is not { Modifiers.Length: > 0 } doll)
            {
                continue;
            }
            result.Add(
                (bloodline, gender),
                new(
                    doll.HairDarkness ?? 0,
                    doll.Modifiers.Value.Select(m => new CharacterDollModifier(
                            m.ModifierLocationID ?? 0,
                            m.PaperdollResourceID ?? 0,
                            m.PaperdollResourceVariation ?? 0
                        ))
                        .ToArray(),
                    (doll.SculptWeights ?? [])
                        .Select(s => new CharacterDollSculpt(
                            s.SculptLocationID ?? 0,
                            s.WeightUpDown ?? 0,
                            s.WeightLeftRight ?? 0,
                            s.WeightForwardBack ?? 0
                        ))
                        .ToArray(),
                    (doll.ColorSelections ?? [])
                        .Select(c => new CharacterDollColor(
                            c.ColorID ?? 0,
                            c.ColorNameA ?? 0,
                            c.ColorNameBC ?? 0,
                            c.Weight ?? 0,
                            c.Gloss ?? 0
                        ))
                        .ToArray()
                )
            );
        }
        return result;
    }
}
