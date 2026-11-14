using System.Security.Claims;
using Eden.Gameplay.Configuration;
using Eden.Server.Administration;
using Eden.Server.Administration.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [AdministrationBrowserFact]
    public async Task LiveSettingsStayInteractiveInFirefoxAndChromium()
    {
        foreach (string browser in new[] { "firefox", "chromium" })
        {
            await using var fixture = await Fixture.Start(liveWorld: true);
            System.Diagnostics.ProcessStartInfo start = new("python")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(
                Path.Combine(AppContext.BaseDirectory, "administration-browser-tests", "Test-AdministrationInteractivity.py")
            );
            start.ArgumentList.Add(fixture.Client.BaseAddress!.ToString());
            start.ArgumentList.Add(browser);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            Assert.True(process.ExitCode == 0, browser + ": " + await output + await errors);
            var state = fixture.World.Services.GetRequiredService<IWorldConfiguration>().Read();
            Assert.Equal(1, state.Active.Id);
            Assert.Equal(3, state.Pending!.Id);
            Assert.Equal(3, state.Pending.Draft.Settings.Rewards.BountyMultiplier);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsSaveUpdatesStatusAndKeepsControlsInteractiveAcrossRepeatedSaves(bool liveWorld)
    {
        await using var fixture = await Fixture.Start(liveWorld);
        using var scope = fixture.App.Services.CreateScope();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(scope.ServiceProvider.GetRequiredService<AdministrationOperations>())
            .AddSingleton<AuthenticationStateProvider>(new AdministratorAuthentication(fixture.Accounts.Authenticate("Corey", Password)!))
            .BuildServiceProvider();
        await using InteractionRenderer renderer = new(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SettingsPage>();
            await renderer.Change(nameof(SettingsForm.OreYieldMultiplier), "2");
            Assert.Contains("Mining hold capacity", renderer.Text);
            await renderer.Change(nameof(SettingsForm.GeneralMiningHoldMultiplier), "2.5");
            await renderer.Change(nameof(SettingsForm.IceHoldMultiplier), "0.5");
            await renderer.Change(nameof(SettingsForm.GasHoldMultiplier), "3");
            Assert.Contains("Unsaved changes", renderer.Text);
            Assert.False(renderer.Button("Save for restart").Disabled);
            await renderer.Submit();
            Assert.Contains("Revision 2 saved · pending restart.", renderer.Text);
            Assert.True(renderer.Button("Save for restart").Disabled);
            var config = fixture.World.Services.GetRequiredService<IWorldConfiguration>();
            var saved = config.Read();
            Assert.Equal(2, saved.Pending!.Draft.Settings.Resources.OreYieldMultiplier);
            Assert.Equal(new MiningHoldPolicy(2.5m, .5m, 3), saved.Pending.Draft.Settings.MiningHolds);
            Assert.Equal(new MiningHoldPolicy(), saved.Active.Draft.Settings.MiningHolds);
            await renderer.Click("Rewards");
            await renderer.Change(nameof(SettingsForm.BountyMultiplier), "3");
            await renderer.Submit();
            Assert.Contains("Revision 3 saved · pending restart.", renderer.Text);
            Assert.Equal(new MiningHoldPolicy(2.5m, .5m, 3), config.Read().Pending!.Draft.Settings.MiningHolds);
            await renderer.Click("Gathering");
            await renderer.Click("Reset to 1×");
            Assert.False(renderer.Button("Save for restart").Disabled);
            await renderer.Click("Discard unsaved changes");
            Assert.Contains("Unsaved changes discarded.", renderer.Text);
            Assert.Equal("2", renderer.Input(nameof(SettingsForm.OreYieldMultiplier)).Attributes["value"]);
            await renderer.Click("Validate draft");
            Assert.Contains("Draft validation passed.", renderer.Text);
        });
    }

    private sealed class AdministratorAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class AdministrationBrowserFactAttribute : FactAttribute
    {
        public AdministrationBrowserFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("EDEN_TEST_ADMIN_BROWSER") != "1")
            {
                Skip = "Set EDEN_TEST_ADMIN_BROWSER=1 with Python Playwright, Firefox and Chromium installed.";
            }
        }
    }

    // Exercise Blazor's real event dispatch and render lifecycle without a browser or an extra test framework.
#pragma warning disable BL0006
    private sealed class InteractionRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private int Root;
        private readonly List<Exception> Errors = [];
        internal string Text => ReadText(GetCurrentRenderTreeFrames(Root));

        internal async Task Mount<T>()
            where T : IComponent
        {
            Root = AssignRootComponentId(InstantiateComponent(typeof(T)));
            await RenderRootComponentAsync(Root);
            Assert.Empty(Errors);
        }

        protected override void HandleException(Exception exception) => Errors.Add(exception);

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        internal Element Button(string text) => Elements(Root).First(element => element.Name == "button" && element.Text == text);

        internal Element Input(string property) =>
            Elements(Root)
                .Single(element =>
                    element.Name == "input"
                    && element.Attributes.GetValueOrDefault("name") is string name
                    && name.EndsWith("." + property, StringComparison.Ordinal)
                );

        internal Task Click(string text) => Dispatch(Button(text), "onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        internal Task Change(string property, string value) => Dispatch(Input(property), "onchange", new ChangeEventArgs { Value = value });

        internal Task Submit() => Dispatch(Elements(Root).Single(element => element.Name == "form"), "onsubmit", EventArgs.Empty);

        private async Task Dispatch(Element element, string eventName, EventArgs args)
        {
            await DispatchEventAsync(element.Events[eventName], null, args);
            Assert.Empty(Errors);
        }

        private IEnumerable<Element> Elements(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (int index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    foreach (var element in Elements(frame.ComponentId))
                    {
                        yield return element;
                    }
                    index += frame.ComponentSubtreeLength - 1;
                }
                else if (frame.FrameType == RenderTreeFrameType.Element)
                {
                    Dictionary<string, object?> attributes = [];
                    Dictionary<string, ulong> events = [];
                    for (int attributeIndex = index + 1; attributeIndex < frames.Count; attributeIndex++)
                    {
                        var attribute = frames.Array[attributeIndex];
                        if (attribute.FrameType != RenderTreeFrameType.Attribute)
                        {
                            break;
                        }
                        attributes[attribute.AttributeName] = attribute.AttributeValue;
                        events[attribute.AttributeName] = attribute.AttributeEventHandlerId;
                    }
                    yield return new(
                        frame.ElementName,
                        attributes,
                        events,
                        ReadText(frames, index + 1, index + frame.ElementSubtreeLength)
                    );
                }
            }
        }

        private string ReadText(ArrayRange<RenderTreeFrame> frames, int start = 0, int? end = null)
        {
            System.Text.StringBuilder text = new();
            for (int index = start; index < (end ?? frames.Count); index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    text.Append(ReadText(GetCurrentRenderTreeFrames(frame.ComponentId)));
                    index += frame.ComponentSubtreeLength - 1;
                }
                else if (frame.FrameType == RenderTreeFrameType.Text)
                {
                    text.Append(frame.TextContent);
                }
                else if (frame.FrameType == RenderTreeFrameType.Markup)
                {
                    text.Append(frame.MarkupContent);
                }
            }
            return text.ToString();
        }

        internal sealed record Element(string Name, Dictionary<string, object?> Attributes, Dictionary<string, ulong> Events, string Text)
        {
            internal bool Disabled => Attributes.GetValueOrDefault("disabled") is true;
        }
    }
#pragma warning restore BL0006
}
