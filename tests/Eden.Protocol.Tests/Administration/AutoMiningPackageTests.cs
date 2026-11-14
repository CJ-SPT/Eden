using System.IO.Compression;
using Eden.Gameplay.Mining;
using Eden.Modding;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Modding;
using Eden.World.Modding;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed class AutoMiningPackageTests
{
    [PinnedSkillClientFact]
    public async Task BundledPanelCompilesAndActiveModEnablesSelectedPilotCalls()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        var store = new ModStore(database);
        string archive = Path.Combine(Path.GetDirectoryName(world.Path)!, "AutoMining.edenmod");
        Assert.True(await ModCommands.TryRun(["--mod-export-automining", archive], database));
        var package = ModPackages.Read(archive);
        Assert.Equal(AutoMiningFilter.ModId, package.Manifest.Id);
        Assert.Equal("client/automining.py", package.Manifest.ClientEntry);
        Assert.Equal("1.2.2", package.Manifest.Version);
        string hash = store.Install(archive);
        store.Enable(hash, true);
        var set = await new ModPublisher(database, store).Build(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
        using (var code = ZipFile.OpenRead(Path.Combine(store.SetDirectory(set.Id), "code.ccp")))
        {
            Assert.NotNull(code.GetEntry("eden_mod_loader.pyj"));
            Assert.Contains(code.Entries, entry => entry.FullName.EndsWith("/automining.pyj", StringComparison.Ordinal));
        }
        Assert.True(store.BeginActivation());
        using var services = ServerServices.Build(new WorldDatabase(world.Path), liveWorld: true);
        var runtime = services.GetRequiredService<ModRuntime>();
        Assert.False(runtime.AutoMiningEnabled);
        runtime.Start();
        Assert.True(runtime.AutoMiningEnabled);
        store.CompleteActivation();

        const long stationId = 60003760;
        var replies = await Exchange("Veldspar");
        Assert.Equal(
            ["", "Veldspar", "Veldspar", "Veldspar", "Veldspar", "Veldspar"],
            replies.Select(reply => BlueMarshal.Text(reply["oreFilter"]))
        );
        var oreGroups = Assert.IsType<List<object?>>(replies[0]["oreGroups"]).Select(BlueMarshal.Text).ToArray();
        Assert.Equal(AutoMiningFilter.OreGroupNames, oreGroups);
        Assert.Contains("Mercoxit", oreGroups);
        Assert.Contains("Veldspar", oreGroups);
        Assert.Equal(0L, replies[0]["unloadStation"]);
        Assert.Equal(95L, replies[0]["unloadThreshold"]);
        Assert.Equal(stationId, replies[^1]["unloadStation"]);
        Assert.Equal(80L, replies[^1]["unloadThreshold"]);
        Assert.Equal((long)MiningFamily.Ore, replies[0]["resourceFamily"]);
        Assert.Equal((long)AutoMiningTargetOrder.Nearest, replies[0]["targetOrder"]);
        Assert.Equal(false, replies[0]["approach"]);
        Assert.Equal(false, replies[0]["jettison"]);
        Assert.All(
            replies,
            reply =>
            {
                Assert.Equal(false, reply["running"]);
                Assert.Equal("Stopped", BlueMarshal.Text(reply["status"]));
            }
        );
        var reconnected = Assert.Single(await Exchange(null));
        Assert.Equal("Veldspar", BlueMarshal.Text(reconnected["oreFilter"]));
        Assert.Equal(stationId, reconnected["unloadStation"]);
        Assert.Equal(80L, reconnected["unloadThreshold"]);
        Assert.Equal((long)MiningFamily.Gas, reconnected["resourceFamily"]);
        Assert.Equal((long)AutoMiningTargetOrder.Smallest, reconnected["targetOrder"]);
        Assert.Equal(true, reconnected["approach"]);
        Assert.Equal(true, reconnected["jettison"]);

        async Task<List<Dictionary<string, object?>>> Exchange(string? filter)
        {
            using var scope = services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
            const string service = "edenAutoMining";
            Queue<object?> calls = new([
                PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
                PilotServicesTests.Request(service, "GetStatus", []),
            ]);
            if (filter is not null)
            {
                calls.Enqueue(PilotServicesTests.Request(service, "SaveFilter", [filter]));
                calls.Enqueue(PilotServicesTests.Request(service, "GetStations", []));
                calls.Enqueue(PilotServicesTests.Request(service, "SaveHauling", [stationId, 80L]));
                calls.Enqueue(
                    PilotServicesTests.Request(
                        service,
                        "SaveSettings",
                        [filter, (long)MiningFamily.Gas, (long)AutoMiningTargetOrder.Smallest, true, true, stationId, 80L]
                    )
                );
                calls.Enqueue(
                    PilotServicesTests.Request(
                        service,
                        "SaveSettings",
                        ["Scordite", (long)MiningFamily.Ice, (long)AutoMiningTargetOrder.Nearest, false, false, stationId + 999, 55L]
                    )
                );
                calls.Enqueue(PilotServicesTests.Request(service, "GetStatus", []));
                calls.Enqueue(PilotServicesTests.Request(service, "Stop", []));
            }
            List<Dictionary<string, object?>> result = [];
            bool listedStations = false;
            int rejectedSettings = 0;
            await Assert.ThrowsAsync<EndOfStreamException>(() =>
                session.RunAsync(
                    () => calls.TryDequeue(out var call) ? Task.FromResult(call) : throw new EndOfStreamException(),
                    packet =>
                    {
                        var message = Assert.IsType<BlueInstance>(packet);
                        if (message.Name.EndsWith(".CallRsp", StringComparison.Ordinal))
                        {
                            var body = BlueRead.Tuple(BlueRead.Tuple(message.State, 14)[4], 1);
                            var value = BlueMarshal.Decode(BlueRead.Bytes(body[0]));
                            if (value is Dictionary<string, object?> status && status.ContainsKey("running"))
                            {
                                result.Add(status);
                            }
                            else if (value is List<object?> stations)
                            {
                                Assert.Contains(stations, entry => Equals(BlueRead.Tuple(entry, 2)[0], stationId));
                                listedStations = true;
                            }
                        }
                        if (message.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal))
                        {
                            rejectedSettings++;
                        }
                        return Task.CompletedTask;
                    },
                    _ => { },
                    1000001,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
            );
            Assert.Equal(filter is not null, listedStations);
            Assert.Equal(filter is null ? 0 : 1, rejectedSettings);
            return result;
        }
    }
}
