using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal sealed partial class PlayerEwarStore
{
    internal static IReadOnlyList<ElectronicWarfareContact> Active(WorldAction world, long targetId) =>
        world
            .Set<WorldPlayerEwar>()
            .Where(e => e.TargetId == targetId)
            .OrderBy(e => e.ModuleId)
            .AsNoTracking()
            .AsEnumerable()
            .Select(e => e.Contact())
            .ToArray();

    internal static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        WorldAction world,
        long npcId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double missileRange = 0
    ) => PlayerEwarRules.NpcAttributes(attributes, Active(world, npcId), missileRange);
}
