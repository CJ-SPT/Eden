using Eden.Protocol.Loot;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal sealed class PilotBindings(long sessionId)
{
    private readonly Dictionary<string, string> _bindings = [];
    private readonly Dictionary<string, BlueObjectReference> _references = [];

    private int _nextSlot = 2;

    public void ReleaseLocationReferences()
    {
        foreach (
            string service in new[]
            {
                "beyonce",
                "ship",
                "invbroker",
                "dogmaIM",
                "jumpCloneSvc",
                "corpStationMgr",
                "reprocessingSvc",
                "repairSvc",
            }.Concat(_references.Keys.Where(k => k.StartsWith(WreckDefinitions.BindingPrefix, StringComparison.Ordinal)).ToArray())
        )
        {
            if (_references.Remove(service, out var reference))
            {
                _bindings.Remove(reference.Id);
            }
        }
    }

    public string? ResolveReference(object? objectId) =>
        objectId is byte[] or string ? _bindings.GetValueOrDefault(BlueMarshal.Text(objectId)) : null;

    public BlueObjectReference Reference(string service)
    {
        if (!_references.TryGetValue(service, out var reference))
        {
            int slot = _nextSlot++;
            reference = new($"N={PilotLogin.NodeId}:{sessionId}-{slot}", slot);
            _references.Add(service, reference);
            _bindings.Add(reference.Id, service);
        }
        return reference;
    }
}
