using System.Text;
using Eden.Data.Characters;
using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PilotNotesServiceTests
{
    [Fact]
    public void NativeNotesReadAndCloseSaveFlowIsPrivateAndSurvivesServiceReopen()
    {
        using TestWorld db = new();
        using var owner = new Session(db, 1000001);
        using var other = new Session(db, 1000002);
        Assert.Same(PilotCallResult.Unsupported, owner.Call("GetNote", [90000002L]));
        Assert.Same(PilotCallResult.Unsupported, owner.Call("SetNote", [90000002L, "Before selection"]));
        owner.Select();
        other.Select();
        const string text = "<b>星間旅行</b><br>Private — пилот 🚀";
        Assert.Equal("", Reply(owner.Call("GetNote", [90000002L])));
        var result = Assert.IsType<PilotCallResult.Success>(owner.Call("GetNote", [90000002L]));
        Assert.IsType<BlueUnicode>(result.Reply.ToBlue());
        Assert.Null(Reply(owner.Call("SetNote", [90000002L, new BlueUnicode(text)])));
        Assert.Equal(text, Reply(owner.Call("GetNote", [90000002L])));
        Assert.Equal("", Reply(other.Call("GetNote", [90000002L])));
        Assert.Null(Reply(other.Call("SetNote", [90000002L, "My own note"])));
        Assert.Equal(text, Reply(owner.Call("GetNote", [90000002L])));
        Assert.Equal("My own note", Reply(other.Call("GetNote", [90000002L])));
        Assert.Equal("", Reply(owner.Call("GetCharacterDescription", [90000002L])));

        using var reopened = ServerServices.Build(new(db.Path));
        using var scope = reopened.CreateScope();
        Assert.Equal(
            text,
            Reply(
                scope
                    .ServiceProvider.GetRequiredService<PilotCallRouter>()
                    .Call(owner.Context, "charMgr", "GetNote", new object?[] { 90000002L }, _ => { })
            )
        );
        Assert.Null(Reply(owner.Call("SetNote", [90000002L, Encoding.UTF8.GetBytes("UTF-8 星間")])));
        Assert.Equal("UTF-8 星間", Reply(owner.Call("GetNote", [90000002L])));
        Assert.Null(Reply(owner.Call("SetNote", [90000002L, ""])));
        Assert.Equal("", Reply(owner.Call("GetNote", [90000002L])));
        Assert.Equal("My own note", Reply(other.Call("GetNote", [90000002L])));
        Assert.Empty(owner.Context.Notifications);
        Assert.Empty(other.Context.Notifications);
    }

    [Fact]
    public void NotesFollowTheSelectedPilotWhenCharactersShareAnAccount()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(90000002L)!.AccountId = 1000001;
            world.SaveChanges();
        }
        using var session = new Session(db, 1000001);
        session.Select();
        Assert.Null(Reply(session.Call("SetNote", [90000002L, "First pilot's private note"])));
        session.Context.LogOff();
        session.Context.Select(db.Store.FindPilot(1000001, 90000002)!);
        Assert.Equal("", Reply(session.Call("GetNote", [90000002L])));
        Assert.Null(Reply(session.Call("SetNote", [90000002L, "Second pilot's private note"])));
        session.Context.LogOff();
        session.Select();
        Assert.Equal("First pilot's private note", Reply(session.Call("GetNote", [90000002L])));
        Assert.Equal("Second pilot's private note", db.Services.GetRequiredService<IPilotNotes>().Read(90000002, 90000002));
        Assert.Empty(session.Context.Notifications);
    }

    [Fact]
    public void NotesRejectForgedOwnersMalformedArgumentsAndInvalidTextWithoutChangingPrivateState()
    {
        using TestWorld db = new();
        using var owner = new Session(db, 1000001);
        owner.Select();
        Assert.Null(Reply(owner.Call("SetNote", [90000002L, "Keep me"])));
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [90000002L],
                ["90000002", "wrong ID"],
                [90000002L, null],
                [90000002L, 7L],
                [90000002L, "forged", 90000002L],
                [0L, "invalid"],
                [long.MaxValue, "unknown"],
                [90000002L, new string('x', PilotNoteRules.MaximumLength + 1)],
                [90000002L, "bad\0text"],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(owner.Call("SetNote", args));
        }
        foreach (object?[] args in new object?[][] { [], ["90000002"], [90000002L, 90000002L], [0L], [-1L], [123L] })
        {
            Assert.IsType<PilotCallResult.Failure>(owner.Call("GetNote", args));
        }
        Assert.IsType<PilotCallResult.Failure>(owner.Call("GetNote", [90000002L], new() { ["owner"] = 90000002L }));
        Assert.IsType<PilotCallResult.Failure>(owner.Call("SetNote", [90000002L, "Forged"], new() { ["owner"] = 90000002L }));
        Assert.Equal("Keep me", Reply(owner.Call("GetNote", [90000002L], new() { ["machoVersion"] = 1L })));
        Assert.Equal("", db.Services.GetRequiredService<IPilotNotes>().Read(90000002, 90000002));
        using WorldDbContext world = new(db.Path);
        Assert.Single(world.Set<SavedPilotNote>());
        Assert.Empty(owner.Context.Notifications);
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Session : IDisposable
    {
        private readonly TestWorld _db;
        private readonly long _accountId;
        private readonly IServiceScope _scope;
        public PilotCallContext Context { get; }

        public Session(TestWorld db, long accountId)
        {
            _db = db;
            _accountId = accountId;
            _scope = db.Services.CreateScope();
            var pilot = db.Store.FindPilot(accountId)!;
            Context = new(
                accountId,
                pilot.Account,
                [pilot],
                p => new(db.Store, p, new TestClock(), new(), db.Objects),
                accountId,
                SignedSettings.Empty
            );
        }

        public void Select() => Context.Select(_db.Store.FindPilot(_accountId)!);

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, "charMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
