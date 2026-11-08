using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

internal sealed record NaniteRepairBatchReply(IReadOnlyList<long> Started, IReadOnlyList<long> MissingPaste, IReadOnlyList<long> Failed)
    : BlueModel
{
    // Godma unpacks one tuple containing three native Python lists.
    public override object ToBlue() =>
        new object?[]
        {
            Started.Select(id => (object?)id).ToList(),
            MissingPaste.Select(id => (object?)id).ToList(),
            Failed.Select(id => (object?)id).ToList(),
        };
}
