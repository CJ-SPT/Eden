using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Sovereignty;

[Injectable(InjectionType.Scoped)]
internal sealed class EntosisHandler(
    IPilotEntosis entosis,
    Eden.Gameplay.Structures.IStructureContacts structures,
    TimeProvider clock,
    ISovereigntyResources? resources = null
) : IModuleEffectSource
{
    internal const string EffectName = "entosisLink";
    private readonly Dictionary<long, EntosisCycle> _effects = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal PilotCallResult? Launch(PilotCallContext context, IReadOnlyList<DroneStack> items)
    {
        if (!entosis.Available)
        {
            return null;
        }
        if (resources is not null && items.Any(item => resources.IsSkyhookItem(context.Pilot.PilotId, item.Id)))
        {
            if (items.Count != 1 || items[0].Quantity != 1)
            {
                return new PilotCallResult.Failure("Deploy one skyhook at a time.");
            }
            try
            {
                var skyhook = resources.DeploySkyhook(context.Pilot.PilotId, items[0].Id);
                foreach (var change in skyhook.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                context.Flight.RefreshStructures(structures.Contacts(context.Pilot.SystemId));
                return new SovereigntyLaunchReply(items[0].Id, skyhook.Skyhook.Id);
            }
            catch (UnauthorizedAccessException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (!items.Any(item => entosis.IsHubItem(context.Pilot.PilotId, item.Id)))
        {
            return null;
        }
        if (items.Count != 1 || items[0].Quantity != 1)
        {
            return new PilotCallResult.Failure("Deploy one sovereignty hub at a time.");
        }
        var result = entosis.Deploy(context.Pilot.PilotId, items[0].Id);
        foreach (var change in result.Changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        context.Flight.RefreshStructures(structures.Contacts(context.Pilot.SystemId));
        return new SovereigntyLaunchReply(items[0].Id, result.Structure.Id);
    }

    internal PilotCallResult Call(PilotCallContext context, string method, object?[] args)
    {
        try
        {
            if (method == "Deactivate" && args is [long module, _])
            {
                entosis.Stop(context.Pilot.PilotId, module, Now);
                return PilotCallResult.None;
            }
            if (
                method != "Activate"
                || args is not [long item, _, long hub, long repeats]
                || !context.Flight.CanTarget
                || !context.Flight.CanUseSelfModules
                || context.Flight.InWarp
                || context.Flight.PreparingWarp
            )
            {
                return new PilotCallResult.Failure("Activate Entosis on a locked target outside warp.");
            }
            context.Flight.Checkpoint();
            Publish(context, entosis.Activate(context.Pilot.PilotId, context.Pilot.ShipId, item, hub, repeats, Now));
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static DogmaActiveEffect Effect(EntosisCycle cycle) =>
        new(
            cycle.ModuleId,
            cycle.PilotId,
            cycle.ShipId,
            cycle.HubId,
            EntosisRules.Effect,
            cycle.StartedAt,
            (cycle.EndsAt - cycle.StartedAt) / (double)TimeSpan.TicksPerMillisecond,
            cycle.Repeats
        );

    private static WeaponContact Beam(EntosisCycle cycle) =>
        new(
            cycle.ModuleId,
            cycle.TypeId,
            0,
            cycle.HubId,
            cycle.StartedAt,
            (cycle.EndsAt - cycle.StartedAt) / (double)TimeSpan.TicksPerMillisecond,
            "effects.EntosisBeam",
            Offensive: false
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        entosis.Available
        && entosis
            .Cycles(pilot.PilotId)
            .FirstOrDefault(cycle => cycle.ModuleId == module && cycle.ShipId == pilot.ShipId && cycle.EndsAt > Now)
            is { } cycle
            ? Effect(cycle)
            : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (entosis.Available)
        {
            entosis.Stop(context.Pilot.PilotId, module, Now);
        }
    }

    private void Publish(PilotCallContext context, EntosisActivation activation)
    {
        var cycle = activation.Cycle;
        if (_effects.Remove(cycle.ModuleId, out var previous))
        {
            context.Flight.SetWeaponEffect(Beam(previous), false);
        }
        _effects[cycle.ModuleId] = cycle;
        context.Notifications.Add(new ShipEffectChanged(Effect(cycle), Now, true, true));
        context.Flight.SetWeaponEffect(Beam(cycle), true);
        foreach (var change in activation.Changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(cycle.PilotId, new DogmaItemKey.Item(cycle.ShipId), DogmaAttributeId.CapacitorCharge, Now, activation.Capacitor),
            ])
        );
        context.Flight.RefreshEntosis();
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected || !entosis.Available)
        {
            return;
        }
        entosis.Advance(context.Pilot.SystemId, Now);
        var current = entosis.Cycles(context.Pilot.PilotId);
        foreach (var previous in _effects.Values.Where(row => !current.Any(cycle => cycle.ModuleId == row.ModuleId)).ToArray())
        {
            _effects.Remove(previous.ModuleId);
            context.Notifications.Add(new ShipEffectChanged(Effect(previous), Now, false, false, Now));
            context.Flight.SetWeaponEffect(Beam(previous), false);
        }
        foreach (var cycle in current)
        {
            if (cycle.EndsAt > Now)
            {
                continue;
            }
            try
            {
                if (entosis.Renew(cycle.PilotId, cycle.ModuleId, Now) is { } renewal)
                {
                    Publish(context, renewal);
                    continue;
                }
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                entosis.Stop(cycle.PilotId, cycle.ModuleId, Now);
                _ = entosis.Renew(cycle.PilotId, cycle.ModuleId, Now);
            }
            if (_effects.Remove(cycle.ModuleId, out var ended))
            {
                context.Notifications.Add(new ShipEffectChanged(Effect(ended), Now, false, false, Now));
                context.Flight.SetWeaponEffect(Beam(ended), false);
            }
        }
        context.Flight.RefreshEntosis();
    }

    internal void Cancel(PilotCallContext context)
    {
        if (context.Selected && entosis.Available)
        {
            entosis.Disconnect(context.Pilot.PilotId, Now);
        }
        foreach (var cycle in _effects.Values)
        {
            context.Flight.SetWeaponEffect(Beam(cycle), false);
        }
        _effects.Clear();
    }
}
