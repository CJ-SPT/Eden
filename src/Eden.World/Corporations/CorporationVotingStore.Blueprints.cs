using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationVotingStore
{
    private CorporationBlueprintLockChange? PrepareBlueprintVote(WorldAction world, SavedCorporation corporation, SavedCorporationVote row)
    {
        var vote = row.Snapshot();
        var draft = vote.Data.Draft;
        var item = world.Set<OwnedItem>().Find(draft.Parameter);
        var office = item is null ? null : world.Set<SavedCorporationOffice>().Find(item.LocationId);
        if (
            item is null
            || item.OwnerId != corporation.Id
            || office?.CorporationId != corporation.Id
            || !CorporationStorageRules.IsDivision(item.FlagId)
            || item.Quantity != 1
            || item.BlueprintRuns >= 0
            || item.TypeId != draft.Parameter1
            || draft.Parameter2 != office.Id && draft.Parameter2 != office.StationId
            || PilotIndustryStore.IsInstalled(world, item.Id)
        )
        {
            throw new InvalidDataException("Choose one uninstalled original blueprint in your corporation's office hangar.");
        }
        // Catalog definitions are immutable resident inputs; the world action remains the only saved-state authority.
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Blueprint)
        {
            throw new InvalidDataException("Only an original blueprint may be locked or unlocked by a vote.");
        }
        var locked = world.Set<SavedCorporationBlueprintLock>().Find(item.Id);
        if (draft.Type == CorporationVoteType.LockBlueprint)
        {
            if (locked is not null)
            {
                throw new InvalidDataException("This blueprint is already locked or the subject of an open lockdown vote.");
            }
            locked = world
                .Add(
                    new SavedCorporationBlueprintLock
                    {
                        ItemId = item.Id,
                        CorporationId = corporation.Id,
                        OfficeId = office.Id,
                        StationId = office.StationId,
                        TypeId = item.TypeId,
                        FlagId = item.FlagId,
                        VoteId = vote.Id,
                    }
                )
                .Entity;
            return LockChange(locked, true);
        }
        if (locked is not { Permanent: true } || locked.CorporationId != corporation.Id)
        {
            throw new InvalidDataException("Choose a blueprint locked by a successful lockdown vote.");
        }
        if (
            world
                .Set<SavedCorporationVote>()
                .Where(other => other.CorporationId == corporation.Id && other.Id != vote.Id)
                .AsEnumerable()
                .Select(other => other.Snapshot())
                .Any(other =>
                    other.Data.Draft.Type == CorporationVoteType.UnlockBlueprint
                    && other.Data.Draft.Parameter == item.Id
                    && other.Data.AppliedAt is null
                    && (
                        other.EndsAt > vote.StartedAt
                        || CorporationVotingRules.Winner(other.Data) == 0 && CorporationVotingRules.Expires(other) > vote.StartedAt
                    )
                )
        )
        {
            throw new InvalidDataException("An unlock vote or pending unlock sanction already exists for this blueprint.");
        }
        row.DataJson = JsonSerializer.Serialize(vote.Data with { BlueprintLockVoteId = locked.VoteId });
        return null;
    }

    private static long AdvanceBlueprintVote(WorldAction world, SavedCorporationVote row, long now)
    {
        var vote = row.Snapshot();
        if (vote.Data.Settled)
        {
            return long.MaxValue;
        }
        if (now < vote.EndsAt)
        {
            return vote.EndsAt;
        }
        bool passed = CorporationVotingRules.Winner(vote.Data) == 0;
        CorporationBlueprintLockChange? change = null;
        if (world.Set<SavedCorporationBlueprintLock>().Find(vote.Data.Draft.Parameter) is { } locked && locked.VoteId == vote.Id)
        {
            if (passed)
            {
                locked.Permanent = true;
            }
            else
            {
                world.Remove(locked);
                change = LockChange(locked, false);
            }
        }
        row.DataJson = JsonSerializer.Serialize(
            vote.Data with
            {
                Settled = true,
                AppliedAt = passed ? vote.EndsAt : null,
                HoldReleased = !passed,
            }
        );
        Notify(world, row.Snapshot(), CorporationVoteEvent.Closed, change);
        return long.MaxValue;
    }

    private static CorporationBlueprintLockChange UnlockBlueprint(WorldAction world, SavedCorporation corporation, CorporationVoteCase vote)
    {
        var locked = world.Set<SavedCorporationBlueprintLock>().Find(vote.Data.Draft.Parameter);
        if (locked is not { Permanent: true } || locked.CorporationId != corporation.Id || locked.VoteId != vote.Data.BlueprintLockVoteId)
        {
            throw new InvalidDataException("The approved unlock no longer applies to this blueprint's current lock.");
        }
        world.Remove(locked);
        var source = world.Set<SavedCorporationVote>().Find(locked.VoteId)!;
        source.DataJson = JsonSerializer.Serialize(source.Snapshot().Data with { HoldReleased = true });
        return LockChange(locked, false);
    }

    private static CorporationBlueprintLockChange LockChange(SavedCorporationBlueprintLock locked, bool value) =>
        new(locked.ItemId, locked.CorporationId, locked.StationId, value);
}
