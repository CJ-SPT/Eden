using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed partial class NpcPopulationSeeder
{
    internal static void RecordKill(WorldAction world, WorldNpc npc, long now)
    {
        var encounter = world.Set<WorldNpcEncounter>().Find(npc.EncounterId);
        if (
            encounter is null
            || encounter.Role is not (NpcRole.Pirate or NpcRole.Convoy)
            || world
                .Set<WorldNpc>()
                .Where(n => n.EncounterId == encounter.Id && n.Id != npc.Id && n.DestroyedAt == null && n.RetiredAt == null)
                .ToArray()
                .Any(n => n.DestroyedAt == null && n.RetiredAt == null)
        )
        {
            return;
        }
        encounter.Generation = checked(encounter.Generation + 1);
        int delay = encounter.Role == NpcRole.Convoy ? NpcConvoyRules.CooldownSeconds : NpcEncounterDefinitions.RespawnSeconds;
        encounter.NextSpawnAt = checked(now + delay * TimeSpan.TicksPerSecond);
    }
}
