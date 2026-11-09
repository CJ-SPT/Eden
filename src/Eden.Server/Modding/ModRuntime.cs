using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.Modding;
using Eden.World.Modding;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server.Modding;

[Injectable(InjectionType.Singleton)]
internal sealed class ModRuntime(WorldDatabase database, ModStore store, IServiceScopeFactory scopes)
    : IModCommands,
        IModSessionEvents,
        IDisposable
{
    private readonly WorldDatabase _database = database;
    private readonly ModStore _store = store;
    private readonly IServiceScopeFactory _scopes = scopes;
    private IReadOnlyDictionary<int, SkillDefinition> _skills = new Dictionary<int, SkillDefinition>();

    private sealed record Command(long Roles, Func<ModCommandContext, ModCommandReply> Handler);

    private readonly Dictionary<(string Mod, string Name), Command> _commands = [];
    private readonly Dictionary<ModEventKind, List<Action<ModEvent>>> _events = [];
    private readonly List<PluginContext> _contexts = [];
    private readonly CancellationTokenSource _failure = new();
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;
    public CancellationToken FailureToken => _failure.Token;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
            var active = SkillPackageActivation.Active(_database.Path);
            if (active is null || !File.Exists(Path.Combine(_store.SetDirectory(active.Id), "mod-set.json")))
            {
                _started = true;
                return;
            }
            _store.VerifySet(active.Id);
            _skills = SkillEditorStore.LoadOriginal(_database, active: true);
            var set = _store.ReadSet(active.Id);
            foreach (var mod in set.Mods)
            {
                string root = Path.Combine(_store.SetDirectory(active.Id), "mods", mod.Id);
                var manifest = JsonSerializer.Deserialize<ModManifest>(
                    File.ReadAllText(Path.Combine(root, "manifest.json")),
                    ModFormat.Json
                )!;
                ModPackages.Validate(manifest);
                if (manifest.Server is not { } plugin)
                {
                    continue;
                }
                string path = Path.Combine(root, plugin.Assembly);
                PluginContext context = new(path);
                _contexts.Add(context);
                var assembly = context.LoadPlugin(path);
                var type = assembly.GetType(plugin.Type, throwOnError: true)!;
                var instance =
                    Activator.CreateInstance(type) as IServerMod
                    ?? throw new InvalidDataException("A server mod entry must implement IServerMod.");
                instance.Register(new Registration(this, manifest.Id));
            }
            _started = true;
            Publish(new(ModEventKind.Started, null));
        }
    }

    public ModCommandReply Execute(string modId, string command, ModCommandContext context)
    {
        Command? registered;
        lock (_gate)
        {
            if (!_started || _failure.IsCancellationRequested)
            {
                return ModCommandReply.Failed("The mod runtime is unavailable.");
            }
            if (
                Encoding.UTF8.GetByteCount(context.Arguments) > ModFormat.MaximumCommandBytes
                || !_commands.TryGetValue((modId, command), out registered)
            )
            {
                return ModCommandReply.Failed("Unknown mod command or oversized request.");
            }
        }
        if ((context.Pilot.Roles & registered.Roles) != registered.Roles)
        {
            return ModCommandReply.Failed("This mod command requires additional roles.");
        }
        try
        {
            var result = registered.Handler(context);
            if (
                Encoding.UTF8.GetByteCount(result.Json) > ModFormat.MaximumCommandBytes
                || result.Error is { Length: > ModFormat.MaximumCommandBytes }
            )
            {
                throw new InvalidDataException("The mod command reply is too large.");
            }
            using var document = JsonDocument.Parse(result.Json);
            return result;
        }
        catch (Exception error)
        {
            ServerLog.Console.Error($"Mod {modId} command {command} failed: {error}");
            return ModCommandReply.Failed("The mod command failed. See the host log.");
        }
    }

    public void Publish(ModEvent notification)
    {
        Action<ModEvent>[] callbacks;
        lock (_gate)
        {
            if (_failure.IsCancellationRequested)
            {
                return;
            }
            callbacks = _events.TryGetValue(notification.Kind, out var registered) ? registered.ToArray() : [];
        }
        try
        {
            foreach (var callback in callbacks)
            {
                callback(notification);
            }
        }
        catch
        {
            _failure.Cancel();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                if (_started)
                {
                    Publish(new(ModEventKind.Stopping, null));
                }
            }
            finally
            {
                _commands.Clear();
                _events.Clear();
                foreach (var context in _contexts)
                {
                    context.Unload();
                }
                _failure.Dispose();
            }
        }
    }

    private sealed class PluginContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        internal Assembly LoadPlugin(string file)
        {
            using var stream = File.OpenRead(file);
            string symbols = Path.ChangeExtension(file, ".pdb");
            using var pdb = File.Exists(symbols) ? File.OpenRead(symbols) : null;
            return pdb is null ? LoadFromStream(stream) : LoadFromStream(stream, pdb);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IServerMod).Assembly.GetName().Name)
            {
                return typeof(IServerMod).Assembly;
            }
            if (name.Name == typeof(SkillDefinition).Assembly.GetName().Name)
            {
                return typeof(SkillDefinition).Assembly;
            }
            return _resolver.ResolveAssemblyToPath(name) is { } assembly ? LoadPlugin(assembly) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
    }

    private sealed class Registration(ModRuntime runtime, string id) : IModRegistration
    {
        public IModServices Services { get; } = new Services(runtime, id);

        public void Command(string name, long requiredRoles, Func<ModCommandContext, ModCommandReply> handler)
        {
            ModPackages.Identity(name);
            if (runtime._started)
            {
                throw new InvalidOperationException("Mod registration is closed after startup.");
            }
            if (requiredRoles < 0 || !runtime._commands.TryAdd((id, name), new(requiredRoles, handler)))
            {
                throw new InvalidDataException("Duplicate mod command or invalid roles.");
            }
        }

        public void On(ModEventKind kind, Action<ModEvent> handler)
        {
            if (runtime._started || !Enum.IsDefined(kind))
            {
                throw new InvalidDataException("Unknown mod event kind.");
            }
            if (!runtime._events.TryGetValue(kind, out var callbacks))
            {
                callbacks = [];
                runtime._events.Add(kind, callbacks);
            }
            callbacks.Add(handler);
        }
    }

    private sealed class Services(ModRuntime runtime, string id) : IModServices
    {
        public string? ReadState(string key) => runtime._store.ReadState(id, key);

        public void WriteState(string key, string json) => runtime._store.WriteState(id, key, json);

        public int ResolveSkill(string logicalId) => runtime._store.ResolveSkill(logicalId);

        public SkillDefinition? Skill(int typeId) => runtime._skills.GetValueOrDefault(typeId);

        public IReadOnlyList<TrainedSkill> Skills(long pilotId)
        {
            using var scope = runtime._scopes.CreateScope();
            using var operation = scope.ServiceProvider.GetRequiredService<IPilotOperationScope>().Begin();
            return scope.ServiceProvider.GetRequiredService<IPilotSkillReader>().ReadSkills(pilotId);
        }

        public RedeemToken Grant(RedeemGrant grant)
        {
            using var scope = runtime._scopes.CreateScope();
            using var operation = scope.ServiceProvider.GetRequiredService<IPilotOperationScope>().Begin();
            return scope
                .ServiceProvider.GetRequiredService<IAccountRedemption>()
                .Grant(grant with { Reference = id + ":" + grant.Reference });
        }

        public void Log(string message) => ServerLog.Console.Information($"Mod {id}: {message}");
    }
}
