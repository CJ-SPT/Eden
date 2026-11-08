using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private IReadOnlyList<NpcEwarContact>? EwarContacts(long sourceId)
    {
        var contacts =
            owner.Reads?.NpcEwarSource(sourceId)
            ?? Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                return world
                    .Set<WorldNpcEwar>()
                    .Where(e => e.NpcId == sourceId && e.AppliedJson != null)
                    .AsNoTracking()
                    .AsEnumerable()
                    .Select(e => e.Contact()!)
                    .ToArray();
            });
        return contacts.Count == 0 ? null : contacts;
    }

    internal IReadOnlyList<NpcEwarSource>? EwarSources(long shipId, long now)
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return null;
        }
        List<NpcEwarSource> sources = [];
        foreach (var aggression in Aggression().Values.Where(a => a.ShipId == shipId))
        {
            var npc = Npc(aggression.NpcId, now);
            if (
                npc is not { Available: true }
                || NpcPopulationRules.IsCivilian(npc.Role)
                || npc.Transit is not null
                || npc.ElectronicWarfare.Count == 0
                || !PlayerEwarRules.CanTarget(npc.IncomingElectronicWarfare, shipId)
            )
            {
                continue;
            }
            sources.Add(
                new(
                    npc.Id,
                    npc.TypeId,
                    npc.SystemId,
                    npc.Position,
                    npc.Home,
                    npc.Radius,
                    NpcPopulationRules.Leash(npc.Attributes, npc.MissileRange, npc.AcquisitionRange),
                    aggression.ReadyAt,
                    npc.ElectronicWarfare
                )
            );
        }
        return sources;
    }
}
