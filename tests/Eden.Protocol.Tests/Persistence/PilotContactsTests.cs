using Eden.Gameplay.Characters;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotContactsTests
{
    private const long Pilot = 90000001;
    private const long Wing = 90000002;

    [Fact]
    public void ContactsBlocksAndLabelsSurviveRestartAndRemainIndependent()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        contacts.Add(Pilot, Wing, 5, true);
        contacts.Add(Wing, Pilot, -5, false);
        long friends = contacts.CreateLabel(Pilot, "星間旅行", 2);
        long miners = contacts.CreateLabel(Pilot, "Miners", null);
        Assert.Equal(1, friends);
        Assert.Equal(2, miners);
        contacts.SetLabels(Pilot, [Wing], friends | miners, true);
        contacts.SetBlocked(Pilot, [Wing, Wing], true);
        contacts.SetBlocked(Pilot, [Wing], true);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPilotContacts>();
        Assert.Equal(new PersonalContact(Wing, 5, true, 3), Assert.Single(saved.Read(Pilot).Addresses));
        Assert.Equal(Wing, Assert.Single(saved.Read(Pilot).Blocked));
        Assert.Equal(new PersonalContact(Pilot, -5, false, 0), Assert.Single(saved.Read(Wing).Addresses));
        Assert.Empty(saved.Read(Wing).Blocked);
        Assert.Empty(saved.Labels(Wing));
        saved.Edit(Pilot, Wing, 10, false);
        Assert.Equal(3, Assert.Single(saved.Read(Pilot).Addresses).LabelMask);
        saved.EditStanding(Pilot, [Wing, Wing], -10);
        saved.SetLabels(Pilot, [Wing], friends, false);
        Assert.Equal(new PersonalContact(Wing, -10, false, 2), Assert.Single(saved.Read(Pilot).Addresses));
        saved.EditLabel(Pilot, miners, "Fleet", 4);
        Assert.Equal(new ContactLabel(miners, "Fleet", 4), saved.Labels(Pilot)[1]);
        saved.DeleteLabel(Pilot, miners);
        Assert.Equal(0, Assert.Single(saved.Read(Pilot).Addresses).LabelMask);
        Assert.Equal(miners, saved.CreateLabel(Pilot, "Reused", null));
        Assert.Equal(0, Assert.Single(saved.Read(Pilot).Addresses).LabelMask);
        saved.SetBlocked(Pilot, [Wing], false);
        saved.SetBlocked(Pilot, [Wing], false);
        saved.Delete(Pilot, [Wing]);
        saved.Delete(Pilot, [Wing]);
        WorldInitialization.Open(db.Path);
        Assert.Empty(contacts.Read(Pilot).Addresses);
        Assert.Empty(contacts.Read(Pilot).Blocked);
        Assert.Single(contacts.Read(Wing).Addresses);
    }

    [Fact]
    public void InvalidBatchesAndForeignLabelsRollBackAllState()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        contacts.Add(Pilot, Wing, 5, false);
        long label = contacts.CreateLabel(Pilot, "Friends", 1);
        contacts.CreateLabel(Wing, "Their label", 1);
        foreach (double standing in new[] { -10.1, 10.1, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<InvalidDataException>(() => contacts.Edit(Pilot, Wing, standing, true));
        }
        foreach (long[] ids in new long[][] { [], [Pilot], [Wing, 123], new long[ContactRules.MaximumBatchSize + 1] })
        {
            Assert.Throws<InvalidDataException>(() => contacts.EditStanding(Pilot, ids, 0));
            Assert.Throws<InvalidDataException>(() => contacts.Delete(Pilot, ids));
            Assert.Throws<InvalidDataException>(() => contacts.SetBlocked(Pilot, ids, true));
            Assert.Throws<InvalidDataException>(() => contacts.SetLabels(Pilot, ids, label, true));
        }
        Assert.Throws<InvalidDataException>(() => contacts.Add(Pilot, Wing, 0, false));
        Assert.Throws<InvalidDataException>(() => contacts.Add(Pilot, Pilot, 0, false));
        Assert.Throws<InvalidDataException>(() => contacts.Edit(Wing, Pilot, 0, false));
        Assert.Throws<InvalidDataException>(() => contacts.SetLabels(Pilot, [Wing], label | 2, true));
        Assert.Throws<InvalidDataException>(() => contacts.DeleteLabel(Pilot, 2));
        Assert.Throws<InvalidDataException>(() => contacts.EditLabel(Pilot, 3, "Bad", 1));
        Assert.Throws<InvalidDataException>(() => contacts.CreateLabel(Pilot, "Friends", 2));
        Assert.Throws<InvalidDataException>(() => contacts.CreateLabel(Pilot, " ", 2));
        Assert.Throws<InvalidDataException>(() => contacts.CreateLabel(Pilot, "Invalid color", -1));
        Assert.Throws<InvalidDataException>(() => contacts.EditLabel(Pilot, label, "Changed", -1));
        Assert.Throws<InvalidDataException>(() => contacts.Read(123));
        Assert.Equal(new PersonalContact(Wing, 5, false, 0), Assert.Single(contacts.Read(Pilot).Addresses));
        Assert.Empty(contacts.Read(Pilot).Blocked);
        Assert.Equal(new ContactLabel(label, "Friends", 1), Assert.Single(contacts.Labels(Pilot)));
        Assert.Empty(contacts.Read(Wing).Addresses);
        Assert.Equal("Their label", Assert.Single(contacts.Labels(Wing)).Name);
    }

    [Fact]
    public void LabelLimitAndBit63BoundaryDoNotOverflowAndDeletedBitsAreReusable()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        contacts.Add(Pilot, Wing, 0, false);
        for (int bit = 0; bit < ContactRules.MaximumLabels; bit++)
        {
            Assert.Equal(1L << bit, contacts.CreateLabel(Pilot, $"Label {bit}", null));
        }
        Assert.Throws<InvalidDataException>(() => contacts.CreateLabel(Pilot, "Overflow", null));
        contacts.SetLabels(Pilot, [Wing], long.MaxValue, true);
        Assert.Equal(long.MaxValue, Assert.Single(contacts.Read(Pilot).Addresses).LabelMask);
        contacts.DeleteLabel(Pilot, 1L << 62);
        Assert.Equal(long.MaxValue ^ (1L << 62), Assert.Single(contacts.Read(Pilot).Addresses).LabelMask);
        Assert.Equal(1L << 62, contacts.CreateLabel(Pilot, "Replacement", null));
    }

    [Fact]
    public void Version40MigrationRetainsSettingsAndDirectoryResolvesOfflinePilots()
    {
        using TestWorld db = new();
        db.Services.GetRequiredService<IPilotCharacterSettings>().Save(Pilot, new("layout", new CharacterSettingValue.Text("keep")));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldContact; DROP TABLE WorldBlockedContact; DROP TABLE WorldContactLabel; PRAGMA user_version=40"
            );
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var contacts = reopened.GetRequiredService<IPilotContacts>();
        Assert.Empty(contacts.Read(Pilot).Addresses);
        Assert.Empty(contacts.Labels(Pilot));
        Assert.Equal(
            "keep",
            Assert
                .IsType<CharacterSettingValue.Text>(Assert.Single(reopened.GetRequiredService<IPilotCharacterSettings>().Read(Pilot)).Value)
                .Value
        );
        var owners = reopened.GetRequiredService<ICharacterDirectory>().Read([Wing, Pilot, Wing, 123]);
        Assert.Equal(new[] { Pilot, Wing }, owners.Select(p => p.Id));
        Assert.Equal("Eden Wing", owners[1].Name);
        contacts.Add(Pilot, Wing, 0, false);
        Assert.Single(contacts.Read(Pilot).Addresses);
    }
}
