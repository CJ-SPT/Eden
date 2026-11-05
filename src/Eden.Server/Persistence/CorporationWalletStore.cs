using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedCorporationWallet
{
    public int CorporationId { get; set; }
    public int Key { get; set; }
    public long BalanceCents { get; set; }
}

public sealed class SavedWalletLedger
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public int Key { get; set; }
    public long Time { get; set; }
    public long CounterpartyId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public WalletEntryType EntryType { get; set; }
    public long ReferenceId { get; set; }
    public string Reason { get; set; } = "";

    public CorporationWalletEntry Snapshot() =>
        new(Id, OwnerId, Key, Time, CounterpartyId, AmountCents, BalanceCents, EntryType, ReferenceId, Reason);
}

[Injectable]
internal sealed class CorporationWalletStore(WorldDatabase database, TimeProvider clock) : ICorporationWallet
{
    public IReadOnlyList<CorporationWalletDivision> Divisions(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var corporation = CorporationRoleStore.Current(world, actorId);
        var roles = CorporationRoleStore.Effective(world, actorId, corporation).Roles;
        return Enumerable
            .Range(CorporationWalletRules.MasterKey, CorporationWalletRules.DivisionCount)
            .Where(key => CorporationWalletRules.CanRead(roles, key))
            .Select(key => new CorporationWalletDivision(key, ReadBalance(world, corporation, key)))
            .ToArray();
    }

    public long Balance(long actorId, int key)
    {
        CorporationWalletRules.CheckKey(key);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var corporation = AuthorizeRead(world, actorId, key);
        return ReadBalance(world, corporation, key);
    }

