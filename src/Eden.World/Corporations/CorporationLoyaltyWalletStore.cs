using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationLoyaltyWalletStore(WorldDatabase database, TimeProvider clock) : ICorporationLoyaltyWallet
{
    public IReadOnlyList<LoyaltyBalance> Read(long actorId) =>
        LiveReadModels.Find(database.Path)?.CorporationLoyalty(actorId, () => ReadSql(actorId)) ?? ReadSql(actorId);

    private IReadOnlyList<LoyaltyBalance> ReadSql(long actorId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<LoyaltyBalance>>(
            database,
            world =>
            {
                var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
                var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
                if (
                    corporation is null
                    || !CorporationRoleStore
                        .Effective(world, actorId, corporation)
                        .Roles.Has(CorporationRole.Accountant | CorporationRole.JuniorAccountant)
                )
                {
                    return [];
                }
                return world
                    .Set<SavedCorporationLoyaltyBalance>()
                    .Where(b => b.CorporationId == corporation.Id && b.Points > 0)
                    .OrderBy(b => b.IssuerCorporationId)
                    .Select(b => new LoyaltyBalance(b.IssuerCorporationId, b.Points))
                    .ToArray();
            }
        );
    }

    public CorporationLoyaltyTransfer Transfer(long actorId, bool fromCorporation, int recipientId, int issuerId, long points)
    {
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
        var recipient =
            world.Set<SavedCorporation>().Find(recipientId)
            ?? throw new InvalidDataException("Choose a player corporation as the LP recipient.");
        if (catalog.NpcCorporationName(issuerId) is null)
        {
            throw new InvalidDataException("Choose a known loyalty point issuer.");
        }
        SavedCorporation? sourceCorporation = fromCorporation ? CorporationRoleStore.Current(world, actorId) : null;
        long sourceOwner = sourceCorporation?.Id ?? actorId;
        if (
            sourceOwner == recipient.Id
            || sourceCorporation is not null && !CorporationRoleStore.Has(world, actorId, sourceCorporation, CorporationRole.Accountant)
        )
        {
            throw new InvalidDataException("Corporation LP transfers require Accountant or Director access and a different recipient.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (issuerId == CurrencyRewardRules.ParagonCorporation)
        {
            if (fromCorporation || recipient.Id != pilot.CorporationId)
            {
                throw new InvalidDataException("Standalone EverMark donations go from a pilot to their own corporation.");
            }
            long joined =
                world
                    .Set<SavedCorporationEmployment>()
                    .Where(e => e.PilotId == actorId)
                    .OrderByDescending(e => e.Id)
                    .Select(e => (long?)e.StartedAt)
                    .FirstOrDefault()
                ?? pilot.CreatedAt;
            if (now - joined < CorporationLoyaltyRules.EverMarkMembership.Ticks)
            {
                throw new InvalidDataException("EverMark donations require 30 days of membership in your corporation.");
            }
        }
        var personal = fromCorporation ? null : world.Set<SavedLoyaltyBalance>().Find(actorId, issuerId);
        var corporate = sourceCorporation is null ? null : world.Set<SavedCorporationLoyaltyBalance>().Find(sourceCorporation.Id, issuerId);
        var destination = world.Set<SavedCorporationLoyaltyBalance>().Find(recipient.Id, issuerId);
        long before = fromCorporation ? corporate?.Points ?? 0 : personal?.Points ?? 0;
        long destinationBefore = destination?.Points ?? 0;
        CorporationLoyaltyRules.Check(before, destinationBefore, points);
        if (fromCorporation)
        {
            corporate!.Points -= points;
        }
        else
        {
            personal!.Points -= points;
        }
        destination ??= world
            .Add(new SavedCorporationLoyaltyBalance { CorporationId = recipient.Id, IssuerCorporationId = issuerId })
            .Entity;
        destination.Points += points;
        var transfer = world
            .Add(
                new SavedCorporationLoyaltyTransfer
                {
                    SourceOwnerId = sourceOwner,
                    RecipientCorporationId = recipient.Id,
                    IssuerCorporationId = issuerId,
                    Points = points,
                    SourceBefore = before,
                    SourceAfter = before - points,
                    RecipientBefore = destinationBefore,
                    RecipientAfter = destination.Points,
                    Time = now,
                }
            )
            .Entity;
        transaction.Commit();
        return transfer.Snapshot();
    }
}
