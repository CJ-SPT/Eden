using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Data.World.Repair;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Repair;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private static IReadOnlyDictionary<long, CombatParty> Contributions(WorldAction world, long id) =>
        world
            .Set<SavedCombatDamage>()
            .Where(d => d.VictimId == id)
            .AsNoTracking()
            .ToArray()
            .ToImmutableDictionary(d => d.AttackerId, ReadContribution);
}
