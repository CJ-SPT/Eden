using Eden.Gameplay.Combat;

namespace Eden.Protocol.Tests.Hosting;

public sealed class GameplayDependenciesTests
{
    [Fact]
    public void GameplayReferencesOnlyRuntimeAssemblies()
    {
        var references = typeof(ShipHealth).Assembly.GetReferencedAssemblies();
        Assert.All(references, reference => Assert.StartsWith("System.", reference.Name));
    }
}
