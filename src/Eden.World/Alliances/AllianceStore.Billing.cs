using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Mail;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Alliances;

internal sealed partial class AllianceStore
{
    public IReadOnlyList<AllianceBill> Bills(long actorId)
    {
        AdvanceBilling();
        return Resident.Economy.Inspect(world =>
        {
            var corporation = Financial(world, actorId, false);
            int alliance = world.Set<SavedAllianceMember>().Find(corporation.Id)!.AllianceId;
            return (IReadOnlyList<AllianceBill>)
                world.Filter<SavedAllianceBill>(b => b.AllianceId == alliance).OrderBy(b => b.Id).AsEnumerable().Select(Bill).ToArray();
        });
    }

    public long Balance(long actorId) =>
        Resident.Economy.Inspect(world =>
        {
            var corporation = Financial(world, actorId, false);
            int key = CorporationWalletStore.SelectedKey(world, actorId, corporation);
            return CorporationWalletStore.ReadBalance(world, corporation, key);
        });

    public void Pay(long actorId, long billId, int divisionKey)
    {
        CorporationWalletRules.CheckKey(divisionKey);
        AdvanceBilling();
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = Financial(world, actorId, true);
        int alliance = world.Set<SavedAllianceMember>().Find(corporation.Id)!.AllianceId;
        var bill = world.Set<SavedAllianceBill>().Find(billId);
        if (
            bill is null
            || bill.AllianceId != alliance
            || !CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, corporation).Roles, divisionKey)
        )
        {
            throw new InvalidDataException("Choose an alliance bill and an authorized corporation wallet division.");
        }
        if (bill.PaidAt.HasValue)
        {
            return;
        }
        if (!Settle(world, bill, corporation, divisionKey, Now))
        {
            throw new InvalidDataException("The executor wallet has insufficient ISK for this maintenance bill.");
        }
        world.Commit();
    }

    private static SavedCorporation Financial(WorldAction world, long actorId, bool write)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        int allianceId =
            world.Set<SavedAllianceMember>().Find(corporation.Id)?.AllianceId
            ?? throw new InvalidDataException("Your corporation is not in an alliance.");
        var alliance = Active(world, allianceId);
        var role = write ? CorporationRole.Accountant : CorporationRole.Accountant | CorporationRole.JuniorAccountant;
        if (alliance.ExecutorCorporationId != corporation.Id || !CorporationRoleStore.Has(world, actorId, corporation, role))
        {
            throw new InvalidDataException("Alliance executor financial authority is required.");
        }
        return corporation;
    }

    private static AllianceBill Bill(SavedAllianceBill row) =>
        new(
            row.Id,
            row.AllianceId,
            row.IssuedAt,
            row.DueAt,
            row.AmountCents,
            row.CorporationCount,
            row.PaidAt,
            row.PaidByCorporationId,
            row.DivisionKey
        );

    private void Issue(WorldAction world, int allianceId, long issuedAt, long dueAt)
    {
        int count = world.Filter<SavedAllianceMember>(m => m.AllianceId == allianceId).Count();
        world.Add(
            new SavedAllianceBill
            {
                Id = world.Next("WorldAllianceBill"),
                AllianceId = allianceId,
                IssuedAt = issuedAt,
                DueAt = dueAt,
                AmountCents = AllianceBillingRules.Amount(count),
                CorporationCount = count,
                DivisionKey = CorporationWalletRules.MasterKey,
            }
        );
        var executor = world.Set<SavedAlliance>().Find(allianceId)!.ExecutorCorporationId;
        if (executor is not int corporationId || world.Set<SavedCorporation>().Find(corporationId) is not { } corporation)
        {
            return;
        }
        foreach (var pilot in world.Pilots.Where(p => p.CorporationId == corporationId))
        {
            if (CorporationRoleStore.Has(world, pilot.Id, corporation, CorporationRole.Accountant | CorporationRole.JuniorAccountant))
            {
                world.Add(
                    new SavedInboxNotification
                    {
                        Type = InboxNotificationType.AllianceMaintenanceBill,
                        SenderId = AllianceBillingRules.ConcordCorporationId,
                        ReceiverId = pilot.Id,
                        Created = issuedAt,
                        AllianceId = allianceId,
                    }
                );
            }
        }
        world.AfterCommit(inbox.Publish);
    }

    private static bool Settle(WorldAction world, SavedAllianceBill bill, SavedCorporation corporation, int key, long now)
    {
        if (bill.PaidAt.HasValue)
        {
            return true;
        }
        long before = CorporationWalletStore.ReadBalance(world, corporation, key);
        if (before < bill.AmountCents)
        {
            return false;
        }
        long after = before - bill.AmountCents;
        CorporationWalletStore.WriteBalance(world, corporation, key, after);
        CorporationWalletStore.AddEntry(
            world,
            corporation.Id,
            key,
            now,
            AllianceBillingRules.ConcordCorporationId,
            -bill.AmountCents,
            after,
            WalletEntryType.AllianceMaintenance,
            bill.Id,
            "Alliance maintenance"
        );
        bill.PaidAt = now;
        bill.PaidByCorporationId = corporation.Id;
        bill.DivisionKey = key;
        return true;
    }

    private static bool AutoPay(WorldAction world, SavedAlliance alliance, SavedAllianceBill bill, long now)
    {
        if (alliance.ExecutorCorporationId is not int id || world.Set<SavedCorporation>().Find(id) is not { ClosedAt: null } corporation)
        {
            return false;
        }
        var settings = CorporationBillingStore.Saved(world, id);
        return settings is not null
            && settings.CorporationId == id
            && settings.Rules.Any(r => r.BillTypeId == AllianceBillingRules.MaintenanceBillType && r.Enabled)
            && Settle(world, bill, corporation, settings.DivisionKey, now);
    }

    private long BillingDeadline() =>
        LiveReadModels
            .Find(database.Path)!
            .AllianceBillingDeadline(() =>
                Resident.Economy.Inspect(world =>
                {
                    long next = long.MaxValue;
                    foreach (var alliance in world.Set<SavedAlliance>().Where(a => a.ClosedAt == null))
                    {
                        long? closure = world.Set<SavedAllianceLifecycle>().Find(alliance.Id)?.ClosureAt;
                        var bills = world.Filter<SavedAllianceBill>(b => b.AllianceId == alliance.Id && !b.Renewed).ToArray();
                        if (bills.Length == 0 && !closure.HasValue)
                        {
                            return 0;
                        }
                        next = Math.Min(next, closure ?? long.MaxValue);
                        foreach (var bill in bills)
                        {
                            next = Math.Min(next, bill.DueAt);
                        }
                    }
                    return next;
                })
            );

    private void AdvanceBilling()
    {
        long now = Now;
        if (BillingDeadline() > now)
        {
            return;
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        bool closed = false;
        foreach (var alliance in world.Set<SavedAlliance>().Where(a => a.ClosedAt == null).ToArray())
        {
            var lifecycle = world.Set<SavedAllianceLifecycle>().Find(alliance.Id);
            if (lifecycle?.ClosureAt is long closing && closing <= now)
            {
                CloseAlliance(world, alliance, closing);
                closed = true;
                continue;
            }
            if (!world.Filter<SavedAllianceMember>(m => m.AllianceId == alliance.Id).Any())
            {
                ScheduleEmpty(world, alliance.Id, now);
                continue;
            }
            var bill = world
                .Filter<SavedAllianceBill>(b => b.AllianceId == alliance.Id && !b.Renewed)
                .OrderBy(b => b.DueAt)
                .FirstOrDefault();
            if (bill is null)
            {
                Issue(world, alliance.Id, alliance.CreatedAt, alliance.CreatedAt + AllianceBillingRules.MaintenancePeriod.Ticks);
                bill = world.Filter<SavedAllianceBill>(b => b.AllianceId == alliance.Id && !b.Renewed).Single();
            }
            while (bill.DueAt <= now)
            {
                if (!bill.PaidAt.HasValue)
                {
                    _ = AutoPay(world, alliance, bill, now);
                }
                if (!bill.PaidAt.HasValue)
                {
                    long closingAt = AllianceBillingRules.NextDowntime(bill.DueAt);
                    if (closingAt <= now)
                    {
                        CloseAlliance(world, alliance, closingAt);
                        closed = true;
                    }
                    break;
                }
                bill.Renewed = true;
                Issue(world, alliance.Id, bill.DueAt, bill.DueAt + AllianceBillingRules.MaintenancePeriod.Ticks);
                bill = world.Filter<SavedAllianceBill>(b => b.AllianceId == alliance.Id && !b.Renewed).Single();
            }
        }
        world.Commit();
        if (closed)
        {
            database.NotifyAccessGroupsChanged();
        }
    }

    internal static void ScheduleEmpty(WorldAction world, int allianceId, long now)
    {
        if (world.Filter<SavedAllianceMember>(m => m.AllianceId == allianceId).Any())
        {
            return;
        }
        var row =
            world.Set<SavedAllianceLifecycle>().Find(allianceId)
            ?? world.Add(new SavedAllianceLifecycle { AllianceId = allianceId }).Entity;
        row.ClosureAt ??= AllianceBillingRules.NextDowntime(now);
    }

    private static void CloseAlliance(WorldAction world, SavedAlliance alliance, long at)
    {
        var before = Snapshot(world, alliance.Id)!;
        foreach (var member in world.Filter<SavedAllianceMember>(m => m.AllianceId == alliance.Id).ToArray())
        {
            Depart(world, member, at);
        }
        foreach (var application in world.Filter<SavedAllianceApplication>(a => a.AllianceId == alliance.Id).ToArray())
        {
            if (application.Status is not (AllianceApplicationStatus.New or AllianceApplicationStatus.Accepted))
            {
                continue;
            }
            var previous = Application(application);
            application.Status = AllianceApplicationStatus.Rejected;
            application.EffectiveAt = null;
            Record(
                world,
                new(0, alliance.Id, application.CorporationId, ApplicationBefore: previous, ApplicationAfter: Application(application))
            );
        }
        alliance.ClosedAt = at;
        alliance.ExecutorCorporationId = null;
        Record(world, new(0, alliance.Id, 0, Before: before, After: Snapshot(world, alliance.Id)));
    }
}
