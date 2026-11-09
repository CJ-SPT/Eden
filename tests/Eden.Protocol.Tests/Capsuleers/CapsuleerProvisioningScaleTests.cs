using Eden.Data.World.Capsuleers;
using Eden.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerProvisioningScaleTests
{
    [Fact]
    public void CatalogSizeDoesNotProvisionUnvisitedCommunities()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        CapsuleerProvisioningTests.AddDestinationOre(f.Database);
        const int unvisitedSystems = 30;
        for (int i = 0; i < unvisitedSystems; i++)
        {
            CapsuleerProvisioningTests.AddSystem(f.Database, 31000000 + i);
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext world = new(f.Database.Path);
        Assert.Empty(world.Set<SavedCapsuleerAgent>());
        Assert.Empty(world.Set<SavedCapsuleerJob>());
        var provisioner = new CapsuleerProvisioner(f.Database, f.Clock);
        Assert.Equal(0, provisioner.Reconcile().CreatedCommunities);
        Assert.Equal(1, provisioner.EnsureSystem(30000141).CreatedCommunities);
        Assert.Equal(2, world.Set<SavedCapsuleerAgent>().Count());
        Assert.Single(world.Set<SavedCapsuleerSystem>());
    }
}
