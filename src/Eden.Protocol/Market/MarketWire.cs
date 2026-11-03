using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

internal static class MarketWire
{
    private static readonly (string Name, BlueColumnType Type)[] s_columns =
    [
        ("orderID", BlueColumnType.Int64),
        ("typeID", BlueColumnType.Int32),
        ("charID", BlueColumnType.Int32),
        ("regionID", BlueColumnType.Int32),
        ("stationID", BlueColumnType.Int64),
        ("solarSystemID", BlueColumnType.Int32),
        ("range", BlueColumnType.Int16),
        ("bid", BlueColumnType.Boolean),
        ("price", BlueColumnType.Float64),
        ("volEntered", BlueColumnType.Int32),
        ("volRemaining", BlueColumnType.Int32),
        ("minVolume", BlueColumnType.Int32),
        ("issueDate", BlueColumnType.Int64),
        ("duration", BlueColumnType.Int16),
        ("jumps", BlueColumnType.Int32),
        ("escrow", BlueColumnType.Float64),
        ("isCorp", BlueColumnType.Boolean),
    ];

    private static double Isk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;

    internal static BlueConstructor Row(MarketOrder order, int duration = MarketDefinitions.NpcOrderDays) =>
        new(
            "blue.DBRow",
            [
                BlueRows.Descriptor(s_columns),
                new object?[]
                {
                    order.Id,
                    order.TypeId,
                    order.OwnerId,
                    order.RegionId,
                    order.StationId,
                    order.SystemId,
                    MarketDefinitions.StationRange,
                    order.Bid,
                    Isk(order.PriceCents),
                    order.Entered,
                    order.Remaining,
                    1,
                    order.Issued,
                    duration,
                    0,
                    0d,
                    false,
                },
            ]
        );

    internal static BlueConstructor Rows(IEnumerable<MarketOrder> orders) =>
        new("carbon.common.script.sys.crowset.CRowset", [BlueRows.Descriptor(s_columns), orders.Select(o => (object?)Row(o)).ToList()]);

    internal static BlueConstructor HistoryRows(IEnumerable<MarketHistoryDay> days)
    {
        var header = BlueRows.Descriptor([
            ("historyDate", BlueColumnType.Int64),
            ("lowPrice", BlueColumnType.Float64),
            ("highPrice", BlueColumnType.Float64),
            ("avgPrice", BlueColumnType.Float64),
            ("volume", BlueColumnType.Int64),
            ("orders", BlueColumnType.Int32),
        ]);
        return new(
            "carbon.common.script.sys.crowset.CRowset",
            [
                header,
                days.Select(d =>
                        (object?)
                            new BlueConstructor(
                                "blue.DBRow",
                                [
                                    header,
                                    new object?[]
                                    {
                                        d.Date,
                                        Isk(d.LowPriceCents),
                                        Isk(d.HighPriceCents),
                                        (double)d.AveragePriceCents / MarketDefinitions.CentsPerIsk,
                                        d.Volume,
                                        d.Orders,
                                    },
                                ]
                            )
                    )
                    .ToList(),
            ]
        );
    }
}
