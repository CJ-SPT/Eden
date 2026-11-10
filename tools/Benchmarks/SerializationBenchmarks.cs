using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Eden.Gameplay.Market;
using Eden.Protocol.Market;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;

namespace Eden.Protocol.Tests.Benchmarks;

[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private const int StationId = 60003760;
    private const int SystemId = 30000142;
    private const int RegionId = 10000002;
    private const int SellerCorporationId = 1000035;
    private const int Price = 100;
    private const int Stock = 1000;
    private const long IssuedAt = 134000000000000000;
    private Aes _aes = null!;
    private BlueMap<long, MarketOrderRow> _summary = null!;
    private EncryptedPackets _packets = null!;

    [Params(32, 4096)]
    public int Orders { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _aes = Aes.Create();
        _packets = new(Stream.Null, _aes, default);
        _summary = new(
            Enumerable
                .Range(1, Orders)
                .ToDictionary(
                    id => (long)id,
                    id => new MarketOrderRow(
                        new(id, StationId, SystemId, RegionId, SellerCorporationId, id, false, Price, Stock, Stock, IssuedAt),
                        MarketDefinitions.NpcOrderDays
                    )
                )
        );
    }

    [Benchmark]
    public byte[] EncodeMarketSummary() => BlueMarshal.Encode(_summary);

    [Benchmark]
    public Task WriteEncryptedSummary() => _packets.WriteAsync(_summary);

    [GlobalCleanup]
    public void Cleanup() => _aes.Dispose();
}
