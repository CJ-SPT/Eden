using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// These records preserve the implemented empty compatibility states. They do not
// define schemas or behavior for contacts, fighters, clones, or campaigns.
internal sealed record LocalSystemState(int SystemId) : BlueModel
{
    public override object ToBlue() => new object?[] { SystemId, null };
}

internal sealed record DynamicBountyState() : BlueModel
{
    public override object ToBlue() => new object?[] { 1d, false };
}