    public void SelectDivision(long actorId, int key)
    {
        CorporationWalletRules.CheckKey(key);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (!CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, corporation).Roles, key))
        {
            throw new InvalidDataException("Wallet withdrawal access is required to select this division.");
        }
        var selection =
            world.Set<SavedCorporationWalletSelection>().Find(actorId)
            ?? world.Add(new SavedCorporationWalletSelection { PilotId = actorId }).Entity;
        selection.CorporationId = corporation.Id;
        selection.Key = key;
        world.SaveChanges();
        transaction.Commit();
    }

    public IReadOnlyList<CorporationWalletEntry> Journal(long actorId, bool corporation, int key)
    {
        CorporationWalletRules.CheckKey(key);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        long owner = corporation ? AuthorizeRead(world, actorId, key).Id : actorId;
        if (!corporation && key != CorporationWalletRules.MasterKey)
        {
            throw new InvalidDataException("Personal wallets have one cash account.");
        }
        return world
            .Set<SavedWalletLedger>()
            .Where(e => e.OwnerId == owner && e.Key == key)
            .OrderByDescending(e => e.Id)
            .Take(1000)
            .AsEnumerable()
            .Select(e => e.Snapshot())
            .ToArray();
    }

    public void Transfer(long actorId, WalletTransferRequest request)
    {
        CorporationWalletRules.CheckTransfer(request);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
        var sourceCorporation = request.FromCorporation ? CorporationRoleStore.Current(world, actorId) : null;
        if (
            sourceCorporation is not null
            && !CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, sourceCorporation).Roles, request.FromKey)
        )
        {
            throw new InvalidDataException("You cannot withdraw from this corporation wallet division.");
        }
        if (sourceCorporation is null && request.FromKey != CorporationWalletRules.MasterKey)
        {
            throw new InvalidDataException("Personal wallets have one cash account.");
        }
        var targetPilot = world.Pilots.Find(request.RecipientId);
        var targetCorporation = request.RecipientId <= int.MaxValue ? world.Set<SavedCorporation>().Find((int)request.RecipientId) : null;
        if (targetPilot is null && targetCorporation is null)
        {
            throw new InvalidDataException("Choose an Eden pilot or player corporation as the recipient.");
        }
        if (targetPilot is not null && request.ToKey != CorporationWalletRules.MasterKey)
        {
            throw new InvalidDataException("Personal wallets have one cash account.");
        }
        long sourceOwner = sourceCorporation?.Id ?? actorId;
        if (sourceOwner == request.RecipientId && request.FromKey == request.ToKey)
        {
            throw new InvalidDataException("Choose a different recipient or division.");
        }
        var sourcePilotWallet = sourceCorporation is null ? PilotWallet(world, actorId) : null;
        long before = sourceCorporation is null ? sourcePilotWallet!.Cents : ReadBalance(world, sourceCorporation, request.FromKey);
        long destinationBefore = targetCorporation is null
            ? PilotWallet(world, request.RecipientId).Cents
            : ReadBalance(world, targetCorporation, request.ToKey);
        if (before < request.AmountCents)
        {
            throw new InvalidDataException("There is not enough ISK in the source wallet.");
        }
        if (destinationBefore > long.MaxValue - request.AmountCents)
        {
            throw new InvalidDataException("The destination wallet cannot hold this amount.");
        }
        long after = before - request.AmountCents;
        long destinationAfter = destinationBefore + request.AmountCents;
        if (sourceCorporation is null)
        {
            sourcePilotWallet!.Cents = after;
        }
        else
        {
            WriteBalance(world, sourceCorporation, request.FromKey, after);
        }
        if (targetCorporation is null)
        {
            PilotWallet(world, request.RecipientId).Cents = destinationAfter;
        }
        else
        {
            WriteBalance(world, targetCorporation, request.ToKey, destinationAfter);
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var type = request.FromCorporation ? CorporationWalletRules.CorporationWithdrawalEntry : CorporationWalletRules.PlayerDonationEntry;
        AddEntry(
            world,
            sourceOwner,
            request.FromKey,
            now,
            request.RecipientId,
            -request.AmountCents,
            after,
            type,
            actor.Id,
            request.Reason
        );
        AddEntry(
            world,
            request.RecipientId,
            request.ToKey,
            now,
            sourceOwner,
            request.AmountCents,
            destinationAfter,
            type,
            actor.Id,
            request.Reason
        );
        world.SaveChanges();
        transaction.Commit();
    }

    internal static int SelectedKey(WorldDbContext world, long actorId, SavedCorporation? corporation)
    {
        var saved = world.Set<SavedCorporationWalletSelection>().Find(actorId);
        return
            saved is not null
            && saved.CorporationId == corporation?.Id
            && CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, corporation).Roles, saved.Key)
            ? saved.Key
            : CorporationWalletRules.MasterKey;
    }

    internal static long TaxBounty(WorldDbContext world, Pilot pilot, long grossCents, long now)
    {
        var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        if (corporation is null)
        {
            return 0;
        }
        long tax = CorporationWalletRules.Tax(grossCents, corporation.Snapshot(0).Details.TaxRate);
        if (tax == 0)
        {
            return 0;
        }
        corporation.BalanceCents = checked(corporation.BalanceCents + tax);
        AddEntry(
            world,
            corporation.Id,
            CorporationWalletRules.MasterKey,
            now,
            pilot.Id,
            tax,
            corporation.BalanceCents,
            CorporationWalletRules.BountyTaxEntry,
            pilot.Id,
            ""
        );
        return tax;
    }

    private static SavedCorporation AuthorizeRead(WorldDbContext world, long actorId, int key)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (!CorporationWalletRules.CanRead(CorporationRoleStore.Effective(world, actorId, corporation).Roles, key))
        {
            throw new InvalidDataException("You cannot view this corporation wallet division.");
        }
        return corporation;
    }

    private static PilotWallet PilotWallet(WorldDbContext world, long pilotId) =>
        world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;

    private static long ReadBalance(WorldDbContext world, SavedCorporation corporation, int key) =>
        key == CorporationWalletRules.MasterKey
            ? corporation.BalanceCents
            : world.Set<SavedCorporationWallet>().Find(corporation.Id, key)?.BalanceCents ?? 0;

    private static void WriteBalance(WorldDbContext world, SavedCorporation corporation, int key, long balance)
    {
        if (key == CorporationWalletRules.MasterKey)
        {
            corporation.BalanceCents = balance;
        }
        else
        {
            var wallet =
                world.Set<SavedCorporationWallet>().Find(corporation.Id, key)
                ?? world.Add(new SavedCorporationWallet { CorporationId = corporation.Id, Key = key }).Entity;
            wallet.BalanceCents = balance;
        }
    }

    internal static void AddEntry(
        WorldDbContext world,
        long owner,
        int key,
        long now,
        long counterparty,
        long amount,
        long balance,
        WalletEntryType type,
        long reference,
        string reason
    ) =>
        world.Add(
            new SavedWalletLedger
            {
                OwnerId = owner,
                Key = key,
                Time = now,
                CounterpartyId = counterparty,
                AmountCents = amount,
                BalanceCents = balance,
                EntryType = type,
                ReferenceId = reference,
                Reason = reason,
            }
        );
}

public sealed class SavedCorporationWalletSelection
{
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public int Key { get; set; } = CorporationWalletRules.MasterKey;
}
