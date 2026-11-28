using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Alliances;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed partial class CorporationLeadershipStore(WorldDatabase database, TimeProvider clock, AllianceStore alliances)
    : ICorporationLeadership
{
    public void PayDividend(long actorId, bool shareholders, long amountCents)
    {
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var corporation = RequireCeo(world, actorId);
        if (amountCents <= 0 || corporation.BalanceCents < amountCents)
        {
            throw new InvalidDataException("The dividend must fit within the corporation master wallet balance.");
        }
        var recipients = shareholders
            ? world
                .Set<SavedCorporationShareHolding>()
                .Where(row => row.CorporationId == corporation.Id && row.Shares > 0)
                .Select(row => new CorporationDividendRecipient(row.ShareholderId, row.Shares))
                .ToArray()
            : world
                .Pilots.Where(pilot => pilot.CorporationId == corporation.Id)
                .Select(pilot => new CorporationDividendRecipient(pilot.Id, 1))
                .ToArray();
        var payments = CorporationDividendRules.Allocate(amountCents, recipients);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        corporation.BalanceCents -= amountCents;
        CorporationWalletStore.AddEntry(
            world,
            corporation.Id,
            CorporationWalletRules.MasterKey,
            now,
            actorId,
            -amountCents,
            corporation.BalanceCents,
            WalletEntryType.CorporationDividendPayment,
            actorId,
            "Corporation dividend"
        );
        foreach (var payment in payments)
        {
            long balance;
            if (world.Pilots.Find(payment.OwnerId) is not null)
            {
                var wallet =
                    world.Set<PilotWallet>().Find(payment.OwnerId) ?? world.Add(new PilotWallet { PilotId = payment.OwnerId }).Entity;
                if (wallet.Cents > long.MaxValue - payment.Cents)
                {
                    throw new InvalidDataException("A recipient wallet cannot hold the dividend.");
                }
                wallet.Cents += payment.Cents;
                balance = wallet.Cents;
            }
            else if (payment.OwnerId <= int.MaxValue && world.Set<SavedCorporation>().Find((int)payment.OwnerId) is { } recipient)
            {
                if (recipient.BalanceCents > long.MaxValue - payment.Cents)
                {
                    throw new InvalidDataException("A recipient corporation wallet cannot hold the dividend.");
                }
                recipient.BalanceCents += payment.Cents;
                balance = recipient.BalanceCents;
            }
            else
            {
                throw new InvalidDataException("A shareholder no longer has a valid wallet.");
            }
            CorporationWalletStore.AddEntry(
                world,
                payment.OwnerId,
                CorporationWalletRules.MasterKey,
                now,
                corporation.Id,
                payment.Cents,
                balance,
                WalletEntryType.CorporationDividendPayment,
                actorId,
                "Corporation dividend"
            );
        }
        transaction.Commit();
    }

    public IReadOnlyList<long> Successors(long actorId) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                var corporation = RequireCeo(world, actorId);
                return Successors(world, corporation);
            }
        );

    public void Transfer(long actorId, long successorId)
    {
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var corporation = RequireCeo(world, actorId);
        if (world.Pilots.Find(actorId)!.ActiveShip.Position is not null)
        {
            throw new InvalidDataException("Dock before resigning as CEO.");
        }
        if (!Successors(world, corporation).Contains(successorId))
        {
            throw new InvalidDataException("Choose a current member with Corporation Management who accepts roles.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        ChangeCeo(world, corporation, successorId, now);
        transaction.Commit();
    }

    internal static void ChangeCeo(WorldAction world, SavedCorporation corporation, long successorId, long now, long? electionVoteId = null)
    {
        long actorId = corporation.CeoId;
        var former = CorporationRoleStore.Effective(world, actorId, corporation);
        var successor = CorporationRoleStore.Effective(world, successorId, corporation);
        corporation.CeoId = successorId;
        var previousAccess = CorporationRoleStore.Read(world, actorId, corporation.Id) with { RolesRemovedAt = now };
        if (electionVoteId.HasValue)
        {
            previousAccess = previousAccess with { Roles = new(), TitleMask = 0, AuthorityHoldVoteId = null };
            var successorAccess = CorporationRoleStore.Read(world, successorId, corporation.Id);
            CorporationRoleStore.Save(world, successorId, corporation.Id, successorAccess with { AuthorityHoldVoteId = electionVoteId });
        }
        var saved =
            world.Set<SavedCorporationMemberAccess>().Find(actorId)
            ?? world.Add(new SavedCorporationMemberAccess { PilotId = actorId, CorporationId = corporation.Id }).Entity;
        saved.DataJson = JsonSerializer.Serialize(previousAccess);
        var details = corporation.Snapshot(0).Details;
        string administration = JsonSerializer.Serialize(
            new CorporationAdministrationChange(corporation.Id, details, details, actorId, successorId)
        );
        string oldRoles = JsonSerializer.Serialize(
            new CorporationRoleChange(corporation.Id, actorId, former, CorporationRoleStore.Effective(world, actorId, corporation))
        );
        string newRoles = JsonSerializer.Serialize(
            new CorporationRoleChange(
                corporation.Id,
                successorId,
                successor,
                CorporationRoleStore.Effective(world, successorId, corporation)
            )
        );
        foreach (long member in world.Pilots.Where(pilot => pilot.CorporationId == corporation.Id).Select(pilot => pilot.Id))
        {
            world.Add(
                new SavedCorporationChange
                {
                    PilotId = member,
                    AdministrationJson = administration,
                    RoleJson = oldRoles,
                }
            );
            world.Add(new SavedCorporationChange { PilotId = member, RoleJson = newRoles });
        }
    }

    private static IReadOnlyList<long> Successors(WorldAction world, SavedCorporation corporation) =>
        world
            .Pilots.Where(pilot => pilot.CorporationId == corporation.Id && pilot.Id != corporation.CeoId)
            .AsEnumerable()
            .Where(pilot =>
                world.Set<PilotSkill>().Find(pilot.Id, CorporationRules.ManagementSkill) is not null
                && !CorporationRoleStore.Read(world, pilot.Id, corporation.Id).BlockRoles
                && !CorporationRoleStore.Read(world, pilot.Id, corporation.Id).AuthorityHoldVoteId.HasValue
            )
            .Select(pilot => pilot.Id)
            .Order()
            .ToArray();

    internal static SavedCorporation RequireCeo(WorldAction world, long actorId)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        return corporation.CeoId == actorId && CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.Director)
            ? corporation
            : throw new InvalidDataException("Only the CEO with active authority can perform this action.");
    }
}
