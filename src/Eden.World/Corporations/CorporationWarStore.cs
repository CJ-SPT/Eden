using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mail;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;

namespace Eden.World.Corporations;

[Injectable]
internal sealed partial class CorporationWarStore(WorldDatabase database, TimeProvider clock, InboxChangeSignal inbox)
    : ICorporationWars,
        ICorporationWarMaintenance
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation wars require a running world.");

    public int Organization(long pilotId) =>
        Resident.Economy.Inspect(world => Organization(world, world.Pilots.Find(pilotId)?.CorporationId ?? 0));

    public bool IsEligible(int ownerId)
    {
        long now = Now;
        return LiveWorld.Find(database.Path)?.Economy.Inspect(world => EligibilityLoss(world, ownerId, now) is null) ?? false;
    }

    public bool CorporationAllowsWar(int corporationId)
    {
        long now = Now;
        return LiveWorld
                .Find(database.Path)
                ?.Economy.Inspect(world =>
                    world.Set<SavedCorporation>().Find(corporationId) is { ClosedAt: null }
                    && Structures(world, Organization(world, corporationId)).Any(structure => Departure(structure, now) is null)
                )
            ?? false;
    }

    public IReadOnlyList<CorporationWar> Wars(int ownerId)
    {
        long now = Now;
        return LiveWorld
                .Find(database.Path)
                ?.Economy.Inspect(world =>
                    WarsInvolving(world, ownerId, now).OrderByDescending(war => war.Id).Select(war => Snapshot(world, war, now)).ToArray()
                )
            ?? [];
    }

    public IReadOnlyList<CorporationWar> PublicWars(long maxWarId)
    {
        long now = Now;
        return Resident.Economy.Inspect(world =>
            world
                .Filter<SavedCorporationWar>(war => maxWarId == 0 || war.Id < maxWarId)
                .OrderByDescending(war => war.Id)
                .Take(50)
                .AsEnumerable()
                .Select(war => Snapshot(world, war, now))
                .ToArray()
        );
    }

    public CorporationWar? War(long warId)
    {
        long now = Now;
        return Resident.Economy.Inspect(world =>
            world.Set<SavedCorporationWar>().Find(warId) is { } war ? Snapshot(world, war, now) : null
        );
    }

    public IReadOnlyList<CorporationWar> WarsForStructure(long structureId)
    {
        long now = Now;
        return LiveWorld
                .Find(database.Path)
                ?.Economy.Inspect(world => HeadquartersHistory(world, structureId).Select(war => Snapshot(world, war, now)).ToArray())
            ?? [];
    }

    public IReadOnlyDictionary<long, IReadOnlyList<long>> HeadquartersWars(IReadOnlyList<long> structureIds)
    {
        long now = Now;
        return LiveWorld
                .Find(database.Path)
                ?.Economy.Inspect(world =>
                    structureIds
                        .Distinct()
                        .ToDictionary(
                            id => id,
                            id => (IReadOnlyList<long>)CurrentHeadquartersWars(world, id, now).Select(war => war.Id).ToArray()
                        )
                )
            ?? new Dictionary<long, IReadOnlyList<long>>();
    }

    private static IEnumerable<SavedCorporationWar> CurrentHeadquartersWars(WorldAction world, long id, long now) =>
        HeadquartersHistory(world, id).Where(war => Finish(war) is null || Finish(war) > now);

    private static IEnumerable<SavedCorporationWar> HeadquartersHistory(WorldAction world, long id) =>
        world.Filter<SavedCorporationWar>(war => war.HeadquartersId == id).AsEnumerable().OrderByDescending(war => war.Id);

    public IReadOnlyList<CorporationWarPeaceTreaty> PeaceTreaties(long pilotId)
    {
        long now = Now;
        return Resident.Economy.Inspect(world =>
        {
            int owner = Organization(world, world.Pilots.Find(pilotId)?.CorporationId ?? 0);
            var headquartersTreaties = ForOrganization(world, owner)
                .Where(war =>
                    war.ReasonEnded == CorporationWarEndReason.HeadquartersLost
                    && war.EndingAt + CorporationWarRules.HeadquartersLossTruce > now
                )
                .OrderByDescending(war => war.EndingAt)
                .Select(war => new CorporationWarPeaceTreaty(
                    war.AttackerId == owner ? war.DefenderId : war.AttackerId,
                    war.EndingAt!.Value + CorporationWarRules.HeadquartersLossTruce,
                    war.Id,
                    war.HeadquartersId!.Value,
                    war.AttackerId == owner
                ));
            var surrenderTreaties = SurrenderTreaties(world, owner, now)
                .SelectMany(offer =>
                    new[] { true, false }.Select(outgoing => new CorporationWarPeaceTreaty(
                        offer.FromOwnerId == owner ? offer.ToOwnerId : offer.FromOwnerId,
                        offer.AcceptedAt!.Value + CorporationWarRules.SurrenderTruce,
                        offer.WarId,
                        world.Set<SavedCorporationWar>().Find(offer.WarId)?.HeadquartersId,
                        outgoing,
                        CorporationWarPeaceReason.Surrender,
                        CorporationWarEndReason.Surrender
                    ))
                );
            return headquartersTreaties.Concat(surrenderTreaties).OrderByDescending(treaty => treaty.ExpiresAt).ToArray();
        });
    }

    public IReadOnlyList<CorporationWarHeadquarters> Headquarters(long pilotId, int ownerId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        return Resident.Economy.Inspect(world =>
        {
            var (owner, _) = Authorization(world, pilotId);
            if (owner != ownerId)
            {
                throw new InvalidDataException("Choose your own corporation or alliance.");
            }
            return Structures(world, owner)
                .Where(s => ValidHeadquarters(catalog, s, now))
                .OrderBy(s => s.Id)
                .Select(s => new CorporationWarHeadquarters(
                    s.TypeId,
                    s.Id,
                    StructureRules.Upkeep(s.LowPowerAt, s.LowPowerAt is null, now, s.TypeId, s.AbandonAt),
                    world
                        .Filter<SavedCorporationWar>(war => war.HeadquartersId == s.Id)
                        .AsEnumerable()
                        .Where(war => Finish(war) is null || Finish(war) > now)
                        .Select(war => war.Id)
                        .Order()
                        .ToArray(),
                    s.CorporationId,
                    s.SystemId,
                    s.Name
                ))
                .ToArray();
        });
    }

    public CorporationWar Declare(long pilotId, int targetOrganizationId, long headquartersId, bool allianceRegistry)
    {
        long now = Now;
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (owner, payer) = Authorization(world, pilotId);
        if ((owner != payer.Id) != allianceRegistry)
        {
            throw new InvalidDataException("Declare wars on behalf of your corporation or your alliance executor.");
        }
        CheckOpponent(world, owner, targetOrganizationId, now);
        if (EligibilityLoss(world, owner, now) is not null || EligibilityLoss(world, targetOrganizationId, now) is not null)
        {
            throw new InvalidDataException("Both organizations must own a structure before declaring a war.");
        }
        if (
            world.Set<SavedStructure>().Find(headquartersId) is not { } headquarters
            || Organization(world, headquarters.CorporationId) != owner
            || !ValidHeadquarters(catalog, headquarters, now)
        )
        {
            throw new InvalidDataException("Choose an anchored high-security Upwell headquarters owned by your organization.");
        }
        var previous = ForPair(world, owner, targetOrganizationId).Where(war => war.AttackerId == owner).ToArray();
        if (
            previous.Any(war =>
                war.ReasonEnded == CorporationWarEndReason.HeadquartersLost
                && war.EndingAt + CorporationWarRules.HeadquartersLossTruce > now
            )
        )
        {
            throw new InvalidDataException("The 14-day peace period after losing your War HQ is still active.");
        }

        long id = world.Next(CorporationWarSchema.WarTable);
        long billId = world.Next("WorldAllianceBill");
        if (!Charge(world, payer, CorporationWalletRules.MasterKey, targetOrganizationId, now))
        {
            throw new InvalidDataException("The main corporation wallet cannot cover the war declaration.");
        }
        var declared = NewWar(id, owner, targetOrganizationId, now);
        declared.HeadquartersId = headquartersId;
        declared.BillId = billId;
        declared.BillIssuedAt = now;
        declared.BillDueAt = checked(now + CorporationWarRules.BillingPeriod);
        world.Add(declared);
        NotifyDeclared(world, declared);
        var result = Snapshot(world, declared, now);
        world.Commit();
        return result;
    }

    public void RetractMutual(long pilotId, long warId)
    {
        long now = Now;
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (owner, _) = Authorization(world, pilotId);
        if (
            world.Set<SavedCorporationWar>().Find(warId) is not { Mutual: true, EndingAt: null } war
            || owner != war.AttackerId && owner != war.DefenderId
        )
        {
            throw new InvalidDataException("Choose a current mutual war involving your organization.");
        }
        war.RetractedAt = now;
        war.RetractedBy = owner;
        End(world, war, CorporationWarEndReason.MutualEnded, now, now);
        world.Commit();
    }

    public void Advance(long now)
    {
        var resident = LiveWorld.Find(database.Path);
        if (resident is null)
        {
            return;
        }
        long expiredInvitation = now - CorporationWarRules.InvitationLifetime;
        bool pending = resident.Economy.Inspect(world =>
            world
                .Filter<SavedCorporationWar>(war => war.EndsAt == null && !war.Mutual)
                .AsEnumerable()
                .Any(war => war.BillDueAt <= now || StructureLoss(world, war, now).HasValue)
            || world.Filter<SavedCorporationWarInvitation>(row => row.SentAt <= expiredInvitation).Any()
        );
        if (!pending)
        {
            return;
        }
        using var transaction = resident.Economy.Begin(TransactionParticipants.None);
        foreach (var war in transaction.Filter<SavedCorporationWar>(war => war.EndsAt == null && !war.Mutual).ToArray())
        {
            Settle(transaction, war, now);
        }
        transaction.RemoveRange(transaction.Filter<SavedCorporationWarInvitation>(row => row.SentAt <= expiredInvitation).ToArray());
        transaction.Commit();
    }

    private void Settle(WorldAction world, SavedCorporationWar war, long now)
    {
        if (war is { Mutual: true } or { EndingAt: not null })
        {
            return;
        }

        var loss = StructureLoss(world, war, now);
        while (war.BillDueAt is long due && due <= now && (loss is null || due < loss.Value.At))
        {
            if (
                Payer(world, war.AttackerId) is not { } payer
                || CorporationBillingStore.Saved(world, payer.Id) is not { } settings
                || !settings.Rules.Any(rule => rule.BillTypeId == CorporationWarRules.WarBillType && rule.Enabled)
                || !Charge(world, payer, settings.DivisionKey, war.DefenderId, due)
            )
            {
                End(world, war, CorporationWarEndReason.BillUnpaid, due, due);
                return;
            }
            Renew(world, war, due);
        }
        if (loss is { } ended)
        {
            End(world, war, ended.Reason, ended.At, Math.Max(war.StartsAt, ended.At));
        }
    }

    private static (long At, CorporationWarEndReason Reason)? StructureLoss(WorldAction world, SavedCorporationWar war, long now)
    {
        var headquarters = world.Set<SavedStructure>().Find(war.HeadquartersId!.Value);
        long? headquartersLoss =
            headquarters is { } structure && Organization(world, structure.CorporationId) == war.AttackerId
                ? Departure(structure, now)
                : now;
        long? attackerLoss = EligibilityLoss(world, war.AttackerId, now, war.DeclaredAt);
        long? defenderLoss = EligibilityLoss(world, war.DefenderId, now, war.DeclaredAt);
        long earliest = Math.Min(headquartersLoss ?? long.MaxValue, Math.Min(attackerLoss ?? long.MaxValue, defenderLoss ?? long.MaxValue));
        if (earliest == long.MaxValue)
        {
            return null;
        }
        var reason =
            headquartersLoss == earliest ? CorporationWarEndReason.HeadquartersLost
            : attackerLoss == earliest ? CorporationWarEndReason.AttackerIneligible
            : CorporationWarEndReason.DefenderIneligible;
        return (Math.Max(war.DeclaredAt, earliest), reason);
    }

    private void NotifyDeclared(WorldAction world, SavedCorporationWar war) =>
        CorporationWarNotifications.Declared(
            world,
            inbox,
            war.Id,
            war.AttackerId,
            war.DefenderId,
            war.DeclaredAt,
            war.StartsAt,
            war.HeadquartersId
        );

    private void NotifyInvitation(WorldAction world, int from, int to, long now, InboxNotificationType type) =>
        CorporationWarNotifications.Invitation(world, inbox, from, to, now, type);

    private void End(WorldAction world, SavedCorporationWar war, CorporationWarEndReason reason, long at, long cooldownStart)
    {
        war.EndingAt = at;
        long finish = checked(cooldownStart + CorporationWarRules.Cooldown);
        war.EndsAt = finish;
        war.ReasonEnded = reason;
        war.BillId = null;
        war.BillIssuedAt = null;
        war.BillDueAt = null;
        war.OpenForAllies = false;
        if (reason != CorporationWarEndReason.Surrender)
        {
            CorporationWarNotifications.Ending(world, inbox, war.Id, war.AttackerId, war.DefenderId, at, finish, war.Mutual);
        }
    }

    private static void Renew(WorldAction world, SavedCorporationWar war, long due)
    {
        war.BillId = world.Next("WorldAllianceBill");
        war.BillIssuedAt = due;
        war.BillDueAt = checked(due + CorporationWarRules.BillingPeriod);
    }

    internal static IReadOnlyList<CorporationWarInvoice> Invoices(WorldAction world, int payerCorporationId, long now)
    {
        if (world is SqlWorldAction)
        {
            return [];
        }
        int owner = Organization(world, payerCorporationId);
        if (Payer(world, owner)?.Id != payerCorporationId)
        {
            return [];
        }
        return world
            .Filter<SavedCorporationWar>(war => war.AttackerId == owner && war.BillIssuedAt <= now && war.BillDueAt > now)
            .AsEnumerable()
            .Where(war => Payable(world, war, now))
            .OrderBy(war => war.BillDueAt)
            .ThenBy(war => war.BillId)
            .Select(war => new CorporationWarInvoice(
                war.BillId!.Value,
                war.Id,
                owner,
                war.DefenderId,
                war.BillDueAt!.Value,
                CorporationWarRules.WeeklyFeeCents
            ))
            .ToArray();
    }

    internal static bool PayInvoice(WorldAction world, long pilotId, long billId, int divisionKey, long now, bool alliance)
    {
        if (world is SqlWorldAction)
        {
            throw new InvalidDataException("War invoice payment requires a running world.");
        }
        CorporationWalletRules.CheckKey(divisionKey);
        var payer = CorporationRoleStore.Current(world, pilotId);
        int owner = Organization(world, payer.Id);
        var access = CorporationRoleStore.Effective(world, pilotId, payer).Roles;
        if (
            Payer(world, owner)?.Id != payer.Id
            || (owner != payer.Id) != alliance
            || !access.Has(CorporationRole.Accountant)
            || !CorporationWalletRules.CanTake(access, divisionKey)
        )
        {
            throw new InvalidDataException("Payment requires the billing organization's wallet withdrawal access.");
        }
        if (
            world.Filter<SavedCorporationWar>(war => war.AttackerId == owner && war.BillId == billId).SingleOrDefault() is not { } invoice
            || !Payable(world, invoice, now)
        )
        {
            return false;
        }
        if (!Charge(world, payer, divisionKey, invoice.DefenderId, now))
        {
            throw new InvalidDataException("The selected corporation wallet cannot cover the war bill.");
        }
        Renew(world, invoice, invoice.BillDueAt!.Value);
        return true;
    }

    private static bool Payable(WorldAction world, SavedCorporationWar war, long now) =>
        war is { EndingAt: null, BillId: not null }
        && war.BillIssuedAt <= now
        && now < war.BillDueAt
        && world.Set<SavedStructure>().Find(war.HeadquartersId!.Value) is { } headquarters
        && Departure(headquarters, now) is null
        && Organization(world, headquarters.CorporationId) == war.AttackerId
        && EligibilityLoss(world, war.AttackerId, now, war.DeclaredAt) is null
        && EligibilityLoss(world, war.DefenderId, now, war.DeclaredAt) is null;

    private static bool Charge(WorldAction world, SavedCorporation payer, int key, int defender, long now)
    {
        long balance = CorporationWalletStore.ReadBalance(world, payer, key);
        if (balance < CorporationWarRules.WeeklyFeeCents)
        {
            return false;
        }
        long after = balance - CorporationWarRules.WeeklyFeeCents;
        CorporationWalletStore.WriteBalance(world, payer, key, after);
        CorporationWalletStore.AddEntry(
            world,
            payer.Id,
            key,
            now,
            AllianceBillingRules.ConcordCorporationId,
            -CorporationWarRules.WeeklyFeeCents,
            after,
            WalletEntryType.WarFee,
            defender,
            $"War fee against {defender}"
        );
        return true;
    }
}
