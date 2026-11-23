using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Agents;

public sealed class EpicArcDirectoryTests
{
    [Fact]
    public void AgentManagerSuppliesCachedCatalogMessagesWithoutAcceptingCharacterArguments()
    {
        const string Mission = "14117";
        const string ChapterTitle = "messages.epicMission.journalText.chapterTitle";
        const string Record = """
            {
              "messages": {
                "messages.epicMission.journalText.chapterTitle": 140784,
                "messages.mission.briefing": 140982
              }
            }
            """;
        using MissionFixture fixture = new(configure: catalog =>
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/missions', {Mission}, {Record})")
        );
        var reply = MissionFixture.Reply(fixture.Call("agentMgr", "GetMessagesForEpicArcMissions", []));
        var cached = Assert.IsType<BlueInstance>(reply);
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", cached.Name);
        object?[] fields = BlueRead.Tuple(cached.State, 3);
        Assert.Equal("never", BlueMarshal.Text(BlueRead.Fields(fields[0])["versionCheck"]));
        var messages = BlueRead.Fields(BlueMarshal.Decode(BlueRead.Bytes(fields[1])));
        var chapter = Assert.IsType<Dictionary<long, object?>>(messages[ChapterTitle]);
        Assert.Equal(140784L, chapter[14117]);
        Assert.DoesNotContain("messages.mission.briefing", messages.Keys);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("agentMgr", "GetMessagesForEpicArcMissions", [MissionFixture.PilotId]));
    }
}
