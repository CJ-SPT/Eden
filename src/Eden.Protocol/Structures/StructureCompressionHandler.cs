using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureCompressionHandler(IStructureCompression compression) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "structureCompressionMgr"
            || method is not ("CompressItemInStructure" or "DecompressGasInStructure" or "GetMyGasDecompressionEfficiency")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected structure compression arguments.");
            }
            if (method == "GetMyGasDecompressionEfficiency" && args.Length == 0)
            {
                return new GasDecompressionEfficiencyReply(compression.Efficiency(context.Pilot.PilotId));
            }
            if (args is not [long itemId] || itemId <= 0 || method == "GetMyGasDecompressionEfficiency")
            {
                throw new InvalidDataException("Choose one resource stack for structure compression or gas decompression.");
            }
            var result =
                method == "CompressItemInStructure"
                    ? compression.Compress(context.Pilot.PilotId, itemId)
                    : compression.DecompressGas(context.Pilot.PilotId, itemId);
            InventoryChangesWire.NotifyItemChange(context, result.Change);
            return new StructureCompressionReply(result);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record GasDecompressionEfficiencyReply(GasDecompressionEfficiency Efficiency) : BlueModel
{
    public override object ToBlue() => new object?[] { (double)Efficiency.Structure, (double)Efficiency.Character };
}

internal sealed record StructureCompressionReply(StructureCompressionResult Result) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Result.SourceItemId,
            Result.SourceTypeId,
            Result.SourceQuantity,
            Result.OutputItemId,
            Result.OutputTypeId,
            Result.OutputQuantity,
        };
}
