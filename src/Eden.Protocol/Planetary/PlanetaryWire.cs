using System.Buffers.Binary;
using Eden.Gameplay.Planetary;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Planetary;

internal static class PlanetaryWire
{
    internal static BlueInstance KeyVal(Dictionary<string, object?> fields) => new("utillib.KeyVal", fields);

    internal static BlueInstance Pin(PlanetaryPin pin, long owner, bool publicOnly = false)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = pin.Id,
            ["pinID"] = pin.Id,
            ["typeID"] = pin.TypeId,
            ["ownerID"] = owner,
            ["latitude"] = pin.Latitude,
            ["longitude"] = pin.Longitude,
        };
        if (publicOnly)
        {
            return KeyVal(fields);
        }
        fields["contents"] = pin.Contents.ToDictionary(c => (long)c.TypeId, c => (object?)c.Quantity);
        fields["state"] = pin.NextRunAt.HasValue ? 1L : 0L;
        fields["lastRunTime"] = pin.TransferReadyAt > 0 ? pin.TransferReadyAt : pin.LastRunAt;
        fields["lastLaunchTime"] = pin.LastLaunchAt;
        fields["schematicID"] = pin.SchematicId;
        fields["hasReceivedInputs"] = pin.Contents.Count > 0;
        fields["receivedInputsLastCycle"] = pin.NextRunAt.HasValue;
        fields["programType"] = pin.Program?.TypeId;
        fields["cycleTime"] = pin.Program?.CycleTicks ?? 0L;
        fields["qtyPerCycle"] = pin.Program?.BaseQuantity ?? 0;
        fields["installTime"] = pin.Program?.InstalledAt;
        fields["expiryTime"] = pin.Program?.ExpiresAt;
        fields["headRadius"] = pin.Program?.HeadRadius ?? PlanetaryRules.MinimumHeadRadius;
        fields["heads"] = (pin.Heads ?? []).Select(h => (object?)new object?[] { h.Id, h.Latitude, h.Longitude }).ToList();
        return KeyVal(fields);
    }

    internal static Dictionary<string, object?> Colony(PlanetaryColony colony) =>
        new()
        {
            ["ownerID"] = colony.OwnerId,
            ["level"] = colony.Level,
            ["currentSimTime"] = colony.SimulatedAt,
            ["pins"] = colony.Pins.Select(p => (object?)Pin(p, colony.OwnerId)).ToList(),
            ["links"] = colony
                .Links.Select(l =>
                    (object?)KeyVal(
                        new()
                        {
                            ["endpoint1"] = l.First,
                            ["endpoint2"] = l.Second,
                            ["level"] = l.Level,
                            ["typeID"] = PlanetaryRules.LinkType,
                        }
                    )
                )
                .ToList(),
            ["routes"] = colony
                .Routes.Select(r =>
                    (object?)KeyVal(
                        new()
                        {
                            ["routeID"] = r.Id,
                            ["charID"] = colony.OwnerId,
                            ["path"] = r.Path.Cast<object?>().ToList(),
                            ["commodityTypeID"] = r.TypeId,
                            ["commodityQuantity"] = r.Quantity,
                        }
                    )
                )
                .ToList(),
        };

    internal static BlueConstructor Rows(IReadOnlyList<(string Name, BlueColumnType Type)> columns, IEnumerable<object?[]> rows)
    {
        var header = BlueRows.Descriptor(columns);
        return new(
            "carbon.common.script.sys.crowset.CRowset",
            [header, rows.Select(row => (object?)new BlueConstructor("blue.DBRow", [header, row])).ToList()]
        );
    }
}

internal sealed record PlanetaryColonyReply(PlanetaryColony Colony) : BlueModel
{
    public override object ToBlue() => PlanetaryWire.KeyVal(PlanetaryWire.Colony(Colony));
}

internal sealed record PlanetaryPlanetReply(PlanetarySnapshot Snapshot) : BlueModel
{
    public override object ToBlue()
    {
        var fields = Snapshot.Colony is { } colony ? PlanetaryWire.Colony(colony) : new Dictionary<string, object?>();
        var planet = Snapshot.Planet;
        fields["planetID"] = planet.Id;
        fields["solarSystemID"] = planet.SystemId;
        fields["planetTypeID"] = planet.TypeId;
        fields["radius"] = planet.Radius;
        fields["celestialIndex"] = planet.Index;
        return PlanetaryWire.KeyVal(fields);
    }
}

