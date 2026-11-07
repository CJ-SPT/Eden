using System.Reflection;
using System.Xml.Linq;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Serialization;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Hosting;

public sealed class GameplayDependenciesTests
{
    [Fact]
    public void GameplayReferencesOnlyRuntimeAssemblies()
    {
        var references = typeof(ShipHealth).Assembly.GetReferencedAssemblies();
        Assert.All(references, reference => Assert.StartsWith("System.", reference.Name));
    }

    [Fact]
    public void InfrastructureReferencesOnlyRuntimeAssemblies()
    {
        var references = typeof(InjectableAttribute).Assembly.GetReferencedAssemblies();
        Assert.All(references, reference => Assert.StartsWith("System.", reference.Name));
    }

    [Fact]
    public void WorldAndProtocolReferenceOnlyTheirSharedBoundaries()
    {
        AssertAdapter(typeof(WorldDatabase).Assembly);
        AssertAdapter(typeof(BlueModel).Assembly);
    }

    [Theory]
    [InlineData("Eden.Gameplay", "")]
    [InlineData("Eden.Infrastructure", "")]
    [InlineData("Eden.World", "Eden.Gameplay,Eden.Infrastructure")]
    [InlineData("Eden.Protocol", "Eden.Gameplay,Eden.Infrastructure")]
    [InlineData("Eden.Server", "Eden.Gameplay,Eden.Infrastructure,Eden.Protocol,Eden.World")]
    public void ProjectReferencesFollowTheDependencyGraphEvenWhenUnused(string project, string allowed)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Eden.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src", project, project + ".csproj"));
        var references = document
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
            .Order()
            .ToArray();
        Assert.Equal(allowed.Split(',', StringSplitOptions.RemoveEmptyEntries), references);
        if (allowed.Length == 0)
        {
            Assert.Empty(document.Descendants("PackageReference"));
        }
    }

    private static void AssertAdapter(Assembly assembly)
    {
        var references = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("Eden.", StringComparison.Ordinal))
            .Order()
            .ToArray();
        Assert.Equal(["Eden.Gameplay", "Eden.Infrastructure"], references);
    }
}
