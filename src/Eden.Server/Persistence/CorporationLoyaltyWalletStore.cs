using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedCorporationLoyaltyBalance
{
    public int CorporationId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long Points { get; set; }
}

public sealed class SavedCorporationLoyaltyTransfer
{
    public long Id { get; set; }
    public long SourceOwnerId { get; set; }
    public int RecipientCorporationId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long Points { get; set; }
    public long SourceBefore { get; set; }
    public long SourceAfter { get; set; }
    public long RecipientBefore { get; set; }
    public long RecipientAfter { get; set; }
    public long Time { get; set; }

    public CorporationLoyaltyTransfer Snapshot() =>
        new(
            Id,
            SourceOwnerId,
            RecipientCorporationId,
            IssuerCorporationId,
            Points,
            SourceBefore,
            SourceAfter,
            RecipientBefore,
            RecipientAfter,
            Time
        );
}

[Injectable]
internal sealed class CorporationLoyaltyWalletStore(WorldDatabase database, TimeProvider clock) : ICorporationLoyaltyWallet
{
    public IReadOnlyList<LoyaltyBalance> Read(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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

    public CorporationLoyaltyTransfer Transfer(long actorId, bool fromCorporation, int recipientId, int issuerId, long points)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
        var recipient =
            world.Set<SavedCorporation>().Find(recipientId)
            ?? throw new InvalidDataException("Choose a player corporation as the LP recipient.");
        if (!catalog.Set<NpcCorporation>().Any(c => c.Id == issuerId))
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
        world.SaveChanges();
        transaction.Commit();
        return transfer.Snapshot();
    }
}
