using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

/// <summary>
/// Lets a realm name its patches after what they do (patch-z40-druid-forms.MPQ) instead of the one
/// character the stock 3.3.5a client allows (patch-R.MPQ): a graphics pack takes a dozen of the
/// letters, and Windows won't tell patch-p from patch-P.
///
/// The client (build 12340) lists Data and matches every file name against the pattern
/// "patch-?.MPQ" with its own wildcard matcher (0x428210), which also knows "*". Changing that one
/// character to "*", and the same in the locale folder's "patch-%s-?.MPQ", makes it load a patch of
/// any name. It still opens them in name order with the later name winning, so one-character patches
/// keep working and keep their place.
///
/// The edit is two bytes and always gives the same file, so a client edited here matches a client
/// torrent that carries the edited Wow.exe.
/// </summary>
public static class LongPatchNameService
{
    private static readonly (byte[] Stock, byte[] Long)[] Patterns =
    [
        (Encoding.ASCII.GetBytes("patch-?.MPQ\0"), Encoding.ASCII.GetBytes("patch-*.MPQ\0")),
        (Encoding.ASCII.GetBytes("patch-%s-?.MPQ\0"), Encoding.ASCII.GetBytes("patch-%s-*.MPQ\0")),
    ];

    private static readonly Regex StockName = new(@"^patch-(?:.|[a-z]{4}-.)\.mpq$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when a stock client loads a patch of this name: patch-X.MPQ, or patch-enUS-X.MPQ in a locale folder.</summary>
    public static bool IsStockName(string fileName) => StockName.IsMatch(fileName);

    /// <summary>True when the realm lists a patch MPQ only an edited client loads.</summary>
    public static bool Needed(RealmInfo realm) => realm.Patches.Any(p => p.InstallMode == PatchInstallMode.File
        && p.FileName.StartsWith("patch-", StringComparison.OrdinalIgnoreCase)
        && p.FileName.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase)
        && !IsStockName(p.FileName));

    /// <summary>
    /// Makes <paramref name="executable"/> (inside the client <paramref name="root"/>) load patches of
    /// any name. Returns true when it changed the file, false when it already did. The file it replaces
    /// is kept under .portalkeeper/backups. Throws when the file isn't a client this knows how to edit:
    /// each pattern has to be there exactly once, in one form or the other.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var bytes = File.ReadAllBytes(executable);
        var changed = false;
        foreach (var (stock, longer) in Patterns)
        {
            var stockAt = Find(bytes, stock);
            var longAt = Find(bytes, longer);
            if (stockAt.Length + longAt.Length != 1)
                throw new InvalidDataException(Path.GetFileName(executable) + " isn't a 3.3.5a client that can be set up for the realm's patches.");
            if (stockAt.Length == 0) continue;
            longer.CopyTo(bytes, stockAt[0]);
            changed = true;
        }
        if (!changed) return false;

        ManagedRuntimeWriteGuard.Check(root, executable);
        var backup = ManagedPath.Resolve(root, Path.Combine(".portalkeeper", "backups", "executable",
            Guid.NewGuid().ToString("N"), Path.GetFileName(executable)));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(executable, backup);
        var temp = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // A copy first: it keeps the file's permissions (the executable bit under Wine).
            File.Copy(executable, temp);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, executable, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }

    private static int[] Find(byte[] haystack, byte[] needle)
    {
        var found = new System.Collections.Generic.List<int>();
        var span = haystack.AsSpan();
        for (var offset = 0; offset <= haystack.Length - needle.Length;)
        {
            var at = span[offset..].IndexOf(needle);
            if (at < 0) break;
            found.Add(offset + at);
            offset += at + 1;
        }
        return found.ToArray();
    }
}
