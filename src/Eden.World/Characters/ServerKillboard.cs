using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class ServerKillboard(WorldDatabase database, CatalogProvider catalog) : IServerKillboard
{
    private const int PageSize = 50;

    public KillboardPage? Read(KillboardFilter filter)
    {
        if (filter.Before is { Id: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return null;
        }
        var rows = live.Economy.Killboard(filter, PageSize + 1);
        var entries = rows.Take(PageSize)
            .Select(row => new KillboardEntry(
                row.Report.Cursor,
                catalog.Systems.GetValueOrDefault(row.Report.SystemId)?.Name ?? $"System {row.Report.SystemId}",
                Party(row.Report.Victim, row.Victim),
                Party(row.Report.Killer, row.Killer)
            ))
            .ToArray();
        return new(entries, rows.Length > PageSize ? entries[^1].Cursor : null);
    }

    private KillboardParty Party(IndexedCombatParty party, string? name)
    {
        string? ship = party.ShipTypeId is int type ? catalog.Items.GetValueOrDefault(type)?.Name ?? $"Type {type}" : null;
        return new(party.Kind, name ?? (party.CharacterId is long id ? $"Pilot {id}" : ship ?? "Unknown NPC"), ship);
    }
}
