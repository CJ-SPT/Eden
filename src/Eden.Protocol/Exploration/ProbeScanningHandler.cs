using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class ProbeScanningHandler(IPilotProbeScanning scanning, TimeProvider clock)
    : ISelectedPilotCallHandler,
        IModuleEffectSource
{
    private readonly Dictionary<long, DogmaActiveEffect> _launches = [];
    private long _pilot;
    private int _system;
    private ProbeScan? _scan;
    private IReadOnlyList<ScanProbe> _probes = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _launches.GetValueOrDefault(module) is { } effect && effect.ShipId == pilot.ShipId ? effect : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_launches.Remove(module, out var effect))
        {
            context.Notifications.Add(new ShipEffectChanged(effect, Now, false, false, Now));
        }
    }

    internal PilotCallResult? CallModule(PilotCallContext context, string method, object?[] args)
    {
        if (args[0] is not long module || !scanning.IsLauncher(context.Pilot.PilotId, module))
        {
            return null;
        }
        if (method == "Deactivate")
        {
            return PilotCallResult.None;
        }
        return args is [_, _, null or long, long repeats] && repeats >= 0
            ? Call(context, "dogmaIM", "LaunchProbes", [module, (long)ProbeScanRules.MaximumProbes])
            : new PilotCallResult.Failure("Invalid probe launch arguments.");
    }

    internal void Enter(PilotCallContext context)
    {
        if (_pilot == context.Pilot.PilotId && _system == context.Pilot.SystemId)
        {
            return;
        }
        _pilot = context.Pilot.PilotId;
        _system = context.Pilot.SystemId;
        _probes = scanning.List(_pilot, _system, Now);
        _scan = scanning.Pending(_pilot);
        foreach (var probe in _probes)
        {
            context.Notifications.Add(new NewScanProbe(probe));
        }
        if (_scan is { } scan && scan.SystemId == _system && scan.ShipId == context.Pilot.ShipId)
        {
            context.Notifications.Add(new SystemScanStarted(scan));
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        Enter(context);
        foreach (var (module, effect) in _launches.ToArray())
        {
            if (
                effect.ShipId != context.Pilot.ShipId
                || !context.Flight.CanUseSelfModules
                || effect.StartTime + (effect.DurationMilliseconds ?? 0) * TimeSpan.TicksPerMillisecond <= Now
            )
            {
                CancelModule(context, module);
            }
        }
        if (_scan is { } scan && scan.ReadyAt <= Now)
        {
            var results = scanning.Complete(_pilot, scan, Now);
            if (scan.SystemId == _system && scan.ShipId == context.Pilot.ShipId)
            {
                context.Notifications.Add(new SystemScanStopped(scan, results));
            }
            _scan = null;
        }
        foreach (var probe in _probes.Where(p => p.ExpiresAt <= Now))
        {
            context.Notifications.Add(new RemoveScanProbe(probe.Id));
        }
        if (_probes.Any(p => p.ExpiresAt <= Now))
        {
            _probes = _probes.Where(p => p.ExpiresAt > Now).ToArray();
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool launch = service == "dogmaIM" && method == "LaunchProbes";
        if (
            !launch
            && (service != "@systemScan" || method is not ("RequestScans" or "RecoverProbes" or "DestroyProbe" or "ReconnectToLostProbes"))
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected probe command arguments.");
            }
            context.Flight.Checkpoint();
            if (!context.Flight.CanUseSelfModules || launch && !context.Flight.CanTarget)
            {
                throw new InvalidDataException("Wait until your ship is ready to operate probes in space.");
            }
            Advance(context);
            if (launch && args is [long module, long count] && count is > 0 and <= ProbeScanRules.MaximumProbes)
            {
                var result = scanning.Launch(_pilot, module, (int)count, Now);
                MagazineWire.NotifyQuantity(context, result.Magazine, result.Magazine.Quantity, Now);
                DogmaActiveEffect effect = new(
                    module,
                    _pilot,
                    context.Pilot.ShipId,
                    context.Pilot.ShipId,
                    DogmaEffect.UseMissiles,
                    Now,
                    (result.Magazine.ReadyAt - Now) / (double)TimeSpan.TicksPerMillisecond,
                    0
                );
                _launches[module] = effect;
                context.Notifications.Add(new ShipEffectChanged(effect, Now, true, true));
                foreach (var probe in result.Probes)
                {
                    context.Notifications.Add(new NewScanProbe(probe));
                }
                _probes = _probes.Concat(result.Probes).ToArray();
                return PilotCallResult.None;
            }
            if (
                method == "RequestScans"
                && args is [Dictionary<long, object?> probes]
                && probes.Count is > 0 and <= ProbeScanRules.MaximumProbes
            )
            {
                _scan = scanning.Begin(_pilot, probes.Select(p => ProbeScanningWire.Placement(p.Key, p.Value)).ToArray(), Now);
                _probes = _probes.ExceptBy(_scan.Probes.Select(p => p.Id), p => p.Id).Concat(_scan.Probes).ToArray();
                context.Notifications.Add(new SystemScanStarted(_scan));
                return PilotCallResult.None;
            }
            if (method == "RequestScans" && args is [null])
            {
                throw new InvalidDataException("Launch scanner probes before analyzing cosmic signatures.");
            }
            if (method == "RecoverProbes" && args is [List<object?> ids] && ids.All(i => i is long))
            {
                var result = scanning.Recover(_pilot, ids.Cast<long>().ToArray(), Now);
                foreach (var change in result.Items)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                Remove(context, result.Removed);
                return new RecoveredProbeIds(result.Removed);
            }
            if (method == "DestroyProbe" && args is [long id])
            {
                scanning.Destroy(_pilot, id, Now);
                Remove(context, [id]);
                return PilotCallResult.None;
            }
            if (method == "ReconnectToLostProbes" && args.Length == 0)
            {
                var current = scanning.List(_pilot, _system, Now);
                foreach (var probe in current.ExceptBy(_probes.Select(p => p.Id), p => p.Id))
                {
                    context.Notifications.Add(new NewScanProbe(probe));
                }
                _probes = current;
                return PilotCallResult.None;
            }
            return new PilotCallResult.Failure("Invalid probe command arguments.");
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void Remove(PilotCallContext context, IReadOnlyList<long> ids)
    {
        foreach (long id in ids)
        {
            context.Notifications.Add(new RemoveScanProbe(id));
        }
        _probes = _probes.Where(p => !ids.Contains(p.Id)).ToArray();
    }
}
