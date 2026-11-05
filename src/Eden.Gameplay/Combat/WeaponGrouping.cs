namespace Eden.Gameplay.Combat;

public sealed record WeaponBank(long MasterId, IReadOnlyList<long> SlaveIds);

public sealed record GroupableWeapon(long Id, int TypeId);

public enum WeaponBankOperation
{
    Link,
    Merge,
    Peel,
    Unlink,
    Destroy,
    LinkAll,
    UnlinkAll,
}

public sealed record WeaponBankChange(IReadOnlyList<WeaponBank> Banks, long? DetachedId = null);

public interface IPilotWeaponBanks
{
    IReadOnlyList<WeaponBank> Read(long pilotId, long shipId);
    WeaponBankChange Change(long pilotId, long shipId, WeaponBankOperation operation, long masterId = 0, long sourceId = 0);
}

public static class WeaponGrouping
{
    public static WeaponBankChange Change(
        IReadOnlyList<WeaponBank> banks,
        IReadOnlyList<GroupableWeapon> weapons,
        WeaponBankOperation operation,
        long masterId = 0,
        long sourceId = 0
    )
    {
        var groups = banks.ToDictionary(b => b.MasterId, b => b.SlaveIds.ToList());
        if (operation == WeaponBankOperation.UnlinkAll)
        {
            return new([]);
        }
        if (operation == WeaponBankOperation.LinkAll)
        {
            groups.Clear();
            foreach (var type in weapons.GroupBy(w => w.TypeId))
            {
                var ids = type.Select(w => w.Id).ToArray();
                if (ids.Length > 1)
                {
                    groups[ids[0]] = ids.Skip(1).ToList();
                }
            }
            return Snapshot();
        }
        var master =
            weapons.SingleOrDefault(w => w.Id == masterId) ?? throw new InvalidDataException("Select an online fitted groupable weapon.");
        masterId = banks.FirstOrDefault(b => b.SlaveIds.Contains(masterId))?.MasterId ?? masterId;
        if (operation == WeaponBankOperation.Destroy)
        {
            groups.Remove(masterId);
            return Snapshot();
        }
        if (operation == WeaponBankOperation.Unlink)
        {
            if (!groups.TryGetValue(masterId, out var slaves) || slaves.Count == 0)
            {
                throw new InvalidDataException("The weapon has no linked modules.");
            }
            long detached = slaves[^1];
            slaves.RemoveAt(slaves.Count - 1);
            return Snapshot(detached);
        }
        var source =
            weapons.SingleOrDefault(w => w.Id == sourceId) ?? throw new InvalidDataException("Select an online fitted groupable weapon.");
        if (master.TypeId != source.TypeId)
        {
            throw new InvalidDataException("Group weapons of the same type.");
        }
        long sourceMaster = banks.FirstOrDefault(b => b.SlaveIds.Contains(sourceId))?.MasterId ?? sourceId;
        if (sourceMaster == masterId)
        {
            return Snapshot();
        }
        if (!groups.TryGetValue(masterId, out var destination))
        {
            destination = [];
            groups[masterId] = destination;
        }
        if (operation == WeaponBankOperation.Merge)
        {
            destination.Add(sourceMaster);
            if (groups.Remove(sourceMaster, out var merged))
            {
                destination.AddRange(merged);
            }
        }
        else
        {
            if (operation == WeaponBankOperation.Link && groups.ContainsKey(sourceMaster))
            {
                throw new InvalidDataException("Merge or peel an existing weapon group.");
            }
            if (groups.TryGetValue(sourceMaster, out var peeled) && peeled.Count > 0)
            {
                sourceId = peeled[^1];
                peeled.RemoveAt(peeled.Count - 1);
            }
            destination.Add(sourceId);
        }
        return Snapshot();

        WeaponBankChange Snapshot(long? detached = null) =>
            new(groups.Where(g => g.Value.Count > 0).Select(g => new WeaponBank(g.Key, g.Value.ToArray())).ToArray(), detached);
    }
}
