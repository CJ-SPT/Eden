using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal sealed record InformationErrorValues(string Information) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["info"] = Information };
}

internal sealed record NotificationErrorValues(string Message) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["notify"] = new BlueUnicode(Message) };
}

internal sealed record CargoSpaceErrorValues(double Available, double Required) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["available"] = Available, ["volume"] = Required };
}

internal sealed record TargetLimitErrorValues(int Limit) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["limit"] = Limit };
}
