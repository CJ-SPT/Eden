using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Notifications;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWarStore
{
    public IReadOnlyList<CorporationWarInvitation> Invitations(long pilotId)
    {
        long expired = Now - CorporationWarRules.InvitationLifetime;
        return Resident.Economy.Inspect(world =>
        {
            int owner = Organization(world, world.Pilots.Find(pilotId)?.CorporationId ?? 0);
            return world
                .Filter<SavedCorporationWarInvitation>(invite =>
                    (invite.FromOwnerId == owner || invite.ToOwnerId == owner) && invite.SentAt > expired
                )
                .OrderByDescending(row => row.SentAt)
                .AsEnumerable()
                .Select(row => new CorporationWarInvitation(row.FromOwnerId, row.ToOwnerId, row.SentAt))
                .ToArray();
        });
    }

    public void SendInvitation(long pilotId, int toOwnerId)
    {
        long now = Now;
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (fromOwner, _) = Authorization(world, pilotId);
        CheckOpponent(world, fromOwner, toOwnerId, now);
        if (world.Set<SavedCorporationWarInvitationSettings>().Find(toOwnerId) is not null)
        {
            throw new InvalidDataException("This organization has disabled mutual war invitations.");
        }
        long expired = now - CorporationWarRules.InvitationLifetime;
        var existing = world.Set<SavedCorporationWarInvitation>().Find(fromOwner, toOwnerId);
        if (existing?.SentAt > expired)
        {
            throw new InvalidDataException("There is already an invitation to mutual war.");
        }
        int outstanding = world
            .Filter<SavedCorporationWarInvitation>(invite => invite.FromOwnerId == fromOwner && invite.SentAt > expired)
            .Count();
        if (outstanding >= CorporationWarRules.MaximumOutstandingInvitations)
        {
            throw new InvalidDataException("Your organization already has five outgoing war invitations.");
        }
        var invite = existing ?? world.Add(new SavedCorporationWarInvitation { FromOwnerId = fromOwner, ToOwnerId = toOwnerId }).Entity;
        invite.SentAt = now;
        NotifyInvitation(world, fromOwner, toOwnerId, now, InboxNotificationType.WarMutualInvitation);
        world.Commit();
    }

    public void WithdrawInvitation(long pilotId, int toOwnerId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (fromOwner, _) = Authorization(world, pilotId);
        if (world.Set<SavedCorporationWarInvitation>().Find(fromOwner, toOwnerId) is { } removed)
        {
            world.Remove(removed);
            world.Commit();
        }
    }

    public void RespondInvitation(long pilotId, int fromOwnerId, bool accept)
    {
        long now = Now;
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (toOwner, _) = Authorization(world, pilotId);
        if (
            world.Set<SavedCorporationWarInvitation>().Find(fromOwnerId, toOwner) is not { } invite
            || invite.SentAt <= now - CorporationWarRules.InvitationLifetime
        )
        {
            throw new InvalidDataException("The mutual war invitation is unavailable.");
        }
        if (accept)
        {
            CheckOpponent(world, toOwner, fromOwnerId, now);
            var war = NewWar(world.Next(CorporationWarSchema.WarTable), fromOwnerId, toOwner, now);
            war.Mutual = true;
            world.Add(war);
            NotifyDeclared(world, war);
            if (world.Set<SavedCorporationWarInvitation>().Find(toOwner, fromOwnerId) is { } reciprocal)
            {
                world.Remove(reciprocal);
            }
        }
        NotifyInvitation(
            world,
            fromOwnerId,
            toOwner,
            now,
            accept ? InboxNotificationType.WarMutualAccepted : InboxNotificationType.WarMutualRejected
        );
        world.Remove(invite);
        world.Commit();
    }

    public bool InvitationsBlocked(long pilotId) =>
        Resident.Economy.Inspect(world =>
        {
            int owner = Organization(world, world.Pilots.Find(pilotId)?.CorporationId ?? 0);
            return world.Set<SavedCorporationWarInvitationSettings>().Find(owner) is not null;
        });

    public void SetInvitationsBlocked(long pilotId, bool blocked)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (owner, _) = Authorization(world, pilotId);
        var saved = world.Set<SavedCorporationWarInvitationSettings>().Find(owner);
        if (blocked && saved is null)
        {
            world.Add(new SavedCorporationWarInvitationSettings { OwnerId = owner });
        }
        else if (!blocked && saved is not null)
        {
            world.Remove(saved);
        }
        world.Commit();
    }
}
