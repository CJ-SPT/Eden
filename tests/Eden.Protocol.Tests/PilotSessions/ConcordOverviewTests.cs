using System.Diagnostics;
using System.Text;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ConcordOverviewTests
{
    [Fact]
    public void SignedLoginExpressionIncludesPortraitsAndConcordColorWithAnEmptyChallengeResult()
    {
        byte[] data = BlueRead.Bytes(BlueMarshal.Decode(LoginClient.Expression));
        using BinaryReader reader = new(new MemoryStream(data), Encoding.ASCII);
        Assert.Equal((byte)'s', reader.ReadByte());
        int length = reader.ReadInt32();
        string expression = Encoding.ASCII.GetString(reader.ReadBytes(length));
        Assert.Equal(data.Length, reader.BaseStream.Position);
        Assert.StartsWith("eval(compile(u", expression);
        Assert.EndsWith(", 'eden-login', 'exec'), {}) or {}", expression);
        Assert.Contains("EvePhoto.GetPortrait = get_portrait", expression);
        Assert.Contains("install_concord_overview()", expression);
        Assert.Contains("install_npc_bounty_display()", expression);
        Assert.Contains("install_presence_lists()", expression);
        Assert.Contains("install_free_point_preview()", expression);
    }

    [PinnedPortraitClientFact]
    public async Task NativeNpcOverviewKeepsConcordBlueAndUsesAdvertisedNpcBounties()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-concord-expression-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(LoginClient.Expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "portrait-client-tests", "concord_overview_client_test.py"));
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("CONCORD overview adapter assertions passed", await output);
            Assert.Contains("NPC bounty display assertions passed", await output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
