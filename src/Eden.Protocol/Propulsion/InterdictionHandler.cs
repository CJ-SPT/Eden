using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Propulsion;

[Injectable(InjectionType.Scoped)]
internal sealed class InterdictionHandler(IPilotInterdiction interdiction, TimeProvider clock) : IModuleEffectSource
{
    private sealed record Cycle(DogmaActiveEffect Effect, InterdictionActivation Activation);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Effect.ShipId == pilot.ShipId ? cycle.Effect : null;

    private static DogmaActiveEffect Effect(DockedPilot pilot, InterdictionActivation cycle) =>
        new(
            cycle.ModuleId,
            pilot.PilotId,
            pilot.ShipId,
            null,
            cycle.Effect,
            cycle.ExpiresAt - (long)(cycle.Duration * TimeSpan.TicksPerMillisecond),
            cycle.Duration,
            0
        );

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.Remove(module, out var cycle))
        {
            context.Notifications.Add(new ShipEffectChanged(cycle.Effect, Now, false, false, Now));
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var (module, cycle) in _cycles.ToArray())
        {
            if (Now >= cycle.Activation.ExpiresAt || cycle.Effect.ShipId != context.Pilot.ShipId || !context.Flight.CanUseSelfModules)
            {
                CancelModule(context, module);
            }
        }
        context.Flight.RefreshInterdiction();
    }

    internal PilotCallResult? CallLauncher(PilotCallContext context, string method, object?[] args)
    {
        if (args[0] is not long module || !interdiction.IsLauncher(context.Pilot.PilotId, module))
        {
            return null;
        }
        if (method == "Deactivate")
        {
            return PilotCallResult.None;
        }
        if (args is not [_, _, null, long repeats] || repeats < 0 || !context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Launch an interdiction probe without a target outside warp.");
        }
        try
        {
            context.Flight.Checkpoint();
            var cycle = interdiction.Launch(context.Pilot.PilotId, context.Pilot.ShipId, module, Now);
            _cycles[module] = new(Effect(context.Pilot, cycle), cycle);
            context.Notifications.Add(new ShipEffectChanged(Effect(context.Pilot, cycle), Now, true, true));
            if (cycle.Magazine?.Current is { } magazine)
            {
                MagazineWire.NotifyQuantity(context, magazine, magazine.Quantity, Now);
            }
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    new(
                        context.Pilot.PilotId,
                        new DogmaItemKey.Item(context.Pilot.ShipId),
                        DogmaAttributeId.CapacitorCharge,
                        Now,
                        cycle.Capacitor
                    ),
                ])
            );
            context.Flight.RefreshInterdiction();
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
