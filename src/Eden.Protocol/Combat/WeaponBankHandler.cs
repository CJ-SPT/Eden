using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Combat;

internal sealed record WeaponBanksWire(IReadOnlyList<WeaponBank> Banks) : BlueModel
{
    public override object ToBlue() =>
        new BlueMap<long, BlueList<long>>(Banks.ToDictionary(b => b.MasterId, b => new BlueList<long>(b.SlaveIds.ToList())));
}

internal sealed record WeaponBanksChanged(long ShipId, IReadOnlyList<WeaponBank> Banks) : PilotNotification("OnWeaponBanksChanged")
{
    public override object?[] Arguments => [ShipId, new WeaponBanksWire(Banks)];
}

[Injectable]
internal sealed class WeaponBankHandler(IPilotWeaponBanks banks, IPilotInventory inventory, IEnumerable<IModuleEffectSource> effects)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dogmaIM")
        {
            return PilotCallResult.Unsupported;
        }
        WeaponBankOperation? operation = method switch
        {
            "LinkWeapons" => WeaponBankOperation.Link,
            "MergeModuleGroups" => WeaponBankOperation.Merge,
            "PeelAndLink" => WeaponBankOperation.Peel,
            "UnlinkModule" => WeaponBankOperation.Unlink,
            "DestroyWeaponBank" => WeaponBankOperation.Destroy,
            "LinkAllWeapons" => WeaponBankOperation.LinkAll,
            "UnlinkAllModules" => WeaponBankOperation.UnlinkAll,
            _ => null,
        };
        if (operation is null)
        {
            return PilotCallResult.Unsupported;
        }
        int expected =
            operation is WeaponBankOperation.Link or WeaponBankOperation.Merge or WeaponBankOperation.Peel ? 3
            : operation is WeaponBankOperation.Unlink or WeaponBankOperation.Destroy ? 2
            : 1;
        if (args.Length != expected || args.Any(a => a is not long))
        {
            return new PilotCallResult.Failure("Invalid weapon grouping arguments.");
        }
        try
        {
            var selected = args.Skip(1).Cast<long>().ToHashSet();
            foreach (var bank in banks.Read(context.Pilot.PilotId, (long)args[0]!))
            {
                if (selected.Contains(bank.MasterId) || bank.SlaveIds.Any(selected.Contains))
                {
                    selected.Add(bank.MasterId);
                    selected.UnionWith(bank.SlaveIds);
                }
            }
            bool all = operation is WeaponBankOperation.LinkAll or WeaponBankOperation.UnlinkAll;
            if (
                inventory
                    .List(context.Pilot.PilotId, (long)args[0]!)
                    .Where(m =>
                        (all || selected.Contains(m.Id))
                        && (
                            m.GroupId
                                is CombatDefinitions.EnergyWeaponGroup
                                    or CombatDefinitions.ProjectileWeaponGroup
                                    or CombatDefinitions.HybridWeaponGroup
                            || MissileDefinitions.LauncherGroups.Contains(m.GroupId)
                        )
                    )
                    .Any(m => effects.Any(e => e.ActiveEffect(context.Pilot, m.Id) is not null))
            )
            {
                return new PilotCallResult.Failure("Deactivate the weapons before changing their group.");
            }
            var result = banks.Change(
                context.Pilot.PilotId,
                (long)args[0]!,
                operation.Value,
                args.Length > 1 ? (long)args[1]! : 0,
                args.Length > 2 ? (long)args[2]! : 0
            );
            // These RPCs update the invoking client's bank cache from their replies.
            return operation == WeaponBankOperation.Unlink ? (PilotCallResult)result.DetachedId!.Value
                : operation is WeaponBankOperation.UnlinkAll or WeaponBankOperation.Destroy ? PilotCallResult.None
                : new PilotCallResult.Success(new WeaponBanksWire(result.Banks));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
