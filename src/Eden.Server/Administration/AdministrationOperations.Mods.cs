using System.Security.Claims;
using Eden.Modding;
using Microsoft.AspNetCore.Components.Forms;

namespace Eden.Server.Administration;

internal sealed record AdminModInstall(string Path);

internal sealed record AdminModSelection(string Hash, bool Enabled);

internal sealed record AdminModBuild(string ClientRoot);

internal sealed partial class AdministrationOperations
{
    internal async Task<ModSetStatus> Mods(ClaimsPrincipal user)
    {
        await Authorize(user);
        return mods.Status();
    }

    internal async Task<string> InstallMod(ClaimsPrincipal user, AdminModInstall request)
    {
        await Authorize(user);
        return mods.Install(request.Path);
    }

    internal async Task<string> UploadMod(ClaimsPrincipal user, IBrowserFile archive)
    {
        await Authorize(user);
        await using var source = archive.OpenReadStream(ModPackages.MaximumBytes);
        string path = Path.GetTempFileName();
        try
        {
            await using (var staged = File.Create(path))
            {
                await source.CopyToAsync(staged);
            }
            return await InstallMod(user, new(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal async Task<ModSetStatus> SelectMod(ClaimsPrincipal user, AdminModSelection request)
    {
        await Authorize(user);
        mods.Enable(request.Hash, request.Enabled);
        return mods.Status();
    }

    internal async Task<ModSetPackage> BuildMods(ClaimsPrincipal user, AdminModBuild request)
    {
        await Authorize(user);
        return await modPublisher.Build(request.ClientRoot, authorize: () => Authorize(user));
    }
}
