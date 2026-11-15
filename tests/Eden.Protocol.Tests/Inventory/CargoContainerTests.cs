using System.Buffers.Binary;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Inventory;

public sealed partial class CargoContainerAssemblyTests
{
    [Theory]
    [InlineData(StationContainer)]
    [InlineData(SmallStandardContainer)]
    [InlineData(3465)]
    [InlineData(33009)]
    public void NativePreOpenValidationUsesResidentIdentityAcrossContainerGroups(int type)
    {
        using Fixture f = new();
        long id = f.Assembled(type);
        var descriptor = Assert.IsType<BlueRowDescriptor>(BlueMarshal.Decode(BlueMarshal.Encode(InventoryWire.Descriptor())));
        byte[] identity = new byte[InventoryWire.PackedSize];
        BinaryPrimitives.WriteInt64LittleEndian(identity, id);
        // All other client-supplied fields are false. The saved item is the authority.
        var row = new BluePackedRow(descriptor, [0, .. identity[..16], 0, .. identity[16..32], 0, .. identity[32..]], [""]);
        Assert.Equal(true, Success(f.Service("ValidateItemCanBeOpened", [row])).ToBlue());
        Assert.Equal(true, Success(f.Service("ValidateItemListCanBeOpened", [new List<object?> { row }])).ToBlue());
        string service = f.Open(id);
        Assert.Empty(Assert.IsType<BlueList<InventoryRow>>(Success(f.Service("List", [], service))).Items);
        Assert.Same(PilotCallResult.None, f.Service("SetLabel", [id, "Organized"]));
        if (type == SmallStandardContainer || type == 33009)
        {
            Assert.IsType<PilotCallResult.Failure>(f.Service("SetPassword", [1L, null, "secret"], service));
            Assert.IsType<PilotCallResult.Failure>(f.Service("ALSCConfigSet", [0L], service));
        }
        else
        {
            Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "secret"], service));
        }
        if (type == 3465)
        {
            using var other = f.AnotherContext();
            string binding = f.Open(id, other);
            Assert.Equal(
                "SCGeneralPasswordRequired",
                Assert.IsType<PilotCallResult.NamedFailure>(f.Service("List", [], binding, context: other)).Name
            );
        }
    }

    [Fact]
    public void NativeContainerCallsRenameOpenViewContentsAndPersistBothPasswords()
    {
        using Fixture f = new();
        long id = f.Assembled();
        long content = f.Add(InventoryDefinitions.CivilianMinerType, 2, id, flag: InventoryFlag.Unlocked);
        var bound = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    f.Service(
                        "MachoBindObject",
                        [
                            new object?[] { (long)ContractAssetTestData.Station, 15L },
                            new object?[] { "GetInventoryFromId", new object?[] { id, 0L } },
                        ]
                    )
                )
                .Reply
        );
        var reference = Assert.IsType<BlueObjectReference>(bound.Reply.ToBlue());
        string service = f.Context.ResolveReference(reference.Id)!;
        Assert.Equal($"{CargoContainerHandler.InventoryPrefix}{id}", service);
        Assert.Same(PilotCallResult.None, f.Service("SetLabel", [id, "  Mining supplies  "]));
        Assert.Equal("Mining supplies", Assert.Single(f.Context.Notifications.OfType<ShipLabelChanged>()).Label);
        Assert.Equal(
            "Mining supplies",
            Assert.Single(f.Db.Services.GetRequiredService<IPilotAssets>().StationItemNames(Owner, new HashSet<long> { id })).Name
        );
        Assert.Equal(id, Assert.IsType<InventoryRow>(Success(f.Service("GetSelfInvItem", [], service))).Id);
        var listed = Assert.Single(Assert.IsType<BlueList<InventoryRow>>(Success(f.Service("List", [], service))).Items);
        Assert.Equal(content, listed.Id);
        Assert.Equal(2, listed.Quantity);
        Assert.NotEmpty(BlueMarshal.Encode(Success(f.Service("GetContainerContents", [id, (long)ContractAssetTestData.Station]))));
        Assert.Equal(
            content,
            Assert
                .Single(
                    Assert
                        .IsType<BlueList<InventoryRow>>(
                            Success(f.Service("GetContainerContents", [id, (long)ContractAssetTestData.Station]))
                        )
                        .Items
                )
                .Id
        );
        var capacity = Assert.IsType<CargoContainerCapacity>(Success(f.Service("GetCapacity", [], service)));
        Assert.Equal(1_000_000, capacity.Capacity);
        Assert.True(capacity.Used > 0);
        Assert.False(Assert.IsType<bool>(Success(f.Service("HasExistingPasswordSet", [1L], service)).ToBlue()));
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "general"], service));
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [2L, null, "config"], service));
        Assert.True(Assert.IsType<bool>(Success(f.Service("HasExistingPasswordSet", [2L], service)).ToBlue()));
        Assert.Same(PilotCallResult.None, f.Service("ALSCConfigSet", [(long)CargoContainerConfiguration.PasswordToOpen], service));
        var retrieved = Assert.IsType<PilotCallResult.Failure>(f.Service("RetrievePassword", [1L], service));
        Assert.True(retrieved.ShowInformationDialog);
        Assert.Contains("general", retrieved.Message);
        Assert.Equal((int)CargoContainerConfiguration.PasswordToOpen, Success(f.Service("ALSCConfigGet", [], service)).ToBlue());

        f.Context.Dispose();
        f.Db.Services.Dispose();
        using var reopened = ServerServices.Build(new(f.Db.Path), liveWorld: true);
        var store = reopened.GetRequiredService<ICargoContainers>();
        Assert.Equal("general", store.RetrievePassword(Owner, id, CargoContainerPasswordType.General));
        Assert.Equal("config", store.RetrievePassword(Owner, id, CargoContainerPasswordType.Configuration));
        Assert.Throws<CargoContainerPasswordRequiredException>(() => store.Read(Owner, id, new()));
        Assert.Equal(content, Assert.Single(store.Read(Owner, id, new("general", "config")).Items).Id);
        Assert.Equal(
            "Mining supplies",
            Assert.Single(reopened.GetRequiredService<IPilotAssets>().StationItemNames(Owner, new HashSet<long> { id })).Name
        );
    }

    [Fact]
    public void PasswordPromptsAreSessionSpecificAndRotationRevokesPreviousAuthorization()
    {
        using Fixture f = new();
        long id = f.Assembled();
        string service = f.Open(id);
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "general"], service));
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [2L, null, "config"], service));
        Assert.Same(PilotCallResult.None, f.Service("ALSCConfigSet", [(long)CargoContainerConfiguration.PasswordToOpen], service));
        using var second = f.AnotherContext();
        string other = f.Open(id, second);
        Assert.Equal(
            "SCGeneralPasswordRequired",
            Assert.IsType<PilotCallResult.NamedFailure>(f.Service("List", [], other, context: second)).Name
        );
        Assert.Equal(
            "SCConfigPasswordRequired",
            Assert.IsType<PilotCallResult.NamedFailure>(f.Service("ALSCConfigGet", [], other, context: second)).Name
        );
        Assert.Equal(
            "SCGeneralPasswordRequired",
            Assert
                .IsType<PilotCallResult.NamedFailure>(
                    f.Service("GetContainerContents", [id, (long)ContractAssetTestData.Station], context: second)
                )
                .Name
        );
        Assert.IsType<PilotCallResult.Failure>(f.Service("EnterPassword", [1L, "wrong"], other, context: second));
        Assert.IsType<PilotCallResult.Failure>(f.Service("SetPassword", [1L, "wrong", "changed"], other, context: second));
        Assert.Same(PilotCallResult.None, f.Service("EnterPassword", [1L, "general"], other, context: second));
        Assert.IsType<PilotCallResult.Success>(f.Service("List", [], other, context: second));
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, "general", "changed"], service));
        Assert.Equal(
            "SCGeneralPasswordRequired",
            Assert.IsType<PilotCallResult.NamedFailure>(f.Service("List", [], other, context: second)).Name
        );
        Assert.Contains("changed", Assert.IsType<PilotCallResult.Failure>(f.Service("RetrievePassword", [1L], service)).Message);
        Assert.Contains("config", Assert.IsType<PilotCallResult.Failure>(f.Service("RetrievePassword", [2L], service)).Message);
    }

    [Fact]
    public void ContainerTransfersConserveQuantitiesAndRejectCapacityAndLockedWithdrawalAtomically()
    {
        using Fixture f = new();
        long id = f.Assembled();
        string service = f.Open(id);
        long stack = f.Add(InventoryDefinitions.CivilianMinerType, 5);
        Assert.Same(PilotCallResult.None, f.Service("Add", [stack, (long)ContractAssetTestData.Station], service, new() { ["qty"] = 2L }));
        var inside = Assert.Single(f.Items(id, InventoryDefinitions.CivilianMinerType));
        Assert.Equal(2, inside.Quantity);
        Assert.Equal((int)InventoryFlag.Unlocked, inside.FlagId);
        Assert.Equal(
            3,
            Assert.Single(f.Items(ContractAssetTestData.Station, InventoryDefinitions.CivilianMinerType), item => item.Id == stack).Quantity
        );
        Assert.Equal(2, f.Context.Notifications.OfType<ItemChanged>().Count());
        Assert.Same(PilotCallResult.None, f.Service("ALSCLockItems", [new List<object?> { inside.Id }], service));
        Assert.IsType<PilotCallResult.Failure>(f.Service("Add", [inside.Id, id], "@hangar"));
        Assert.Equal((int)InventoryFlag.Locked, Assert.Single(f.Items(id, InventoryDefinitions.CivilianMinerType)).FlagId);
        Assert.Same(PilotCallResult.None, f.Service("ALSCUnlockItems", [new List<object?> { inside.Id }], service));
        Assert.Same(PilotCallResult.None, f.Service("Add", [inside.Id, id], "@hangar"));
        Assert.Empty(f.Items(id, InventoryDefinitions.CivilianMinerType));

        long small = f.Assembled(SmallStandardContainer);
        string smallService = f.Open(small);
        long huge = f.Add(StationContainer, 1);
        var before = f.Items(ContractAssetTestData.Station, StationContainer);
        int notifications = f.Context.Notifications.Count;
        var failure = Assert.IsType<PilotCallResult.NamedFailure>(
            f.Service("MultiAdd", [new List<object?> { stack, huge }, (long)ContractAssetTestData.Station], smallService)
        );
        Assert.Equal("NotEnoughCargoSpace", failure.Name);
        Assert.Equal(before, f.Items(ContractAssetTestData.Station, StationContainer));
        Assert.Empty(f.Items(small, InventoryDefinitions.CivilianMinerType));
        Assert.Equal(notifications, f.Context.Notifications.Count);
        Assert.IsType<PilotCallResult.Failure>(f.Service("Add", [id, (long)ContractAssetTestData.Station], smallService));
    }

    [Fact]
    public void ContainerCallsRejectForeignRemotePackagedNestedAndMovedBindings()
    {
        using Fixture f = new();
        long id = f.Assembled();
        string service = f.Open(id);
        foreach (
            var definition in new[]
            {
                (Owner, (long)ContractAssetTestData.Station, false),
                (ContractAssetTestData.Other, (long)ContractAssetTestData.Station, true),
                (Owner, (long)ContractAssetTestData.RemoteStation, true),
                (Owner, id, true),
            }
        )
        {
            long rejected = f.Add(StationContainer, 1, definition.Item2, definition.Item1);
            using (WorldDbContext saved = new(f.Db.Path))
            {
                saved.Set<OwnedItem>().Find(rejected)!.Singleton = definition.Item3;
                saved.SaveChanges();
            }
            Assert.IsType<PilotCallResult.Failure>(f.Service("GetInventoryFromId", [rejected]));
            Assert.IsType<PilotCallResult.Failure>(f.Service("SetLabel", [rejected, "Hidden"]));
            Assert.IsType<PilotCallResult.Failure>(f.Service("GetContainerContents", [rejected, (long)ContractAssetTestData.Station]));
        }
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Set<OwnedItem>().Find(id)!.LocationId = ContractAssetTestData.RemoteStation;
            saved.SaveChanges();
        }
        Assert.IsType<PilotCallResult.Failure>(f.Service("List", [], service));
        Assert.IsType<PilotCallResult.Failure>(f.Service("SetPassword", [1L, null, "secret"], service));
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void RemotePersonalContentPreviewsValidateTheRealStationWithoutGrantingWritableAccess()
    {
        using Fixture f = new();
        ContractAssetTestData.AddRemoteStation(f.Db);
        long id = f.Add(StationContainer, 1, ContractAssetTestData.RemoteStation);
        using (var action = f.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin())
        {
            action.Set<OwnedItem>().Find(id)!.Singleton = true;
            action.Commit();
        }
        long child = f.Add(InventoryDefinitions.CivilianMinerType, 2, id, flag: InventoryFlag.Unlocked);
        Assert.Equal(
            child,
            Assert
                .Single(
                    Assert
                        .IsType<BlueList<InventoryRow>>(
                            Success(f.Service("GetContainerContents", [id, (long)ContractAssetTestData.RemoteStation]))
                        )
                        .Items
                )
                .Id
        );
        Assert.IsType<PilotCallResult.Failure>(f.Service("GetContainerContents", [id, (long)ContractAssetTestData.Station]));
        Assert.IsType<PilotCallResult.Failure>(f.Service("GetInventoryFromId", [id]));
        Assert.IsType<PilotCallResult.Failure>(f.Service("SetLabel", [id, "Remote change"]));
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopq")]
    [InlineData("bad\npassword")]
    public void InvalidPasswordsLeaveTheExistingPasswordUnchanged(string password)
    {
        using Fixture f = new();
        long id = f.Assembled();
        string service = f.Open(id);
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "original"], service));
        Assert.IsType<PilotCallResult.Failure>(f.Service("SetPassword", [1L, "original", password], service));
        Assert.Equal(
            "original",
            f.Db.Services.GetRequiredService<ICargoContainers>().RetrievePassword(Owner, id, CargoContainerPasswordType.General)
        );
    }

    [Fact]
    public void ResidentSettingsSurviveFailedCheckpointProjectionAndRetryWithoutGameplaySql()
    {
        using Fixture f = new();
        long id = f.Assembled();
        string service = f.Open(id);
        var store = f.Db.Services.GetRequiredService<ICargoContainers>();
        store.Read(Owner, id, new());
        List<string> commands = [];
        using (SessionPerformance.Begin("resident container operations", _ => { }, force: true, command: commands.Add))
        {
            Assert.Same(PilotCallResult.None, f.Service("SetLabel", [id, "Checkpoint supplies"]));
            Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "general"], service));
            Assert.Same(PilotCallResult.None, f.Service("SetPassword", [2L, null, "config"], service));
            Assert.Same(PilotCallResult.None, f.Service("ALSCConfigSet", [(long)CargoContainerConfiguration.LockAddedItems], service));
            Assert.Empty(Assert.IsType<BlueList<InventoryRow>>(Success(f.Service("List", [], service))).Items);
            Assert.Equal("general", store.RetrievePassword(Owner, id, CargoContainerPasswordType.General));
        }
        Assert.DoesNotContain(commands, sql => sql.Contains("World", StringComparison.Ordinal));
        var live = f.Db.Services.GetRequiredService<LiveWorld>();
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            using var command = snapshot.Database.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER RejectContainer BEFORE INSERT ON WorldCargoContainer
                BEGIN SELECT RAISE(ABORT,'test container checkpoint'); END
                """;
            command.ExecuteNonQuery();
            Assert.Throws<SqliteException>(() => snapshot.Project());
        }
        Assert.Equal("config", store.RetrievePassword(Owner, id, CargoContainerPasswordType.Configuration));
        Assert.Equal("Checkpoint supplies", Assert.Single(store.Names(Owner, new HashSet<long> { id })).Name);
        using (var retry = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            retry.Project();
            using WorldDbContext saved = new(retry.Database);
            var settings = saved.Set<SavedCargoContainer>().Find(id)!;
            Assert.Equal("Checkpoint supplies", settings.Name);
            Assert.Equal("general", settings.GeneralPassword);
            Assert.Equal("config", settings.ConfigurationPassword);
            Assert.Equal(CargoContainerConfiguration.LockAddedItems, settings.Configuration);
        }
    }

    [Fact]
    public void ANewPilotSelectionClearsPreviouslyEnteredPasswords()
    {
        using Fixture f = new();
        long id = f.Assembled();
        string service = f.Open(id);
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "general"], service));
        Assert.Same(PilotCallResult.None, f.Service("ALSCConfigSet", [(long)CargoContainerConfiguration.PasswordToOpen], service));
        Assert.IsType<PilotCallResult.Success>(f.Service("List", [], service));
        var pilot = f.Context.Pilot;
        f.Context.LogOff();
        f.Context.Select(pilot);
        string binding = f.Open(id);
        Assert.Equal("SCGeneralPasswordRequired", Assert.IsType<PilotCallResult.NamedFailure>(f.Service("List", [], binding)).Name);
    }

    [Fact]
    public void Version164UpgradePreservesContainersAndCreatesCheckpointSettings()
    {
        using Fixture f = new();
        long id = f.Assembled();
        f.Context.Dispose();
        f.Db.Services.Dispose();
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Database.ExecuteSqlRaw("DROP TABLE WorldCargoContainer; PRAGMA user_version=164;");
        }
        WorldInitialization.Initialize(new(f.Db.Path));
        using var reopened = ServerServices.Build(new(f.Db.Path), liveWorld: true);
        var containers = reopened.GetRequiredService<ICargoContainers>();
        Assert.Equal(id, containers.Read(Owner, id, new()).Item.Id);
        Assert.False(containers.HasPassword(Owner, id, CargoContainerPasswordType.General));
        Assert.Equal("Preserved container", containers.Rename(Owner, id, "Preserved container"));
        Assert.Equal("Preserved container", Assert.Single(containers.Names(Owner, new HashSet<long> { id })).Name);
    }

    [Fact]
    public async Task RemovingAnItemAlsoRemovesItsResidentSettingsAndCheckpointRow()
    {
        using Fixture f = new();
        long id = f.Assembled();
        string service = f.Open(id);
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "secret"], service));
        var live = f.Db.Services.GetRequiredService<LiveWorld>();
        await live.FlushAsync();
        using (var action = live.Economy.Begin())
        {
            action.Remove(action.Set<OwnedItem>().Find(id)!);
            action.Commit();
        }
        Assert.Null(live.Economy.Inspect(action => action.Set<SavedCargoContainer>().Find(id)));
        await live.FlushAsync();
        f.Context.Dispose();
        f.Db.Services.Dispose();
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<SavedCargoContainer>().Find(id));
        Assert.Null(saved.Set<OwnedItem>().Find(id));
        Assert.Equal(0, saved.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Fact]
    public void ContainerNameAndConfigurationRulesRejectInvalidValues()
    {
        Assert.Equal("Default", CargoContainerRules.Name(" ", "Default"));
        Assert.Equal("Supplies", CargoContainerRules.Name(" Supplies ", "Default"));
        Assert.Throws<InvalidDataException>(() =>
            CargoContainerRules.Name(new string('a', CargoContainerRules.MaximumNameLength + 1), "Default")
        );
        Assert.Throws<InvalidDataException>(() => CargoContainerRules.Name("bad\nname", "Default"));
        Assert.Throws<InvalidDataException>(() => CargoContainerRules.CheckConfiguration((CargoContainerConfiguration)int.MaxValue));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopq")]
    public void PureContainerRulesRejectPasswordBoundaries(string password) =>
        Assert.Throws<InvalidDataException>(() => CargoContainerRules.CheckPassword(password));

    private static BlueModel Success(PilotCallResult result) => Assert.IsType<PilotCallResult.Success>(result).Reply;
}
