using System.Text.Json;
using Eden.Modding;
using Eden.World.Modding;
using Eden.World.Storage;

namespace Eden.Server.Modding;

internal static class ModCommands
{
    internal static async Task<bool> TryRun(string[] args, WorldDatabase database)
    {
        string? Value(string option)
        {
            int index = Array.IndexOf(args, option);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var store = new ModStore(database);
        if (Value("--export-modding-reference") is { } reference)
        {
            await File.WriteAllTextAsync(reference, JsonSerializer.Serialize(store.Reference(), ModFormat.Json));
            return true;
        }
        if (Value("--mod-install") is { } archive)
        {
            Console.WriteLine(store.Install(archive));
            return true;
        }
        if (Value("--mod-enable") is { } enable)
        {
            store.Enable(enable, true);
            return true;
        }
        if (Value("--mod-disable") is { } disable)
        {
            store.Enable(disable, false);
            return true;
        }
        if (Value("--mod-build") is { } client)
        {
            Console.WriteLine(JsonSerializer.Serialize(await new ModPublisher(database, store).Build(client), ModFormat.Json));
            return true;
        }
        if (args.Contains("--mod-status"))
        {
            Console.WriteLine(JsonSerializer.Serialize(store.Status(), ModFormat.Json));
            return true;
        }
        return false;
    }
}
