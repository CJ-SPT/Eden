using Eden.Gameplay.Information;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Information;

[Injectable(InjectionType.Scoped)]
internal sealed class DirectionalScanHandler(IPilotInformation information, TimeProvider clock) : ISelectedPilotCallHandler
{
    private sealed record ScanName(long Id, string Name);

    private string? _binding;
    private long _readyAt;
    private long _pilot;
    private int _system;
    private IReadOnlyList<ScanName> _names = [];

    internal IEnumerable<LocationRow> Names(PilotCallContext context, IReadOnlySet<long> ids) =>
        context.Selected
        && context.ResolveReference(_binding) == "@systemScan"
        && context.Pilot.PilotId == _pilot
        && context.Pilot.SystemId == _system
        && context.Pilot.Position is not null
            // The client primes off-grid names through config.GetMultiLocationsEx. D-scan
            // does not disclose coordinates; only the ordinary visible-object path may do so.
            ? _names.Where(n => ids.Contains(n.Id)).Select(n => new LocationRow(n.Id, n.Name, _system, 0, 0, 0))
            : [];

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "@systemScan" || method != "ConeScan")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 5 || args.Any(a => a is not (long or double)))
        {
            return new PilotCallResult.Failure("Invalid directional scan arguments.");
        }
        try
        {
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            if (now < _readyAt)
            {
                return new PilotCallResult.Failure("The directional scanner is still resetting.");
            }
            context.Flight.Checkpoint();
            // Directional scanning is available during warp; targeting is not.
            if (!context.Flight.CanUseSelfModules)
            {
                return new PilotCallResult.Failure("Use the directional scanner while in space.");
            }
            var contacts = information.Directional(
                context.Pilot.PilotId,
                Convert.ToDouble(args[0]),
                Convert.ToDouble(args[1]),
                new(Convert.ToDouble(args[2]), Convert.ToDouble(args[3]), Convert.ToDouble(args[4])),
                now
            );
            _readyAt = checked(now + InformationRules.DirectionalScanCooldown.Ticks);
            _binding = context.Reference("@systemScan", PilotBindingLifetime.Location).Id;
            _pilot = context.Pilot.PilotId;
            _system = context.Pilot.SystemId;
            _names = contacts.Select(c => new ScanName(c.Id, c.Name)).ToArray();
            return new DirectionalScanReply(contacts);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
