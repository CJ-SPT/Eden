using System.Diagnostics;
using System.Text;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CharacterPortraitTests
{
    private const long Pilot = 90000001;
    private const long Other = 90000002;

    [Fact]
    public void PortraitBatchAndPublicProfileWorkBeforeSelectionAndAcrossAccountsAfterReopen()
    {
        using TestWorld db = new();
        var appearance = Avatar(
            new()
            {
                ["appearance"] = new PaperDollRow(Row("AppearanceRow"), [0.375d]),
                ["modifiers"] = new List<object?>
                {
                    new PaperDollRow(Row("ModifierRow"), [10L, 60L, null]),
                    new PaperDollRow(Row("ModifierRow"), [22L, 239L, 2L]),
                },
            }
        );
        var rendererAppearance = Avatar(
            new()
            {
                ["appearance"] = new PaperDollRow(Row("AppearanceRow"), [0.375d]),
                ["modifiers"] = new List<object?>
                {
                    new PaperDollRow(Row("ModifierRow"), [10L, 60L, 0L]),
                    new PaperDollRow(Row("ModifierRow"), [22L, 239L, 2L]),
                },
            }
        );
        var portrait = Avatar(
            new()
            {
                ["cameraPosition"] = new object?[] { 1.25d, 2d, 3d },
                ["cameraPoi"] = new object?[] { 4d, 5d, 6d },
                ["cameraFieldOfView"] = 0.75d,
                ["backgroundID"] = 123L,
                ["lightID"] = 10745L,
                ["poseData"] = new Dictionary<string, object?>
                {
                    ["HeadLookTarget"] = new object?[] { 0.1d, 0.2d, 0.3d },
                    ["SmileRight"] = 0.5d,
                    ["PortraitPoseNumber"] = 2L,
                },
            }
        );
        using (WorldDbContext saved = new(db.Path))
        {
            var pilot = saved.Pilots.Find(Other)!;
            pilot.Appearance = AvatarWire.Encode(appearance);
            pilot.Portrait = AvatarWire.Encode(portrait);
            saved.SaveChanges();
        }
        using var services = Eden.Server.ServerServices.Build(new(db.Path));
        using var scope = services.CreateScope();
        using var context = Context(db);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.False(context.Selected);
        var batch = Assert.IsType<Dictionary<long, object?>>(
            Reply(
                router.Call(
                    context,
                    "paperDollServer",
                    "GetPaperDollDataBatch",
                    new object?[]
                    {
                        new List<object?> { Other, Other, long.MaxValue },
                    },
                    _ => { }
                )
            )
        );
        Assert.Equal(BlueMarshal.Encode(rendererAppearance), BlueMarshal.Encode(Assert.Single(batch).Value));
        Assert.Equal(Other, Assert.Single(batch).Key);
        var single = Reply(router.Call(context, "paperDollServer", "GetPaperDollData", new object?[] { Other }, _ => { }));
        Assert.Equal(BlueMarshal.Encode(rendererAppearance), BlueMarshal.Encode(single));
        var settings = BlueRead.Tuple(
            Reply(router.Call(context, "paperDollServer", "GetPaperDollPortraitDataFor", new object?[] { Other }, _ => { })),
            2
        );
        var render = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(settings[0]))).State);
        Assert.Equal(1.25d, render["cameraX"]);
        Assert.Equal(2d, render["cameraY"]);
        Assert.Equal(3d, render["cameraZ"]);
        Assert.Equal(4d, render["cameraPoiX"]);
        Assert.Equal(5d, render["cameraPoiY"]);
        Assert.Equal(6d, render["cameraPoiZ"]);
        Assert.Equal(0.75d, render["cameraFieldOfView"]);
        Assert.Equal(123L, render["backgroundID"]);
        Assert.Equal(10745L, render["lightID"]);
        Assert.Equal(0.1d, render["headLookTargetX"]);
        Assert.Equal(0.2d, render["headLookTargetY"]);
        Assert.Equal(0.3d, render["headLookTargetZ"]);
        Assert.Equal(0.5d, render["smileRight"]);
        Assert.Equal(2L, render["portraitPoseNumber"]);
        Assert.Null(settings[1]);
        using (WorldDbContext saved = new(db.Path))
        {
            Assert.Equal(BlueMarshal.Encode(appearance), saved.Pilots.Find(Other)!.Appearance);
            Assert.Equal(BlueMarshal.Encode(portrait), saved.Pilots.Find(Other)!.Portrait);
        }
        var profile = BlueRead.Fields(
            Assert.IsType<BlueInstance>(Reply(router.Call(context, "charMgr", "GetPublicInfo", new object?[] { Pilot }, _ => { }))).State
        );
        Assert.Equal(Pilot, profile["characterID"]);
        Assert.Equal(1L, profile["gender"]);
        Assert.Equal((long)PilotRole.Player, context.Roles);
        Assert.False(context.Selected);
        Assert.Same(PilotCallResult.Unsupported, router.Call(context, "charMgr", "GetPublicInfo", new object?[] { Other }, _ => { }));
        Assert.Same(
            PilotCallResult.Unsupported,
            router.Call(context, "charMgr", "SetCharacterDescription", new object?[] { "edit" }, _ => { })
        );
    }

    [Fact]
    public void LegacyAndSimulatedPilotsUseStableNativeDollsMatchingBloodlineAndGenderWithoutWrites()
    {
        using TestWorld db = new();
        AddDollCatalog(db);
        var reader = db.Services.GetRequiredService<ICharacterPortraits>();
        var first = reader.Read([Pilot, Other]);
        Assert.All(first, source => Assert.Equal(0.375, source.DefaultDoll!.HairDarkness));
        var wire = BlueRead.Fields(Assert.IsType<BlueInstance>(Reply(new CharacterDollReply(first[0]))).State);
        Assert.Equal(new object?[] { 0.375d }, Assert.IsType<PaperDollRow>(wire["appearance"]).Fields);
        Assert.Equal(
            new object?[] { 3L, 285L, 2L },
            Assert.IsType<PaperDollRow>(Assert.Single(Assert.IsType<List<object?>>(wire["modifiers"]))).Fields
        );
        Assert.Equal(
            new object?[] { 1L, 0.1d, 0.2d, 0.3d },
            Assert.IsType<PaperDollRow>(Assert.Single(Assert.IsType<List<object?>>(wire["sculpts"]))).Fields
        );
        Assert.Equal(
            new object?[] { 5L, 173L, 0L, 0.4d, 0.5d },
            Assert.IsType<PaperDollRow>(Assert.Single(Assert.IsType<List<object?>>(wire["colors"]))).Fields
        );
        using (WorldDbContext saved = new(db.Path))
        {
            Assert.All(saved.Pilots.ToArray(), pilot => Assert.Null(pilot.Appearance));
            var pilot = saved.Pilots.Find(Other)!;
            pilot.GenderId = 0;
            saved.SaveChanges();
        }
        Assert.Equal(0.75, reader.Read([Other]).Single().DefaultDoll!.HairDarkness);
        var reopened = new CharacterPortraitStore(new(db.Path), new(CatalogProvider.Open(new WorldDatabase(db.Path).CatalogPath)));
        Assert.Equal(
            BlueMarshal.Encode(new CharacterDollReply(first[0])),
            BlueMarshal.Encode(new CharacterDollReply(reopened.Read([Pilot])[0]))
        );
    }

    [Fact]
    public void UnknownDeletedAndMissingAppearancesHaveSafeEmptyRepliesAndBatchesRejectMalformedInputs()
    {
        using TestWorld db = new();
        using (WorldDbContext saved = new(db.Path))
        {
            saved.Add(
                new SavedCharacterDeletion
                {
                    PilotId = Other,
                    ReadyAt = 1,
                    DeletedAt = 2,
                }
            );
            saved.SaveChanges();
        }
        using var context = Context(db);
        var handler = new CharacterPortraitHandler(
            db.Services.GetRequiredService<ICharacterPortraits>(),
            db.Services.GetRequiredService<IPilotCharacterSheet>()
        );
        Assert.Empty(
            BlueRead.Fields(
                Reply(
                    handler.Call(
                        context,
                        "paperDollServer",
                        "GetPaperDollDataBatch",
                        new object?[]
                        {
                            new List<object?> { Other, long.MaxValue },
                        },
                        null
                    )
                )
            )
        );
        Assert.Null(Reply(handler.Call(context, "paperDollServer", "GetPaperDollData", new object?[] { Other }, null)));
        Assert.Null(Reply(handler.Call(context, "paperDollServer", "GetPaperDollData", new object?[] { Pilot }, null)));
        var empty = BlueRead.Tuple(
            Reply(handler.Call(context, "paperDollServer", "GetPaperDollPortraitDataFor", new object?[] { long.MaxValue }, null)),
            2
        );
        Assert.Empty(Assert.IsType<List<object?>>(empty[0]));
        Assert.Null(empty[1]);
        object?[] badBatches =
        [
            null,
            new object?[] { Pilot },
            new List<object?> { "90000001" },
            new List<object?> { 0L },
            Enumerable.Repeat<object?>(Pilot, ICharacterPortraits.MaximumBatchSize + 1).ToList(),
        ];
        foreach (var batch in badBatches)
        {
            Assert.IsType<PilotCallResult.Failure>(
                handler.Call(context, "paperDollServer", "GetPaperDollDataBatch", new object?[] { batch }, null)
            );
        }
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "paperDollServer", "GetPaperDollData", new object?[] { Pilot }, new() { ["unexpected"] = true })
        );
        Assert.IsType<PilotCallResult.UnsupportedCall>(
            handler.Call(context, "paperDollServer", "WritePaperDollData", Array.Empty<object?>(), null)
        );
    }

    [Fact]
    public void SignedPortraitExpressionContainsPortableSourceAndAnEmptyChallengeResult()
    {
        byte[] data = BlueRead.Bytes(BlueMarshal.Decode(PortraitClient.Expression));
        using BinaryReader reader = new(new MemoryStream(data), Encoding.ASCII);
        Assert.Equal((byte)'s', reader.ReadByte());
        int length = reader.ReadInt32();
        string expression = Encoding.ASCII.GetString(reader.ReadBytes(length));
        Assert.Equal(data.Length, reader.BaseStream.Position);
        Assert.StartsWith("eval(compile(u", expression);
        Assert.EndsWith(", 'eden-portraits', 'exec'), {}) or {}", expression);
        Assert.Contains("EvePhoto.GetPortrait = get_portrait", expression);
    }

    [PinnedPortraitClientFact]
    public async Task SignedPortraitAdapterRunsInPinnedPythonAgainstPhotoStubs()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-portrait-expression-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(PortraitClient.Expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "portrait-client-tests", "portrait_client_test.py"));
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Portrait adapter assertions passed", await output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Row(string name) => "eve.common.script.paperDoll.paperDollDefinitions." + name;

    private static BlueInstance Avatar(Dictionary<string, object?> fields) => new("utillib.KeyVal", fields);

    private static object? Reply(BlueModel model) => BlueMarshal.Decode(BlueMarshal.Encode(model));

    private static object? Reply(PilotCallResult result) => Reply(Assert.IsType<PilotCallResult.Success>(result).Reply);

    private static PilotCallContext Context(TestWorld db)
    {
        var pilot = db.Store.FindPilot(1000001)!;
        return new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            42,
            SignedSettings.Empty
        );
    }

    private static void AddDollCatalog(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR REPLACE INTO SourceRecord VALUES
            ('client/npccharacters','3000001','{{"bloodlineID":1,"gender":1}}'),
            ('client/npccharacters','3000002','{{"bloodlineID":1,"gender":0}}'),
            ('client/paperdolls','3000001','{{"hairDarkness":0.375,
                "modifiers":[{{"modifierLocationID":3,"paperdollResourceID":285,"paperdollResourceVariation":2}}],
                "sculptWeights":[{{"sculptLocationID":1,"weightUpDown":0.1,"weightLeftRight":0.2,"weightForwardBack":0.3}}],
                "colorSelections":[{{"colorID":5,"colorNameA":173,"colorNameBC":0,"weight":0.4,"gloss":0.5}}]}}'),
            ('client/paperdolls','3000002','{{"hairDarkness":0.75,
                "modifiers":[{{"modifierLocationID":3,"paperdollResourceID":286,"paperdollResourceVariation":0}}]}}');
            """
        );
    }
}
