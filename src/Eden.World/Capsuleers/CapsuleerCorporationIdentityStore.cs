using System.Text.Json;
using Eden.Data.World.AccessGroups;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

public sealed record CapsuleerCorporationNamePreview(int CorporationId, string CurrentName, string ProposedName, string Ticker);

[Injectable]
internal sealed class CapsuleerCorporationIdentityStore(WorldDatabase database)
{
    private ResidentEconomy Resident =>
        LiveWorld.Find(database.Path)?.Economy ?? throw new InvalidOperationException("The world is not running.");

    public CapsuleerCorporationNamePreview? Preview(int corporationId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Resident.Read(world => Propose(world, catalog, corporationId));
    }

    public bool Apply(CapsuleerCorporationNamePreview confirmed)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var world = Resident.Begin(TransactionParticipants.None);
        if (Propose(world, catalog, confirmed.CorporationId) != confirmed)
        {
            return false;
        }
        var corporation = world.Set<SavedCorporation>().Find(confirmed.CorporationId)!;
        var details = corporation.Snapshot(0).Details with { Name = confirmed.ProposedName };
        corporation.Name = details.Name;
        corporation.DetailsJson = JsonSerializer.Serialize(details);
        var group = world.Filter<SavedAccessGroup>(row => row.CreatorId == corporation.Id).SingleOrDefault(row => row.IsCorporation);
        if (group is not null)
        {
            group.Name = details.Name;
            world.AfterCommit(database.NotifyAccessGroupsChanged);
        }
        world.Commit();
        return true;
    }

    private static CapsuleerCorporationNamePreview? Propose(WorldAction world, CatalogDbContext catalog, int corporationId)
    {
        if (
            world.Set<SavedCorporation>().Find(corporationId) is not { ClosedAt: null, CapsuleerFoundingSystemId: int system } corporation
            || !CorporationMembershipStore.BotLed(world, corporation)
            || world.Filter<Pilot>(pilot => pilot.CorporationId == corporationId).Any(pilot => pilot.AccountId > 0)
            || world.Pilots.Find(corporation.CreatorId) is not { } founder
        )
        {
            return null;
        }
        long seed = world.Set<SavedCapsuleerProvisioning>().Find(1)?.WorldSeed ?? 0;
        string systemName = catalog.RequireSystem(system).Name;
        bool frontier = catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0;
        foreach (bool mining in new[] { true, false })
        {
            for (int attempt = 0; attempt < CapsuleerCorporationRules.MaximumIdentityAttempts; attempt++)
            {
                var legacy = CapsuleerCorporationRules.LegacyIdentity(systemName, seed, system, attempt, mining, frontier);
                if (legacy.Name == corporation.Name && legacy.Ticker == corporation.Ticker)
                {
                    return Replacement(world, catalog, corporation, founder.Name, systemName, seed, mining, frontier);
                }
            }
        }
        return null;
    }

    private static CapsuleerCorporationNamePreview? Replacement(
        WorldAction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        string founderName,
        string system,
        long seed,
        bool mining,
        bool frontier
    )
    {
        for (int attempt = 0; attempt < CapsuleerCorporationRules.MaximumIdentityAttempts; attempt++)
        {
            var candidate = CapsuleerCorporationRules.Identity(
                system,
                seed,
                corporation.CreatorId,
                founderName,
                attempt,
                mining,
                frontier
            ) with
            {
                Ticker = corporation.Ticker,
            };
            if (!PlayerCorporationStore.IdentityTaken(world, catalog, candidate, corporation.Id))
            {
                return new(corporation.Id, corporation.Name, candidate.Name, corporation.Ticker);
            }
        }
        return null;
    }
}