internal sealed record PlanetaryPlanetsReply(IReadOnlyList<PlanetarySummary> Colonies) : BlueModel
{
    public override object ToBlue() =>
        PlanetaryWire.Rows(
            [
                ("solarSystemID", BlueColumnType.Int32),
                ("planetID", BlueColumnType.Int32),
                ("typeID", BlueColumnType.Int32),
                ("numberOfPins", BlueColumnType.Int32),
                ("celestialIndex", BlueColumnType.UInt8),
            ],
            Colonies.Select(c => new object?[] { c.Planet.SystemId, c.Planet.Id, c.Planet.TypeId, c.PinCount, c.Planet.Index })
        );
}

internal sealed record PlanetaryLaunchesReply(IReadOnlyList<PlanetaryLaunch> Launches) : BlueModel
{
    public override object ToBlue() =>
        PlanetaryWire.Rows(
            [
                ("launchID", BlueColumnType.Int32),
                ("solarSystemID", BlueColumnType.Int32),
                ("itemID", BlueColumnType.Int64),
                ("ownerID", BlueColumnType.Int32),
                ("planetID", BlueColumnType.Int32),
                ("status", BlueColumnType.UInt8),
                ("launchTime", BlueColumnType.FileTime),
                ("x", BlueColumnType.Float64),
                ("y", BlueColumnType.Float64),
                ("z", BlueColumnType.Float64),
            ],
            Launches.Select(l =>
                new object?[] { l.Id, l.SystemId, (long)l.Id, l.OwnerId, l.PlanetId, 0L, l.Time, l.Position.X, l.Position.Y, l.Position.Z }
            )
        );
}

internal sealed record PlanetaryQuoteReply(PlanetaryProgramQuote Quote) : BlueModel
{
    public override object ToBlue() => new object?[] { Quote.BaseQuantity, Quote.CycleTicks, Quote.Cycles };
}

internal sealed record PlanetaryTransferReply(PlanetaryTransfer Transfer) : BlueModel
{
    public override object ToBlue() => new object?[] { Transfer.Colony.SimulatedAt, Transfer.SourceReadyAt };
}

internal sealed record PlanetaryResourceReply(PlanetaryResourceField Field) : BlueModel
{
    public override object ToBlue()
    {
        var bytes = new byte[Field.Coefficients.Count * sizeof(float)];
        for (int i = 0; i < Field.Coefficients.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), Field.Coefficients[i]);
        }
        return PlanetaryWire.KeyVal(
            new()
            {
                ["data"] = bytes,
                ["numBands"] = Field.Bands,
                ["proximity"] = Field.Proximity,
            }
        );
    }
}

internal sealed record PlanetaryResourceInfoReply(PlanetaryPlanet Planet) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "run" },
                BlueMarshal.Encode(new BlueMap<int, int>(PlanetaryResources.Types(Planet.TypeId).ToDictionary(t => t, _ => 1))),
                new object?[] { 1L, 0L },
            }
        );
}

internal sealed record PlanetaryPublicPinsReply(IReadOnlyList<PlanetaryColony> Colonies, bool Extractors) : BlueModel
{
    public override object ToBlue() =>
        Extractors
            ? Colonies
                .SelectMany(c => c.Pins.Where(p => p.Heads is { Count: > 0 }).Select(p => (object?)PlanetaryWire.Pin(p, c.OwnerId, true)))
                .ToList()
            : Colonies.ToDictionary(c => c.OwnerId, c => (object?)PlanetaryWire.Pin(c.Pins[0], c.OwnerId, true));
}

internal sealed record PlanetaryPublicNetworkReply(PlanetaryColony? Colony) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Colony?.Pins.Select(p => (object?)PlanetaryWire.Pin(p, Colony.OwnerId, true)).ToList() ?? [],
            Colony?.Links.Select(l => (object?)new List<object?> { l.First, l.Second }).ToList() ?? [],
        };
}
