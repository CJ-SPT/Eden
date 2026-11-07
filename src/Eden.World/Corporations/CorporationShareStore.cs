using System.Text.Json;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Corporations;

public sealed class SavedCorporationShareIssue
{
    public int CorporationId { get; set; }
    public long Shares { get; set; }
}

public sealed class SavedCorporationShareHolding
{
    public int CorporationId { get; set; }
    public long ShareholderId { get; set; }
    public long Shares { get; set; }
}

public sealed class SavedCorporationShareTransfer
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public long ActorId { get; set; }
    public long FromOwnerId { get; set; }
    public long ToOwnerId { get; set; }
    public long Shares { get; set; }
    public long Time { get; set; }
}

[Injectable]
internal sealed class CorporationShareStore(WorldDatabase database, TimeProvider clock) : ICorporationShares
{
    public IReadOnlyList<CorporationShareHolding> Holdings(long actorId, bool corporation)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown shareholder.");
        long owner = actorId;
        if (corporation)
        {
            var current = CorporationRoleStore.Current(world, actorId);
            RequireFinancial(world, actorId, current);
            owner = current.Id;
        }
        return world
            .Set<SavedCorporationShareHolding>()
            .Where(h => h.ShareholderId == owner && h.Shares > 0)
            .OrderBy(h => h.CorporationId)
            .AsEnumerable()
            .Select(h => Snapshot(world, h))
            .ToArray();
    }

    public IReadOnlyList<CorporationShareHolding> Shareholders(long actorId, int corporationId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown shareholder.");
        var issuer = world.Set<SavedCorporation>().Find(corporationId) ?? throw new InvalidDataException("Unknown share issuer.");
        bool ownFinancial =
            actor.CorporationId == issuer.Id
            && CorporationRoleStore.Has(world, actorId, issuer, CorporationRole.Accountant | CorporationRole.JuniorAccountant);
        bool privateHolder = world.Set<SavedCorporationShareHolding>().Find(corporationId, actorId)?.Shares > 0;
        if (!ownFinancial && !privateHolder)
        {
            throw new InvalidDataException("Financial authority or personal shares in this corporation are required.");
        }
        return world
            .Set<SavedCorporationShareHolding>()
            .Where(h => h.CorporationId == corporationId && h.Shares > 0)
            .OrderBy(h => h.ShareholderId)
            .AsEnumerable()
            .Select(h => Snapshot(world, h))
            .ToArray();
    }

    public void Transfer(long actorId, CorporationShareTransfer request)
    {
        if (request.Shares <= 0)
        {
            throw new InvalidDataException("Choose a positive whole number of shares.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown shareholder.");
        long sourceId = actorId;
        if (request.FromCorporation)
        {
            var current = CorporationRoleStore.Current(world, actorId);
            CorporationRoleStore.RequireDirector(world, actorId, current);
            sourceId = current.Id;
        }
        if (
            sourceId == request.RecipientId
            || (
                world.Pilots.Find(request.RecipientId) is null
                && (request.RecipientId is <= 0 or > int.MaxValue || world.Set<SavedCorporation>().Find((int)request.RecipientId) is null)
            )
        )
        {
            throw new InvalidDataException("Choose a different saved pilot or player corporation as the recipient.");
        }
        var source = world.Set<SavedCorporationShareHolding>().Find(request.CorporationId, sourceId);
        if (source is null || source.Shares < request.Shares)
        {
            throw new InvalidDataException("The source does not own that many shares in this corporation.");
        }
        var destination =
            world.Set<SavedCorporationShareHolding>().Find(request.CorporationId, request.RecipientId)
            ?? world
                .Add(new SavedCorporationShareHolding { CorporationId = request.CorporationId, ShareholderId = request.RecipientId })
                .Entity;
        long sourceBefore = source.Shares;
        long destinationBefore = destination.Shares;
        var balances = CorporationShareRules.Transfer(sourceBefore, destinationBefore, request.Shares);
        source.Shares = balances.Source;
        destination.Shares = balances.Destination;
        world.Add(
            new SavedCorporationShareTransfer
            {
                CorporationId = request.CorporationId,
                ActorId = actorId,
                FromOwnerId = sourceId,
                ToOwnerId = request.RecipientId,
                Shares = request.Shares,
                Time = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            }
        );
        Notify(world, new(request.CorporationId, sourceId, sourceBefore, source.Shares));
        Notify(world, new(request.CorporationId, request.RecipientId, destinationBefore, destination.Shares));
        world.SaveChanges();
        transaction.Commit();
    }

    public bool CanObserve(long actorId, long shareholderId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var actor = world.Pilots.Find(actorId);
        return actor is not null
            && (
                actor.Id == shareholderId
                || actor.CorporationId == shareholderId
                    && world.Set<SavedCorporation>().Find(actor.CorporationId) is { } corporation
                    && CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.Accountant | CorporationRole.JuniorAccountant)
            );
    }

    internal static void Issue(WorldDbContext world, int corporationId)
    {
        world.Add(new SavedCorporationShareIssue { CorporationId = corporationId, Shares = CorporationRules.InitialShares });
        world.Add(
            new SavedCorporationShareHolding
            {
                CorporationId = corporationId,
                ShareholderId = corporationId,
                Shares = CorporationRules.InitialShares,
            }
        );
    }

    private static CorporationShareHolding Snapshot(WorldDbContext world, SavedCorporationShareHolding holding)
    {
        int? ownerCorporation = world.Pilots.Find(holding.ShareholderId)?.CorporationId;
        return new(holding.CorporationId, holding.ShareholderId, holding.Shares, ownerCorporation);
    }

    private static void RequireFinancial(WorldDbContext world, long actorId, SavedCorporation corporation)
    {
        if (!CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.Accountant | CorporationRole.JuniorAccountant))
        {
            throw new InvalidDataException("Corporation financial authority is required to read company shares.");
        }
    }

    private static void Notify(WorldDbContext world, CorporationShareChange change)
    {
        long[] recipients;
        if (world.Pilots.Find(change.ShareholderId) is not null)
        {
            recipients = [change.ShareholderId];
        }
        else
        {
            var corporation = world.Set<SavedCorporation>().Find((int)change.ShareholderId)!;
            recipients = world
                .Pilots.Where(p => p.CorporationId == corporation.Id)
                .Select(p => p.Id)
                .AsEnumerable()
                .Where(id =>
                    CorporationRoleStore.Has(world, id, corporation, CorporationRole.Accountant | CorporationRole.JuniorAccountant)
                )
                .ToArray();
        }
        string json = JsonSerializer.Serialize(change);
        foreach (long recipient in recipients)
        {
            world.Add(new SavedCorporationChange { PilotId = recipient, ShareJson = json });
        }
    }
}
