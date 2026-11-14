using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal enum PilotBindingLifetime
{
    Account,
    Pilot,
    Location,
}

internal sealed class PilotBindings(long sessionId)
{
    private sealed record Binding(string Service, PilotBindingLifetime Lifetime, BlueObjectReference Reference);

    private readonly Dictionary<string, Binding> _bindings = [];
    private readonly Dictionary<string, Binding> _references = [];
    private int _nextSlot = 2;

    public void ReleasePilotReferences()
    {
        foreach (var binding in _references.Values.Where(b => b.Lifetime != PilotBindingLifetime.Account).ToArray())
        {
            _references.Remove(binding.Service);
            _bindings.Remove(binding.Reference.Id);
        }
    }

    public void ReleaseFleetReferences(long? keepFleet = null)
    {
        foreach (
            var binding in _references
                .Values.Where(b => b.Service.StartsWith("@fleet:", StringComparison.Ordinal) && b.Service != $"@fleet:{keepFleet}")
                .ToArray()
        )
        {
            _references.Remove(binding.Service);
            _bindings.Remove(binding.Reference.Id);
        }
    }

    public void ReleaseCorporationReferences() => ReleaseReference("corpRegistry");

    public void ReleaseReference(string service)
    {
        if (_references.Remove(service, out var binding))
        {
            _bindings.Remove(binding.Reference.Id);
        }
    }

    public void ReleaseLocationReferences()
    {
        foreach (var binding in _references.Values.Where(b => b.Lifetime == PilotBindingLifetime.Location).ToArray())
        {
            _references.Remove(binding.Service);
            _bindings.Remove(binding.Reference.Id);
        }
    }

    public string? ResolveReference(object? objectId) =>
        objectId is byte[] or string ? _bindings.GetValueOrDefault(BlueMarshal.Text(objectId))?.Service : null;

    public BlueObjectReference Reference(string service, PilotBindingLifetime lifetime)
    {
        if (!_references.TryGetValue(service, out var binding))
        {
            int slot = _nextSlot++;
            binding = new(service, lifetime, new($"N={PilotLogin.NodeId}:{sessionId}-{slot}", slot));
            _references.Add(service, binding);
            _bindings.Add(binding.Reference.Id, binding);
        }
        if (binding.Lifetime != lifetime)
        {
            throw new InvalidOperationException("A bound service must have one lifetime.");
        }
        return binding.Reference;
    }
}
