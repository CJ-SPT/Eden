using Eden.Server.Configuration;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ServerDataPathsTests
{
    [Fact]
    public void SourceAndPublishedServersUseTheirOwnDataDirectory()
    {
        var working = Directory.CreateTempSubdirectory("eden-paths-");
        try
        {
            string published = Path.Combine(working.FullName, "publish");
            Assert.Equal(
                Path.Combine(published, "data", "world.sqlite"),
                ServerDataPaths.WorldPath(null, null, working.FullName, published)
            );

            File.WriteAllText(Path.Combine(working.FullName, "Eden.Server.csproj"), "<Project />");
            Assert.Equal(
                Path.Combine(working.FullName, "data", "world.sqlite"),
                ServerDataPaths.WorldPath(null, null, working.FullName, published)
            );
        }
        finally
        {
            working.Delete(recursive: true);
        }
    }

    [Fact]
    public void WorldOverrideTakesPrecedenceOverDataDirectory()
    {
        string working = Path.GetTempPath();
        Assert.Equal(Path.Combine(working, "mounted", "world.sqlite"), ServerDataPaths.WorldPath(null, "mounted", working, working));
        Assert.Equal(Path.Combine(working, "custom.sqlite"), ServerDataPaths.WorldPath("custom.sqlite", "mounted", working, working));
    }
}
