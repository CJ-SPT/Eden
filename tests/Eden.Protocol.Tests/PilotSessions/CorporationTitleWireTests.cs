using System.Diagnostics;
using System.Text.Json;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationTitleWireTests
{
    [Fact]
    public void NewStyleRowsRejectUnknownClassesAndMalformedState()
    {
        BlueNewObject row = new(
            BlueNewObject.RowClass,
            new object?[]
            {
                new List<object?> { "titleID" },
                new List<object?> { 1L },
            }
        );
        Assert.Equal(BlueMarshal.Encode(row), BlueMarshal.Encode(BlueMarshal.Decode(BlueMarshal.Encode(row))));
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(row with { Name = "untrusted.Row" })));
        object?[] malformed =
        [
            null,
            new Dictionary<string, object?>(),
            new object?[]
            {
                new List<object?> { "titleID" },
                new List<object?>(),
            },
            new object?[]
            {
                new List<object?> { 1L },
                new List<object?> { 1L },
            },
            new object?[]
            {
                new object?[] { "titleID" },
                new List<object?> { 1L },
            },
        ];
        foreach (var state in malformed)
        {
            Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(row with { State = state })));
        }
    }

    [PinnedPortraitClientFact]
    public async Task PinnedRowRejectsLegacyAllocationAndSupportsNativeTitleInitialization()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-corp-rows-" + Guid.NewGuid().ToString("N"));
        try
        {
            var titles = Enumerable
                .Range(0, CorporationRoleRules.TitleCount)
                .Select(index => new CorporationTitle(1L << index, index == 0 ? "Explorers 星" : "", new(Roles: 1L << 40)))
                .ToArray();
            var decoded = Assert.IsType<Dictionary<long, object?>>(
                BlueMarshal.Decode(BlueMarshal.Encode(new CorporationTitlesReply(titles)))
            );
            var rows = decoded.Values.Select(value =>
            {
                var row = Assert.IsType<BlueNewObject>(value);
                var state = BlueRead.Tuple(row.State, 2);
                var header = Assert.IsType<List<object?>>(state[0]).Select(BlueMarshal.Text).ToArray();
                var line = Assert.IsType<List<object?>>(state[1]).ToArray();
                int name = Array.IndexOf(header, "titleName");
                line[name] = BlueMarshal.Text(line[name]);
                return new NativeTitleRow(header, line);
            });
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "corporation-client-tests", "corporation_title_rows_test.py"));
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Native corporation title assertions passed", await output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed record NativeTitleRow(string[] Header, object?[] Line);

    [Fact]
    public void RegistryTitlesUseNewStyleRowsWithMutableHeaderAndLineLists()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        Assert.Null(ceo.Reply("UpdateTitle", [1L, "Explorers 星", (long)CorporationRole.Accountant, 0L, 0L, 0L, 0L, 0L, 0L, 0L]));
        var titles = Assert.IsType<Dictionary<long, object?>>(ceo.Reply("GetTitles", []));
        Assert.Equal(CorporationRoleRules.TitleCount, titles.Count);
        foreach (var (id, value) in titles)
        {
            var row = Assert.IsType<BlueNewObject>(value);
            Assert.Equal("carbon.common.script.sys.row.Row", row.Name);
            Assert.Equal(35, BlueMarshal.Encode(row)[5]);
            var state = BlueRead.Tuple(row.State, 2);
            var header = Assert.IsType<List<object?>>(state[0]).Select(BlueMarshal.Text).ToArray();
            var line = Assert.IsType<List<object?>>(state[1]);
            Assert.Equal(header.Length, line.Count);
            Assert.Equal(id, line[Array.IndexOf(header, "titleID")]);
            Assert.Equal(id == 1 ? "Explorers 星" : "", BlueMarshal.Text(line[Array.IndexOf(header, "titleName")]));
            Assert.Equal(id == 1 ? (long)CorporationRole.Accountant : 0L, line[Array.IndexOf(header, "roles")]);
        }
    }
}
