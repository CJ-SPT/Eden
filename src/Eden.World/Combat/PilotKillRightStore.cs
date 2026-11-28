using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mail;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.Infrastructure.Registration;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotKillRightStore(WorldDatabase database, TimeProvider clock) : IPilotKillRights
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Kill rights require an active resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public IReadOnlyList<KillRight> Mine(long pilotId) =>
        Resident.Economy.Inspect(world =>
        {
            long now = Now;
            return Snapshot(
                world
                    .Filter<SavedKillRight>(row => row.OwnerId == pilotId && row.ExpiresAt > now)
                    .Concat(world.Filter<SavedKillRight>(row => row.TargetId == pilotId && row.ExpiresAt > now))
            );
        });

    public IReadOnlyList<KillRight> Available(long pilotId, IReadOnlyList<long> targets) =>
        Resident.Economy.Inspect(world =>
        {
            var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
            long now = Now;
            var rights = world
                .Filter<SavedKillRight>(row => targets.Contains(row.TargetId))
                .AsEnumerable()
                .GroupBy(row => row.TargetId)
                .Where(rows => rows.All(row => row.ActiveUntil <= now))
                .SelectMany(rows => rows);
            return Snapshot(rights.Where(row => row.ExpiresAt > now && CanBuy(world, pilot, row)));
        });

    public KillRight ListForSale(long pilotId, long id, long priceCents, long? restrictedTo)
    {
        if (priceCents is < 0 or > KillRightRules.MaximumPriceCents)
        {
            throw new InvalidDataException("Enter a valid kill-right price.");
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        long now = Now;
        var right = RequireOwned(world, pilotId, id, now);
        if (restrictedTo is long recipient && !IsRecipient(world, recipient))
        {
            throw new InvalidDataException("Choose a character, corporation or alliance.");
        }
        if (right.PriceCents != priceCents || right.RestrictedTo != restrictedTo)
        {
            right.PriceCents = priceCents;
            right.RestrictedTo = restrictedTo;
            Notify(
                world,
                right.TargetId,
                pilotId,
                right,
                restrictedTo is null ? InboxNotificationType.KillRightAvailableOpen : InboxNotificationType.KillRightAvailable,
                now
            );
        }
        world.Commit();
        return right.Snapshot();
    }

    public void CancelSale(long pilotId, long id, long targetId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        long now = Now;
        var right = RequireOwned(world, pilotId, id, now);
        if (right.TargetId != targetId)
        {
            throw new InvalidDataException("The kill right does not belong to this target.");
        }
        if (right.PriceCents.HasValue)
        {
            Notify(
                world,
                targetId,
                pilotId,
                right,
                right.RestrictedTo is null ? InboxNotificationType.KillRightUnavailableOpen : InboxNotificationType.KillRightUnavailable,
                now
            );
            right.PriceCents = null;
            right.RestrictedTo = null;
        }
        world.Commit();
    }

    public KillRightActivationResult Activate(long pilotId, long id, KillRightPresence presence, long? quotedPriceCents = null)
    {
        var live = Resident;
        using var selection = live.EnterPersistence("kill-right activation participants", TransactionParticipants.None);
        long? targetId = live.Economy.Inspect(world => world.Set<SavedKillRight>().Find(id)?.TargetId);
        if (targetId is not long target)
        {
            return KillRightActivationResult.Unavailable;
        }
        using var world = live.Economy.Begin(new(systems: [presence.SystemId], pilots: [pilotId, target]));
        long now = Now;
        var right = world.Set<SavedKillRight>().Find(id)!;
        if (right.ExpiresAt <= now)
        {
            return KillRightActivationResult.Expired;
        }
        var pilot = world.Pilots.Find(pilotId);
        var subject = world.Pilots.Find(target);
        if (pilot is null || subject is null || quotedPriceCents is null && right.OwnerId != pilotId)
        {
            return KillRightActivationResult.Unavailable;
        }
        long payment = 0;
        if (quotedPriceCents is long quote && right.OwnerId != pilotId)
        {
            if (!CanBuy(world, pilot, right))
            {
                return right.PriceCents.HasValue ? KillRightActivationResult.Unavailable : KillRightActivationResult.NotForSale;
            }
            payment = right.PriceCents!.Value;
            if (payment != quote)
            {
                return KillRightActivationResult.PriceChanged;
            }
        }
        if (
            pilot.ActiveShipId != presence.ShipId
            || !presence.VisibleShips.Contains(subject.ActiveShipId)
            || live.Economy.Cloaked(subject.ActiveShipId)
            || !OnGrid(pilot.ActiveShip, subject.ActiveShip, presence.SystemId)
        )
        {
            return KillRightActivationResult.TargetNotVisible;
        }
        if (TargetRights(world, target).Any(row => row.ActiveUntil > now))
        {
            return KillRightActivationResult.Unavailable;
        }
        if (payment > 0)
        {
            const int key = CorporationWalletRules.MasterKey;
            CorporationWalletStore.Transfer(
                world,
                pilotId,
                new(false, key, right.OwnerId, key, payment, "Kill right activation"),
                now,
                WalletEntryType.KillRightPurchase,
                id
            );
        }
        var state = CrimewatchPersistence.State(world, target);
        state.SystemId = presence.SystemId;
        right.ActiveUntil = checked(now + CrimewatchRules.FlagDuration.Ticks);
        state.SuspectUntil = Math.Max(state.SuspectUntil, right.ActiveUntil);
        world.Commit();
        return KillRightActivationResult.Activated;
    }

    internal static void Earn(WorldAction world, long owner, long target, AggressionDecision decision, long now)
    {
        if (world is not EconomyTransaction || decision.Flag != CrimewatchFlag.Criminal || decision.Engagement || owner == target)
        {
            return;
        }
        var existing = world.Filter<SavedKillRight>(row => row.OwnerId == owner && row.TargetId == target).SingleOrDefault();
        if (existing is not null)
        {
            if (existing.ExpiresAt > now || existing.ActiveUntil > now)
            {
                return;
            }
            world.Remove(existing);
        }
        var right = world
            .Add(
                new SavedKillRight
                {
                    OwnerId = owner,
                    TargetId = target,
                    ExpiresAt = checked(now + KillRightRules.Lifetime.Ticks),
                }
            )
            .Entity;
        Notify(world, owner, target, right, InboxNotificationType.KillRightEarned, now);
    }

    internal static long? Consume(WorldAction world, long target, long? attacker, long now)
    {
        if (world is not EconomyTransaction || attacker is null || attacker == target)
        {
            return null;
        }
        var right = TargetRights(world, target).SingleOrDefault(row => row.ActiveUntil > now);
        if (right is null)
        {
            return null;
        }
        Notify(world, right.OwnerId, target, right, InboxNotificationType.KillRightUsed, now);
        world.Remove(right);
        return right.OwnerId;
    }

    private static SavedKillRight RequireOwned(WorldAction world, long pilot, long id, long now) =>
        world.Set<SavedKillRight>().Find(id) is { } right && right.OwnerId == pilot && right.ExpiresAt > now
            ? right
            : throw new InvalidDataException("Choose an unexpired kill right you own.");

    private static IQueryable<SavedKillRight> TargetRights(WorldAction world, long target) =>
        world.Filter<SavedKillRight>(row => row.TargetId == target);

    private static KillRight[] Snapshot(IEnumerable<SavedKillRight> rights) =>
        rights.OrderBy(row => row.Id).Select(row => row.Snapshot()).ToArray();

    private static bool CanBuy(WorldAction world, Pilot pilot, SavedKillRight right) =>
        KillRightRules.CanBuy(
            right.Snapshot(),
            pilot.Id,
            pilot.CorporationId,
            world.Set<SavedAllianceMember>().Find(pilot.CorporationId)?.AllianceId
        );

    private bool OnGrid(Ship source, Ship target, int system)
    {
        if (source.Position is not { } from || target.Position is not { } to)
        {
            return false;
        }
        return (from.SystemId ?? database.Catalog.FindStation(source.StationId)?.SolarSystemId) == system
            && (to.SystemId ?? database.Catalog.FindStation(target.StationId)?.SolarSystemId) == system
            && new SpacePosition(from.X, from.Y, from.Z).DistanceTo(new(to.X, to.Y, to.Z)) <= NavigationDefinitions.MaximumTargetDistance;
    }

    private bool IsRecipient(WorldAction world, long id) =>
        id > 0
        && (
            world.PilotExists(id)
            || id <= int.MaxValue
                && (
                    world.Set<SavedCorporation>().Find((int)id) is { ClosedAt: null }
                    || world.Set<SavedAlliance>().Find((int)id) is { ClosedAt: null }
                    || database.Catalog.NpcCorporations.ContainsKey((int)id)
                )
        );

    private static void Notify(WorldAction world, long recipient, long sender, SavedKillRight right, InboxNotificationType type, long now)
    {
        var content = new KillRightNotificationContent(right.Snapshot());
        world.Add(
            new SavedInboxNotification
            {
                Type = type,
                SenderId = sender,
                ReceiverId = recipient,
                Created = now,
                Message = JsonSerializer.Serialize(content),
            }
        );
        Eden.World.Mail.PilotInboxStore.PublishResident(world);
    }
}
