using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

    // A locale is two lower-case and two upper-case letters (enUS), so patch-test.MPQ isn't one.
    private static readonly Regex StockName = new(@"^(?i:patch)-(?:.|[a-z]{2}[A-Z]{2}(?:-.)?)(?i:\.mpq)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// True when a stock client loads a patch of this name: patch-X.MPQ, or patch-enUS.MPQ and
    /// patch-enUS-X.MPQ in a locale folder.
    /// </summary>
    public static bool IsStockName(string fileName) => StockName.IsMatch(fileName);

    /// <summary>True when the realm lists a patch MPQ only an edited client loads.</summary>
    public static bool Needed(RealmInfo realm) => realm.Patches.Any(p => p.InstallMode == PatchInstallMode.File
        && p.FileName.StartsWith("patch-", StringComparison.OrdinalIgnoreCase)
        && p.FileName.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase)
        && !IsStockName(p.FileName));

    /// <summary>
    /// True when <paramref name="executable"/> already loads patches of any name. Throws when the file
    /// isn't a client this knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Makes <paramref name="executable"/> (inside the client <paramref name="root"/>) load patches of
    /// any name. Returns true when it changed the file, false when it already did. The file it replaces
    /// is kept under .portalkeeper/backups, once however often this is retried. Throws when the file
    /// isn't a client this knows how to edit: each pattern has to be there exactly once, in one form or
    /// the other. A failure leaves the file as it was.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var name = Path.GetFileName(executable);
        var original = File.ReadAllBytes(executable);
        var edited = Edited(original, name);
        if (edited is null) return false;

        ManagedRuntimeWriteGuard.Check(root, executable);
        var temp = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Written fresh, not copied: a copy of a read-only Wow.exe couldn't be written to.
            File.WriteAllBytes(temp, edited);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(executable));

            var backup = ManagedPath.Resolve(root, Path.Combine(".portalkeeper", "backups", "executable",
                Convert.ToHexString(SHA256.HashData(original))[..16].ToLowerInvariant(), name));
            if (!File.Exists(backup) || new FileInfo(backup).Length != original.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (File.Exists(backup)) File.SetAttributes(backup, FileAttributes.Normal);
                File.Copy(executable, backup, true);
            }

            // Windows won't replace a read-only file; elsewhere the folder decides.
            var attributes = File.GetAttributes(executable);
            var readOnly = OperatingSystem.IsWindows() && (attributes & FileAttributes.ReadOnly) != 0;
            if (readOnly) File.SetAttributes(executable, attributes & ~FileAttributes.ReadOnly);
            try { File.Move(temp, executable, true); }
            finally { if (readOnly) File.SetAttributes(executable, File.GetAttributes(executable) | FileAttributes.ReadOnly); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }

    /// <summary>The file with both patterns in their long form, or null when it has them already.</summary>
    private static byte[]? Edited(byte[] original, string name)
    {
        byte[]? bytes = null;
        foreach (var (stock, longer) in Patterns)
        {
            var stockAt = Find(original, stock);
            var longAt = Find(original, longer);
            if (stockAt.Length + longAt.Length != 1)
                throw new InvalidDataException(name + " isn't a 3.3.5a client that can be set up for the realm's patches.");
            if (stockAt.Length == 0) continue;
            bytes ??= (byte[])original.Clone();
            longer.CopyTo(bytes, stockAt[0]);
        }
        return bytes;
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
