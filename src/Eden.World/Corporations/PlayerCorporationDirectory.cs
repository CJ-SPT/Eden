using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class PlayerCorporationDirectory(WorldDatabase database) : IPlayerCorporationDirectory
{
    public IReadOnlyList<CorporationDirectoryEntry> Read(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        if (LiveWorld.Find(database.Path) is { } live)
        {
            var snapshot = live.OwnerDirectory.Snapshot;
            return ids.Distinct()
                .Select(id => snapshot.Corporations.GetValueOrDefault(id)?.Entry)
                .OfType<CorporationDirectoryEntry>()
                .OrderBy(entry => entry.Id)
                .ToArray();
        }
        using WorldDbContext world = new(database.Path) { Participants = TransactionParticipants.None };
        return world
            .Set<SavedCorporation>()
            .AsNoTracking()
            .Where(corporation => ids.Contains(corporation.Id))
            .OrderBy(corporation => corporation.Id)
            .Select(corporation => new { corporation.Id, corporation.DetailsJson })
            .AsEnumerable()
            .Select(corporation =>
            {
                var details = JsonSerializer.Deserialize<CorporationDetails>(corporation.DetailsJson)!;
                return new CorporationDirectoryEntry(corporation.Id, details.Name, details.Ticker, details.Logo);
            })
            .ToArray();
    }
}
