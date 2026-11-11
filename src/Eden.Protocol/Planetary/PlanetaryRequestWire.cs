using Eden.Gameplay.Planetary;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Planetary;

// Build 3662168 eve/common/script/planet/commandStream.pyj.
internal enum PlanetaryCommandCode
{
    CreatePin = 1,
    RemovePin = 2,
    CreateLink = 3,
    RemoveLink = 4,
    UpgradeLink = 5,
    CreateRoute = 6,
    RemoveRoute = 7,
    SetSchematic = 8,
    UpgradeCommandCenter = 9,
    AddHead = 10,
    RemoveHead = 11,
    MoveHead = 12,
    InstallProgram = 13,
}

internal static class PlanetaryRequestWire
{
    internal static long Integer(object? value) =>
        value is long number ? number : throw new InvalidDataException("Expected a planetary integer.");

    internal static int Int32(object? value) => checked((int)Integer(value));

    internal static double Number(object? value) =>
        value switch
        {
            double number when double.IsFinite(number) => number,
            long number => number,
            _ => throw new InvalidDataException("Expected a finite planetary coordinate."),
        };

    internal static IReadOnlyList<object?> Sequence(object? value) =>
        value switch
        {
            List<object?> list => list,
            object?[] tuple => tuple,
            _ => throw new InvalidDataException("Expected a planetary sequence."),
        };

    internal static Dictionary<string, object?> Fields(object? value) =>
        value switch
        {
            BlueInstance { Name: "util.KeyVal" or "utillib.KeyVal", State: Dictionary<string, object?> fields } => fields,
            Dictionary<string, object?> fields => fields,
            _ => throw new InvalidDataException("Expected planetary fields."),
        };

    internal static PlanetaryId Id(object? value) =>
        value is object?[] { Length: 2 } tuple ? new(Integer(tuple[1]), Int32(tuple[0])) : new(Integer(value));

    internal static IReadOnlyList<long> Path(object? value) => Sequence(value).Select(Integer).ToArray();

    internal static IReadOnlyList<PlanetaryHead> Heads(object? value) =>
        Sequence(value)
            .Select(v =>
            {
                var tuple = BlueRead.Tuple(v, 3);
                return new PlanetaryHead(Int32(tuple[0]), Number(tuple[1]), Number(tuple[2]));
            })
            .ToArray();

    internal static IReadOnlyList<PlanetaryCargoRequest> Cargo(object? value)
    {
        var dictionary = value as Dictionary<long, object?> ?? throw new InvalidDataException("Expected planetary cargo quantities.");
        if (dictionary.Count > 128)
        {
            throw new InvalidDataException("Too many planetary cargo entries.");
        }
        return dictionary.Select(p => new PlanetaryCargoRequest(p.Key, Int32(p.Value))).ToArray();
    }

    internal static IReadOnlyList<PlanetaryCommodity> Commodities(object? value) =>
        Cargo(value).Select(c => new PlanetaryCommodity(checked((int)c.ItemId), c.Quantity)).ToArray();

    internal static IReadOnlyList<PlanetaryCommand> Commands(object? value)
    {
        var stream = Sequence(value);
        if (stream.Count is < 1 or > PlanetaryRules.MaximumCommands)
        {
            throw new InvalidDataException("Submit a bounded planetary command stream.");
        }
        return stream
            .Select(v =>
            {
                var command = BlueRead.Tuple(v, 2);
                var args = Sequence(command[1]);
                return ((PlanetaryCommandCode)Int32(command[0]), args.Count) switch
                {
                    (PlanetaryCommandCode.CreatePin, 4) => (PlanetaryCommand)
                        new CreatePlanetaryPin(Id(args[0]), Int32(args[1]), Number(args[2]), Number(args[3])),
                    (PlanetaryCommandCode.RemovePin, 1) => new RemovePlanetaryPin(Id(args[0])),
                    (PlanetaryCommandCode.CreateLink, 3) => new CreatePlanetaryLink(Id(args[0]), Id(args[1]), Int32(args[2])),
                    (PlanetaryCommandCode.RemoveLink, 2) => new RemovePlanetaryLink(Id(args[0]), Id(args[1])),
                    (PlanetaryCommandCode.UpgradeLink, 3) => new UpgradePlanetaryLink(Id(args[0]), Id(args[1]), Int32(args[2])),
                    (PlanetaryCommandCode.CreateRoute, 4) => new CreatePlanetaryRoute(
                        Id(args[0]),
                        Sequence(args[1]).Select(Id).ToArray(),
                        Int32(args[2]),
                        Int32(args[3])
                    ),
                    (PlanetaryCommandCode.RemoveRoute, 1) => new RemovePlanetaryRoute(Id(args[0])),
                    (PlanetaryCommandCode.SetSchematic, 2) => new SetPlanetarySchematic(Id(args[0]), Int32(args[1])),
                    (PlanetaryCommandCode.UpgradeCommandCenter, 2) => new UpgradePlanetaryCommand(Id(args[0]), Int32(args[1])),
                    (PlanetaryCommandCode.AddHead, 4) => new AddPlanetaryHead(
                        Id(args[0]),
                        new(Int32(args[1]), Number(args[2]), Number(args[3]))
                    ),
                    (PlanetaryCommandCode.RemoveHead, 2) => new RemovePlanetaryHead(Id(args[0]), Int32(args[1])),
                    (PlanetaryCommandCode.MoveHead, 4) => new MovePlanetaryHead(
                        Id(args[0]),
                        new(Int32(args[1]), Number(args[2]), Number(args[3]))
                    ),
                    (PlanetaryCommandCode.InstallProgram, 3) => new InstallPlanetaryProgram(
                        Id(args[0]),
                        args[1] is null ? null : Int32(args[1]),
                        Number(args[2])
                    ),
                    _ => throw new InvalidDataException("Unknown planetary command or argument count."),
                };
            })
            .ToArray();
    }
}
