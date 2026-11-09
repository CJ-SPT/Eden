using System.Net;
using System.Text.Json;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Server.Administration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Eden.Protocol.Tests.Administration.SkillBonusEditorTests;

namespace Eden.Protocol.Tests.Administration;

public sealed class SkillBonusBuilderTests
{
    internal static SkillEditorChoices Choices()
    {
        SkillEditorModifier Output(int input, int attribute, string name, string target, DogmaOperation operation) =>
            new(input, attribute, operation, false, "Description")
            {
                AttributeName = name,
                TargetName = target,
                UnitName = "m/s",
            };
        SkillEditorOption Scale(int id, int input) =>
            new(id, "Level scaling")
            {
                Modifiers = [new((int)DogmaAttributeId.SkillLevel, input, DogmaOperation.PreMultiply, true, "Scale")],
            };
        return new(
            [],
            [new(20, "Velocity bonus"), new(434, "Mining bonus"), new(30, "Flat speed"), new(31, "Speed factor")],
            [
                Scale(10, 20),
                Scale(389, 434),
                new(11, "Internal speed rule") { Modifiers = [Output(20, 37, "Ship velocity", "your ship", DogmaOperation.PostPercent)] },
                new(391, "Internal mining rule")
                {
                    Modifiers = [Output(434, 77, "Ore mining yield", "fitted items requiring Mining", DogmaOperation.PostPercent)],
                },
                new(12, "Internal flat rule") { Modifiers = [Output(30, 37, "Ship velocity", "your ship", DogmaOperation.Add)] },
                new(13, "Internal multiplier") { Modifiers = [Output(31, 37, "Ship velocity", "your ship", DogmaOperation.PostMultiply)] },
                new(14, "Unsupported", Available: false)
                {
                    Modifiers = [Output(20, 37, "Ship velocity", "your ship", DogmaOperation.PostPercent)],
                },
            ],
            [],
            []
        );
    }

    [Fact]
    public void BuilderOffersDifferentStatsAndAddsRequestedBonusWithoutExposingRules()
    {
        var choices = Choices();
        var templates = SkillBonusBuilder.Templates(choices);
        Assert.Equal(4, templates.Count);
        Assert.Contains(templates, template => template.Name == "Ship velocity" && template.Target == "your ship");
        Assert.Contains(templates, template => template.Name == "Ore mining yield");
        Assert.DoesNotContain(templates, template => template.EffectId == 14);
        var speed = templates.Single(template => template.EffectId == 11);
        SkillForm form = new() { Name = "Custom speed" };
        SkillBonusBuilder.Add(form, choices, speed, 7.5, false, true);
        Assert.Equal(new[] { 11, 10 }, form.Effects.Select(effect => effect.EffectId));
        Assert.Equal(7.5, Assert.Single(form.Attributes).Value);
        var bonus = Assert.Single(SkillBonusBuilder.Read(form, choices, templates));
        Assert.Equal("+37.5%", bonus.Preview(5));
        SkillBonusBuilder.SetScaling(bonus, false);
        Assert.Equal("+7.5%", bonus.Preview(5));
        Assert.Equal(11, Assert.Single(form.Effects).EffectId);
        SkillBonusBuilder.SetScaling(bonus, true);
        Assert.Equal("+37.5%", bonus.Preview(5));
        bonus.Decrease = true;
        bonus.Amount = 2.5;
        Assert.Equal(-2.5, Assert.Single(form.Attributes).Value);
        Assert.Equal("-12.5%", bonus.Preview(5));
        SkillBonusBuilder.Remove(bonus);
        Assert.Empty(form.Effects);
        Assert.Equal(-2.5, Assert.Single(form.Attributes).Value);
    }

    [Fact]
    public void InlineScalingAndIndependentBonusesAreManagedTogetherWhileComplexRulesStayTechnical()
    {
        var choices = Choices();
        var application = choices.Effects.Single(effect => effect.Id == 11);
        var scaling = choices.Effects.Single(effect => effect.Id == 10).Modifiers.Single();
        var inline = application with { Id = 16, Modifiers = [scaling, application.Modifiers.Single()] };
        var complex = application with
        {
            Id = 17,
            Modifiers =
            [
                application.Modifiers.Single(),
                application.Modifiers.Single() with
                {
                    TargetAttributeId = (int)DogmaAttributeId.CargoCapacity,
                    AttributeName = "Cargo capacity",
                },
            ],
        };
        var intermediate = application with
        {
            Id = 18,
            Modifiers = [application.Modifiers.Single() with { AttributeName = "Ship Bonus CF" }],
        };
        choices = choices with { Effects = choices.Effects.Concat([inline, complex, intermediate]).ToArray() };
        var templates = SkillBonusBuilder.Templates(choices);
        Assert.Contains(templates, template => template.EffectId == 16 && template.InlineScaling);
        Assert.DoesNotContain(templates, template => template.EffectId is 17 or 18);
        SkillForm form = new();
        var template = templates.Single(template => template.EffectId == 16);
        Assert.Throws<InvalidOperationException>(() => SkillBonusBuilder.Add(form, choices, template, 5, false, false));
        Assert.Empty(form.Attributes);
        Assert.Empty(form.Effects);
        SkillBonusBuilder.Add(form, choices, template, 5, false, true);
        Assert.Equal(16, Assert.Single(form.Effects).EffectId);
        SkillBonusBuilder.Add(form, choices, templates.Single(template => template.EffectId == 391), 20, false, true);
        var bonuses = SkillBonusBuilder.Read(form, choices, templates);
        Assert.Equal(2, bonuses.Count);
        var speed = bonuses.Single(bonus => bonus.Template.EffectId == 16);
        var before = form.Build();
        Assert.Throws<InvalidOperationException>(() => SkillBonusBuilder.SetScaling(speed, false));
        Assert.True(SkillEditingRules.SameDefinition(before, form.Build()));
        SkillBonusBuilder.Remove(speed);
        Assert.Equal(new[] { 391, 389 }, form.Effects.Select(effect => effect.EffectId));
        Assert.Equal("+100%", Assert.Single(SkillBonusBuilder.Read(form, choices, templates)).Preview(5));
    }

