using Portalkeeper.Models;

namespace Portalkeeper.ViewModels;

/// <summary>
/// One row of the SETTINGS page's REALM PATCHES: a Recommended or Optional realm patch the player
/// turns on or off. Turning it off takes the patch out of the client, turning it on puts it back.
/// </summary>
public sealed class PatchChoice(MainViewModel owner, PatchDefinition patch, bool isOn, bool isInstalled, bool isValid)
{
    public string Name => patch.Name;

    public bool IsOn
    {
        get => isOn;
        set => owner.SetPatchChoice(patch, value);
    }

    public string Status => (isOn, isInstalled, isValid) switch
    {
        (true, _, true) => "On",
        (true, _, false) => "On: installs with the next realm check",
        (false, true, _) => "Off: comes out with the next realm check",
        _ => "Off",
    };
}
