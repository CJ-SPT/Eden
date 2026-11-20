using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationRecruitmentStore(WorldDatabase database, TimeProvider clock) : ICorporationRecruitment
{
    private LiveWorld Resident => LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The world is not running.");
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public long Create(long actorId, CorporationRecruitmentDraft draft, int days)
    {
        CorporationRecruitmentRules.Validate(draft);
        long fee = CorporationRecruitmentRules.Fee(days, true);
        using var transaction = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = Authorize(transaction, actorId);
        CheckRecruiters(transaction, corporation.Id, draft.Recruiters);
        long id = CreateAdvertisement(transaction, corporation, draft, days, fee, Now);
        transaction.Commit();
        return id;
    }

    internal static long CreateAdvertisement(
        EconomyTransaction transaction,
        SavedCorporation corporation,
        CorporationRecruitmentDraft draft,
        int days,
        long fee,
        long now
    )
    {
        var existing = transaction.Filter<SavedCorporationRecruitmentAd>(ad => ad.CorporationId == corporation.Id).ToArray();
        if (existing.Count(ad => ad.ExpiresAt > now) >= CorporationRecruitmentRules.MaximumAds)
        {
            throw new InvalidDataException("A corporation can have at most three active recruitment ads.");
        }
        // Expired ads remain editable until a new advertisement needs their slot.
        foreach (var expired in existing.Where(ad => ad.ExpiresAt <= now))
        {
            transaction.Remove(expired);
        }
        var row = transaction
            .Add(
                new SavedCorporationRecruitmentAd
                {
                    Id = transaction.Next("WorldCorporationRecruitmentAd"),
                    CorporationId = corporation.Id,
                    CreatedAt = now,
                    ExpiresAt = checked(now + days * TimeSpan.TicksPerDay),
                    DetailsJson = JsonSerializer.Serialize(draft),
                }
            )
            .Entity;
        Pay(transaction, corporation, row.Id, fee, now);
        Notify(transaction, corporation);
        return row.Id;
    }

    public void Update(long actorId, long id, CorporationRecruitmentDraft draft, int addedDays)
    {
        CorporationRecruitmentRules.Validate(draft);
        long fee = CorporationRecruitmentRules.Fee(addedDays, false);
        using var transaction = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = Authorize(transaction, actorId);
        var ad = Owned(transaction, corporation.Id, id);
        CheckRecruiters(transaction, corporation.Id, draft.Recruiters);
        long now = Now;
        if (ad.ExpiresAt <= now && addedDays == 0)
        {
            throw new InvalidDataException("Extend an expired advertisement before saving it.");
        }
        long expires = checked(Math.Max(ad.ExpiresAt, now) + addedDays * TimeSpan.TicksPerDay);
        if (expires > now + CorporationRecruitmentRules.MaximumDays * TimeSpan.TicksPerDay)
        {
            throw new InvalidDataException("Recruitment ads can run for at most 28 days from today.");
        }
        ad.ExpiresAt = expires;
        ad.DetailsJson = JsonSerializer.Serialize(draft);
        Pay(transaction, corporation, id, fee, now);
        Notify(transaction, corporation);
        transaction.Commit();
    }

    public void Delete(long actorId, long id)
    {
        using var transaction = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = Authorize(transaction, actorId);
        transaction.Remove(Owned(transaction, corporation.Id, id));
        Notify(transaction, corporation);
        transaction.Commit();
    }

    public IReadOnlyList<CorporationRecruitmentAd> ForCorporation(int corporationId, bool includeExpired = false) =>
        Resident.Economy.Inspect(world =>
        {
            int count = world.Pilots.Count(p => p.CorporationId == corporationId);
            long now = Now;
            return world
                .Set<SavedCorporationRecruitmentAd>()
                .Where(ad => ad.CorporationId == corporationId && (includeExpired || ad.ExpiresAt > now))
                .OrderBy(ad => ad.Id)
                .AsEnumerable()
                .Select(ad => ad.Snapshot(count) with { AllianceId = world.Set<SavedAllianceMember>().Find(corporationId)?.AllianceId })
                .ToArray();
        });

    public IReadOnlyList<CorporationRecruitmentMatch> Search(CorporationRecruitmentSearch query)
    {
        CorporationRecruitmentRules.Validate(query);
        return Resident.Economy.Inspect(world =>
        {
            long now = Now;
            var counts = world.Pilots.GroupBy(p => p.CorporationId).ToDictionary(group => group.Key, group => group.Count());
            var corporations = world.Set<SavedCorporation>().ToDictionary(c => c.Id, c => c.Snapshot(0).Details);
            List<CorporationRecruitmentMatch> matches = [];
            foreach (var row in world.Set<SavedCorporationRecruitmentAd>().Where(ad => ad.ExpiresAt > now))
            {
                var ad = row.Snapshot(counts.GetValueOrDefault(row.CorporationId)) with
                {
                    AllianceId = world.Set<SavedAllianceMember>().Find(row.CorporationId)?.AllianceId,
                };
                if (query.ExcludeAlliances && ad.AllianceId.HasValue)
                {
                    continue;
                }
                if (
                    corporations.TryGetValue(row.CorporationId, out var details)
                    && CorporationRecruitmentRules.Match(query, ad, details) is int grade
                )
                {
                    matches.Add(new(grade, ad));
                }
            }
            return matches.OrderByDescending(match => match.Grade).ThenBy(match => match.Ad.Id).ToArray();
        });
    }

    public IReadOnlyList<long> Recruiters(long id) =>
        Resident.Economy.Inspect(world =>
        {
            var ad =
                world.Set<SavedCorporationRecruitmentAd>().Find(id) ?? throw new InvalidDataException("Unknown recruitment advertisement.");
            return ad.Snapshot(0).Details.Recruiters.Where(pilot => world.Pilots.Find(pilot)?.CorporationId == ad.CorporationId).ToArray();
        });

    private static SavedCorporation Authorize(WorldAction world, long actorId)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (!CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.PersonnelManager))
        {
            throw new InvalidDataException("Personnel management is required to edit recruitment advertisements.");
        }
        return corporation;
    }

    private static SavedCorporationRecruitmentAd Owned(WorldAction world, int corporation, long id)
    {
        var ad = world.Set<SavedCorporationRecruitmentAd>().Find(id);
        return ad?.CorporationId == corporation ? ad : throw new InvalidDataException("Choose one of your corporation's ads.");
    }

    private static void CheckRecruiters(WorldAction world, int corporation, IReadOnlyList<long> recruiters)
    {
        if (recruiters.Any(id => world.Pilots.Find(id)?.CorporationId != corporation))
        {
            throw new InvalidDataException("Recruiters must be current corporation members.");
        }
    }

    private static void Pay(WorldAction world, SavedCorporation corporation, long ad, long fee, long now)
    {
        if (corporation.BalanceCents < fee)
        {
            throw new InvalidDataException("The corporation master wallet does not have enough ISK for this advertisement.");
        }
        if (fee == 0)
        {
            return;
        }
        corporation.BalanceCents -= fee;
        CorporationWalletStore.AddEntry(
            world,
            corporation.Id,
            CorporationWalletRules.MasterKey,
            now,
            corporation.Id,
            -fee,
            corporation.BalanceCents,
            WalletEntryType.CorporationAdvertisementFee,
            ad,
            "Recruitment advertisement"
        );
    }

    private static void Notify(WorldAction world, SavedCorporation corporation)
    {
        var before = corporation.Snapshot(0).Details;
        var after = before with { RecruitmentRevision = checked(before.RecruitmentRevision + 1) };
        corporation.DetailsJson = JsonSerializer.Serialize(after);
        string change = JsonSerializer.Serialize(new CorporationAdministrationChange(corporation.Id, before, after));
        foreach (long pilot in world.Pilots.Where(p => p.CorporationId == corporation.Id).Select(p => p.Id))
        {
            world.Add(new SavedCorporationChange { PilotId = pilot, AdministrationJson = change });
        }
    }
}
