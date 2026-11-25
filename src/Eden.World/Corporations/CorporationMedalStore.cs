using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationMedalStore(WorldDatabase database, TimeProvider clock) : ICorporationMedals
{
    private ResidentEconomy Economy =>
        LiveWorld.Find(database.Path)?.Economy ?? throw new InvalidDataException("Corporation medals require a running world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public long? Create(long actorId, CorporationMedalDesign design, bool confirmed)
    {
        CorporationMedalRules.Validate(design);
        using var world = Economy.Begin(TransactionParticipants.None);
        var corporation = RequireManager(world, actorId);
        int division = WalletDivision(world, corporation, actorId);
        if (!confirmed)
        {
            return null;
        }
        long id = world.Next(CorporationMedalSchema.MedalTable);
        long now = Now;
        Charge(world, corporation, division, actorId, id, now, CorporationMedalRules.FeeCents, WalletEntryType.MedalCreation);
        var medal = new CorporationMedal(id, corporation.Id, actorId, now, design);
        world.Add(
            new SavedCorporationMedal
            {
                Id = id,
                CorporationId = corporation.Id,
                DataJson = JsonSerializer.Serialize(medal),
            }
        );
        world.Commit();
        return id;
    }

    public bool Award(long actorId, long medalId, IReadOnlyList<long> recipients, string reason, bool confirmed)
    {
        CorporationMedalRules.ValidateAward(recipients, reason);
        using var world = Economy.Begin(TransactionParticipants.None);
        var corporation = RequireManager(world, actorId);
        int division = WalletDivision(world, corporation, actorId);
        if (
            world.Set<SavedCorporationMedal>().Find(medalId)?.CorporationId != corporation.Id
            || recipients.Any(id => world.Pilots.Find(id)?.CorporationId != corporation.Id)
        )
        {
            throw new InvalidDataException("Choose a medal and recipients from your corporation.");
        }
        if (!confirmed)
        {
            return false;
        }
        long now = Now;
        long cost = CorporationMedalRules.FeeCents * recipients.Count;
        Charge(world, corporation, division, actorId, medalId, now, cost, WalletEntryType.MedalIssuing);
        foreach (long recipientId in recipients)
        {
            var award = new CorporationMedalAward(medalId, recipientId, actorId, now, reason, CorporationMedalStatus.Private);
            world.Add(
                new SavedCorporationMedalAward
                {
                    Id = world.Next(CorporationMedalSchema.AwardTable),
                    MedalId = medalId,
                    RecipientId = recipientId,
                    DataJson = JsonSerializer.Serialize(award),
                }
            );
        }
        world.Commit();
        return true;
    }

    public IReadOnlyList<CorporationMedalDetails> Catalogue(long actorId, int corporationId) =>
        Economy.Inspect(world =>
        {
            if (world.Pilots.Find(actorId)?.CorporationId != corporationId)
            {
                throw new InvalidDataException("Choose your corporation's medal catalogue.");
            }
            return world
                .Filter<SavedCorporationMedal>(row => row.CorporationId == corporationId)
                .OrderBy(row => row.Id)
                .Select(row => RecordDetails(world, row))
                .ToArray();
        });

    public CorporationMedalDetails? Details(long medalId) =>
        Economy.Inspect(world => world.Set<SavedCorporationMedal>().Find(medalId) is { } row ? RecordDetails(world, row) : null);

    public IReadOnlyList<CorporationMedalAward> Recipients(long viewerId, long medalId) =>
        Economy.Inspect(world =>
        {
            if (world.Set<SavedCorporationMedal>().Find(medalId) is not { } medal)
            {
                return Array.Empty<CorporationMedalAward>();
            }
            bool sameCorporation = world.Pilots.Find(viewerId)?.CorporationId == medal.CorporationId;
            return world
                .Filter<SavedCorporationMedalAward>(row => row.MedalId == medalId)
                .OrderBy(row => row.Id)
                .Select(row => row.Snapshot())
                .Where(award => sameCorporation || award.RecipientId == viewerId || award.Status == CorporationMedalStatus.Public)
                .ToArray();
        });

    public IReadOnlyList<ReceivedCorporationMedal> Received(long viewerId, long recipientId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
                world
                    .Filter<SavedCorporationMedalAward>(row => row.RecipientId == recipientId)
                    .OrderByDescending(row => row.Id)
                    .Select(row => row.Snapshot())
                    .Where(award =>
                        award.Status == CorporationMedalStatus.Public
                        || viewerId == recipientId && award.Status == CorporationMedalStatus.Private
                    )
                    .Select(award => new ReceivedCorporationMedal(
                        world.Set<SavedCorporationMedal>().Find(award.MedalId)!.Snapshot(),
                        award
                    ))
                    .ToArray()
            )
        ?? [];

    public void SetStatus(long actorId, IReadOnlyDictionary<long, CorporationMedalStatus> changes)
    {
        if (changes.Count > CorporationMedalRules.MaximumStatusChanges || changes.Any(pair => pair.Key <= 0 || !Enum.IsDefined(pair.Value)))
        {
            throw new InvalidDataException("Choose valid decoration visibility values.");
        }
        using var world = Economy.Begin(TransactionParticipants.None);
        var received = world
            .Filter<SavedCorporationMedalAward>(row => row.RecipientId == actorId)
            .AsEnumerable()
            .Where(row => row.Snapshot().Status != CorporationMedalStatus.Deleted)
            .ToLookup(row => row.MedalId);
        if (changes.Keys.Any(id => !received.Contains(id)))
        {
            throw new InvalidDataException("You can only change the visibility of your own decorations.");
        }
        foreach (var (medalId, status) in changes)
        {
            foreach (var row in received[medalId])
            {
                var award = row.Snapshot();
                if (award.Status != status)
                {
                    row.DataJson = JsonSerializer.Serialize(award with { Status = status });
                }
            }
        }
        world.Commit();
    }

    private static CorporationMedalDetails RecordDetails(WorldAction world, SavedCorporationMedal row) =>
        new(row.Snapshot(), world.Filter<SavedCorporationMedalAward>(award => award.MedalId == row.Id).Count());

    private static SavedCorporation RequireManager(WorldAction world, long actorId)
    {
        if (
            world.Pilots.Find(actorId) is not { } pilot
            || world.Set<SavedCorporation>().Find(pilot.CorporationId) is not { ClosedAt: null } corporation
            || !CorporationRoleStore.Effective(world, actorId, corporation).Roles.Has(CorporationRole.PersonnelManager)
        )
        {
            throw new InvalidDataException("You need the Personnel Manager or Director role to manage corporation medals.");
        }
        return corporation;
    }

    private static int WalletDivision(WorldAction world, SavedCorporation corporation, long actorId)
    {
        int key = CorporationWalletStore.SelectedKey(world, actorId, corporation);
        if (!CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, corporation).Roles, key))
        {
            throw new InvalidDataException("Choose a corporation wallet division you can withdraw from.");
        }
        return key;
    }

    private static void Charge(
        WorldAction world,
        SavedCorporation corporation,
        int division,
        long actorId,
        long medalId,
        long now,
        long cost,
        WalletEntryType type
    )
    {
        long balance = CorporationWalletStore.ReadBalance(world, corporation, division);
        if (balance < cost)
        {
            throw new InsufficientMedalFunds(cost);
        }
        long after = balance - cost;
        CorporationWalletStore.WriteBalance(world, corporation, division, after);
        CorporationWalletStore.AddEntry(world, corporation.Id, division, now, actorId, -cost, after, type, medalId, "");
    }
}