    [Theory]
    [InlineData(11, true, 25, false, 25, "+125%")]
    [InlineData(11, true, 5, true, -5, "-25%")]
    [InlineData(11, false, 5, true, -5, "-5%")]
    [InlineData(12, false, 25, false, 25, "+25 m/s")]
    [InlineData(13, false, 25, false, 1.25, "+25%")]
    [InlineData(13, false, 25, true, 0.75, "-25%")]
    [InlineData(13, false, 0, true, 1, "0%")]
    public void AmountDirectionAndBasisMatchStoredMath(
        int effect,
        bool perLevel,
        double amount,
        bool decrease,
        double expected,
        string preview
    )
    {
        var choices = Choices();
        var templates = SkillBonusBuilder.Templates(choices);
        SkillForm form = new();
        var template = templates.Single(template => template.EffectId == effect);
        SkillBonusBuilder.Add(form, choices, template, amount, decrease, perLevel);
        Assert.Equal(expected, Assert.Single(form.Attributes).Value);
        var bonus = Assert.Single(SkillBonusBuilder.Read(form, choices, templates));
        Assert.Equal(amount, bonus.Amount, 8);
        Assert.Equal(preview, bonus.Preview(5));
    }

    [Fact]
    public void RejectedDuplicateSharedUnknownAndInvalidCompositionsPreserveDraft()
    {
        var choices = Choices();
        var speed = SkillBonusBuilder.Templates(choices).Single(template => template.EffectId == 11);
        SkillForm form = new();
        foreach (double amount in new[] { double.NaN, double.PositiveInfinity, -1 })
        {
            Assert.Throws<InvalidOperationException>(() => SkillBonusBuilder.Add(form, choices, speed, amount, false, true));
            Assert.Empty(form.Effects);
            Assert.Empty(form.Attributes);
        }
        SkillBonusBuilder.Add(form, choices, speed, 5, false, true);
        var before = form.Build();
        Assert.Throws<InvalidOperationException>(() => SkillBonusBuilder.Add(form, choices, speed, 10, false, true));
        Assert.True(SkillEditingRules.SameDefinition(before, form.Build()));
        form.Effects.Add(new() { EffectId = 391 });
        form.Attributes.Add(new() { AttributeId = 434, Value = 20 });
        // A second application reads the same strength; neither is safe to edit as an independent card.
        var other = choices.Effects.Single(effect => effect.Id == 11) with
        {
            Id = 15,
        };
        choices = choices with { Effects = choices.Effects.Append(other).ToArray() };
        form.Effects.Add(new() { EffectId = 15 });
        Assert.DoesNotContain(
            SkillBonusBuilder.Read(form, choices, SkillBonusBuilder.Templates(choices)),
            bonus => bonus.Template.InputId == 20
        );
        form.Effects.Add(new() { EffectId = 999999 });
        Assert.Empty(SkillBonusBuilder.Read(form, choices, SkillBonusBuilder.Templates(choices)));
        Assert.NotNull(SkillBonusBuilder.Conflict(form, choices, SkillBonusBuilder.Templates(choices).Single(t => t.EffectId == 12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyEditorShowsStatTargetStrengthAndBasisBeforeAnyClickAndComposesSelectedSpeed(bool decrease)
    {
        var choices = Choices();
        SkillForm form = new();
        TestableBonuses? panel = null;
        int changes = 0;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<BonusHost>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(BonusHost.Form)] = form,
                        [nameof(BonusHost.Options)] = choices,
                        [nameof(BonusHost.Editable)] = true,
                        [nameof(BonusHost.Capture)] = (Action<TestableBonuses>)(value => panel = value),
                        [nameof(BonusHost.Changed)] = EventCallback.Factory.Create(this, () => changes++),
                    }
                )
            );
            string html = WebUtility.HtmlDecode(component.ToHtmlString());
            Assert.Contains("What changes", html);
            Assert.Contains("Applies to", html);
            Assert.Contains("Ship velocity (%)", html);
            Assert.Contains("Ore mining yield (%)", html);
            Assert.Contains("Amount", html);
            Assert.Contains("Per skill level", html);
            Assert.DoesNotContain("Add ore mining yield", html);
            Assert.DoesNotContain("Effect rule", html);
            Assert.Empty(form.Effects);
            panel!.ChooseStat("37:True");
            Assert.Equal(0, changes);
            Assert.Empty(form.Effects);
            if (!decrease && Environment.GetEnvironmentVariable("EDEN_SKILL_LAYOUT_DIRECTORY") is { Length: > 0 } readyOutput)
            {
                Directory.CreateDirectory(readyOutput);
                await File.WriteAllTextAsync(Path.Combine(readyOutput, "skill-bonus-ready.html"), component.ToHtmlString());
            }
            await panel.Compose(7.5, decrease, true);
            panel.Refresh();
            Assert.Equal(1, changes);
            Assert.Equal(decrease ? -7.5 : 7.5, Assert.Single(form.Attributes).Value);
            Assert.Contains(decrease ? "-37.5%" : "+37.5%", WebUtility.HtmlDecode(component.ToHtmlString()));
            if (decrease && Environment.GetEnvironmentVariable("EDEN_SKILL_LAYOUT_DIRECTORY") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "skill-bonus-composed.html"), component.ToHtmlString());
            }
        });
    }

    [Fact]
    public void BuiltShipBonusPersistsReopensAndAffectsShipAtEachTrainedLevel()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        using (CatalogDbContext catalog = new(database.OriginalCatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (20,'speedFactor'),(280,'skillLevel')");
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                ('client/dogmaattributes','20','{{"name":"speedFactor","defaultValue":0}}'),
                ('client/dogmaattributes','280','{{"name":"skillLevel","defaultValue":0}}'),
                ('client/dogmaeffects','900001','{{"effectCategory":0,"modifierInfo":[
                    {{"domain":"itemID","func":"ItemModifier","modifiedAttributeID":20,"modifyingAttributeID":280,"operation":0}}]}}'),
                ('client/dogmaeffects','900002','{{"effectCategory":0,"modifierInfo":[
                    {{"domain":"shipID","func":"ItemModifier","modifiedAttributeID":37,"modifyingAttributeID":20,"operation":6}}]}}');
                """
            );
        }
        SkillEditorStore editor = new(database, TimeProvider.System);
        var choices = editor.Choices();
        var template = SkillBonusBuilder.Templates(choices).Single(template => template.EffectId == 900002);
        Assert.Equal("your ship", template.Target);
        var form = SkillForm.From(
            editor.Read().Skills.First().Definition with
            {
                TypeId = 0,
                Name = "Custom speed",
                Prerequisites = [],
                Attributes = [],
                Effects = [],
                IconId = null,
                MarketGroupId = null,
            }
        );
        SkillBonusBuilder.Add(form, choices, template, 7.5, false, true);
        var created = editor.Create(editor.Read().Generation, form.Build(), "Admin");
        var reopened = new SkillEditorStore(new(fixture.Path), TimeProvider.System);
        var snapshot = reopened.Read();
        var saved = snapshot.Skills.Single(skill => skill.Definition.TypeId == created.Definition.TypeId);
        Assert.True(SkillEditingRules.SameDefinition(created.Definition, saved.Definition));
        string id = new('a', 64);
        string package = Path.Combine(fixture.Path + ".skill-packages", id);
        Directory.CreateDirectory(package);
        SkillCatalogProjection.Create(database.OriginalCatalogPath, Path.Combine(package, "catalog.sqlite"), snapshot);
        File.WriteAllText(
            Path.Combine(package, "skills.json"),
            JsonSerializer.Serialize(new[] { new PublishedSkillDefinition(saved.Definition, true, false) }, SkillEditorStore.Json)
        );
        File.WriteAllText(
            Path.Combine(package, "package.json"),
            JsonSerializer.Serialize(new SkillClientPackage(id, snapshot.Generation, new string('b', 64), 1), SkillEditorStore.Json)
        );
        File.WriteAllText(fixture.Path + ".skills-active", id);
        WorldDatabase active = new(fixture.Path);
        TestClock clock = new(DateTimeOffset.UtcNow);
        const long pilot = 90000001;
        double baseline = new PilotFittingStore(active, clock).ShipAttributes(pilot)[DogmaAttributeId.MaxVelocity];
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Add(new PilotSkill { PilotId = pilot, TypeId = saved.Definition.TypeId });
            world.SaveChanges();
        }
        foreach (int level in new[] { 1, 3, 5, 0 })
        {
            using (WorldDbContext world = new(fixture.Path))
            {
                world.Set<PilotSkill>().Find(pilot, saved.Definition.TypeId)!.Points = TrainedSkill.PointsForLevel(
                    saved.Definition.Rank,
                    level
                );
                world.SaveChanges();
            }
            Assert.Equal(
                baseline * (1 + 0.075 * level),
                new PilotFittingStore(active, clock).ShipAttributes(pilot)[DogmaAttributeId.MaxVelocity],
                6
            );
        }
    }
}
