using Eden.Data.Characters;
using Eden.Data.Contracts;
using Eden.Data.Corporations;
using Eden.Data.Industry;
using Eden.Data.Market;
using Eden.Data.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Contracts;
using Eden.World.Corporations;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class CharacterDeletionStore(WorldDatabase database, TimeProvider clock) : ICharacterDeletion
{
    public CharacterDeletionState? Read(long accountId, long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        RequireOwner(world, accountId, pilotId);
        var saved = world.Set<SavedCharacterDeletion>().Find(pilotId);
        return saved is null ? null : new(saved.ReadyAt, saved.DeletedAt);
    }

    public long Prepare(long accountId, long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        RequireOwner(world, accountId, pilotId);
        var saved = world.Set<SavedCharacterDeletion>().Find(pilotId);
        if (saved is null)
        {
            RequireSettled(world, pilotId);
            saved = new() { PilotId = pilotId, ReadyAt = CharacterDeletionRules.ReadyAt(Now) };
            world.Add(saved);
            world.SaveChanges();
        }
        transaction.Commit();
        return saved.ReadyAt;
    }

    public void Cancel(long accountId, long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        RequireOwner(world, accountId, pilotId);
        world.Set<SavedCharacterDeletion>().Where(d => d.PilotId == pilotId).ExecuteDelete();
        transaction.Commit();
    }

    public void Delete(long accountId, long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        RequireOwner(world, accountId, pilotId);
        var saved = world.Set<SavedCharacterDeletion>().Find(pilotId);
        long now = Now;
        CharacterDeletionRules.RequireReady(saved is null ? null : new(saved.ReadyAt, saved.DeletedAt), now);
        RequireSettled(world, pilotId);
        // Keep identities, ownership and historical references intact. The tombstone
        // permanently removes account access and releases the slot without reusing IDs.
        saved!.DeletedAt = now;
        world.Set<QueuedLevel>().Where(q => q.PilotId == pilotId).ExecuteDelete();
        world.Positions.Where(p => world.Ships.IgnoreQueryFilters().Any(s => s.OwnerId == pilotId && s.Id == p.ShipId)).ExecuteDelete();
        world.SaveChanges();
        transaction.Commit();
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static void RequireSettled(WorldDbContext world, long pilotId)
    {
        int corporation = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.CorporationId).Single();
        if (world.Set<SavedCorporation>().Any(c => c.Id == corporation))
        {
            throw new InvalidDataException("Leave your player corporation before preparing for biomass.");
        }
        if (world.Set<SavedPersonalOrder>().Any(o => o.PilotId == pilotId && o.Status == PersonalOrderStatus.Open))
        {
            throw new InvalidDataException("Close your market orders before preparing for biomass.");
        }
        if (
            world
                .Set<IndustryJob>()
                .Any(j => j.PilotId == pilotId && (j.Status == IndustryJobStatus.Installed || j.Status == IndustryJobStatus.Ready))
        )
        {
            throw new InvalidDataException("Deliver or cancel your industry jobs before preparing for biomass.");
        }
        if (
            world
                .Set<SavedContract>()
                .Any(c =>
                    (
                        c.IssuerId == pilotId
                        || c.AcceptorId == pilotId
                        || world.Set<SavedContractBid>().Any(b => b.ContractId == c.Id && b.BidderId == pilotId)
                    )
                    && (
                        c.Status == ContractStatus.Outstanding
                        || c.Status == ContractStatus.InProgress
                        || c.Status == ContractStatus.FinishedIssuer
                        || c.Status == ContractStatus.FinishedContractor
                    )
                )
        )
        {
            throw new InvalidDataException("Settle your contracts before preparing for biomass.");
        }
    }

    private static void RequireOwner(WorldDbContext world, long accountId, long pilotId)
    {
        if (
            !world.Pilots.Any(p => p.Id == pilotId && p.AccountId == accountId)
            || world.Set<SavedCharacterDeletion>().Any(d => d.PilotId == pilotId && d.DeletedAt != null)
        )
        {
            throw new InvalidDataException("Unknown pilot for this account.");
        }
    }
}
