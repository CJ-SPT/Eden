using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Corporations;
using Eden.Data.World.Docking;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class CharacterDeletionStore(WorldDatabase database, TimeProvider clock) : ICharacterDeletion
{
    public CharacterDeletionState? Read(long accountId, long pilotId) =>
        WorldActionScope.Inspect<CharacterDeletionState?>(
            database,
            world =>
            {
                RequireOwner(world, accountId, pilotId);
                var saved = world.Set<SavedCharacterDeletion>().Find(pilotId);
                return saved is null ? null : new(saved.ReadyAt, saved.DeletedAt);
            }
        );

    public long Prepare(long accountId, long pilotId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
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
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        RequireOwner(world, accountId, pilotId);
        if (world.Set<SavedCharacterDeletion>().Find(pilotId) is { } saved)
        {
            world.Remove(saved);
        }
        transaction.Commit();
    }

    public void Delete(long accountId, long pilotId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        RequireOwner(world, accountId, pilotId);
        var saved = world.Set<SavedCharacterDeletion>().Find(pilotId);
        long now = Now;
        CharacterDeletionRules.RequireReady(saved is null ? null : new(saved.ReadyAt, saved.DeletedAt), now);
        RequireSettled(world, pilotId);
        // Keep identities, ownership and historical references intact. The tombstone
        // permanently removes account access and releases the slot without reusing IDs.
        saved!.DeletedAt = now;
        world.RemoveRange(world.Filter<QueuedLevel>(q => q.PilotId == pilotId).ToArray());
        var hulls = world.Filter<Ship>(ship => ship.OwnerId == pilotId).Select(ship => ship.Id).ToArray();
        world.RemoveRange(world.Filter<FlightPosition>(position => Enumerable.Contains(hulls, position.ShipId)).ToArray());
        transaction.Commit();
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static void RequireSettled(WorldAction world, long pilotId)
    {
        int corporation = world.Pilots.Find(pilotId)!.CorporationId;
        if (world.Set<SavedCorporation>().Find(corporation) is not null)
        {
            throw new InvalidDataException("Leave your player corporation before preparing for biomass.");
        }
        if (world.Filter<SavedPersonalOrder>(o => o.PilotId == pilotId && o.Status == PersonalOrderStatus.Open).Any())
        {
            throw new InvalidDataException("Close your market orders before preparing for biomass.");
        }
        if (
            world
                .Filter<IndustryJob>(j =>
                    j.PilotId == pilotId && (j.Status == IndustryJobStatus.Installed || j.Status == IndustryJobStatus.Ready)
                )
                .Any()
        )
        {
            throw new InvalidDataException("Deliver or cancel your industry jobs before preparing for biomass.");
        }
        var bids = world.Filter<SavedContractBid>(bid => bid.BidderId == pilotId).Select(bid => bid.ContractId).ToArray();
        if (
            world
                .Filter<SavedContract>(c =>
                    (c.IssuerId == pilotId || c.AcceptorId == pilotId || Enumerable.Contains(bids, c.Id))
                    && (
                        c.Status == ContractStatus.Outstanding
                        || c.Status == ContractStatus.InProgress
                        || c.Status == ContractStatus.FinishedIssuer
                        || c.Status == ContractStatus.FinishedContractor
                    )
                )
                .Any()
        )
        {
            throw new InvalidDataException("Settle your contracts before preparing for biomass.");
        }
    }

    private static void RequireOwner(WorldAction world, long accountId, long pilotId)
    {
        if (world.Pilots.Find(pilotId)?.AccountId != accountId || world.Set<SavedCharacterDeletion>().Find(pilotId)?.DeletedAt is not null)
        {
            throw new InvalidDataException("Unknown pilot for this account.");
        }
    }
}
