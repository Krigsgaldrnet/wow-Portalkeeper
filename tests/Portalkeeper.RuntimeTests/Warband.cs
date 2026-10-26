using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: the warband camp's Wow.exe edits (WarbandSelectService) on a stand-in PE laid out like
// build 12340 (.text and .zdata), and `warband-exe <Wow.exe>` to apply them to a copy of a real client.
internal static partial class Program
{
    /// <summary>A PE32 stand-in for Wow.exe with .text and .zdata where build 12340 has them, stock bytes at every place.</summary>
    private static byte[] WarbandStandInClient(uint zdataRaw = 0x72CE00, uint imageBase = 0x400000)
    {
        const int textSize = 0xE4000;
        var bytes = new byte[zdataRaw + 0x1000];
        new Random(71).NextBytes(bytes.AsSpan(0x400, textSize));
        void U16(int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(bytes, at);
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        U32(0x3C, 0x80);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, 0x80);
        U16(0x84, 0x014C); // i386
        U16(0x86, 2); // .text, .zdata
        U16(0x94, 0xE0);
        U16(0x96, 0x0102);
        const int optional = 0x98;
        U16(optional, 0x10B); // PE32
        U32(optional + 4, textSize);
        U32(optional + 16, 0x1000);
        U32(optional + 20, 0x1000);
        U32(optional + 28, imageBase);
        U32(optional + 32, 0x1000);
        U32(optional + 36, 0x200);
        U16(optional + 40, 4);
        U16(optional + 48, 4);
        U32(optional + 56, WarbandSelectService.ZdataRva + 0x1000);
        U32(optional + 60, 0x400);
        U16(optional + 68, 2);
        U32(optional + 92, 16);
        const int text = optional + 0xE0;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(bytes, text);
        U32(text + 8, textSize);
        U32(text + 12, 0x1000);
        U32(text + 16, textSize);
        U32(text + 20, 0x400);
        U32(text + 36, 0x60000020);
        const int zdata = text + 40;
        Encoding.ASCII.GetBytes(".zdata\0\0").CopyTo(bytes, zdata);
        U32(zdata + 8, 0x1000);
        U32(zdata + 12, WarbandSelectService.ZdataRva);
        U32(zdata + 16, 0x1000);
        U32(zdata + 20, zdataRaw);
        U32(zdata + 36, 0xE0000040);
        foreach (var place in WarbandSelectService.Places)
            if (place.Offset + place.StockBytes.Length <= bytes.Length) place.StockBytes.CopyTo(bytes, place.Offset);
        // The character limit's guard too, so both edits can be tried together.
        Convert.FromHexString("807DFF0AC745F800000000760B").CopyTo(bytes, CharacterListLimitService.GuardOffset);
        return bytes;
    }

    private static void WarbandSelectChecks(string root, Action<string, bool> check)
    {
        var client = Path.Combine(root, "warband");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "Wow.exe");
        var stock = WarbandStandInClient();
        File.WriteAllBytes(exe, stock);
        string[] Backups() => Directory.Exists(Path.Combine(client, ".portalkeeper", "backups", "executable"))
            ? Directory.EnumerateFiles(Path.Combine(client, ".portalkeeper", "backups", "executable"), "*", SearchOption.AllDirectories).ToArray()
            : [];

        check("warband: a stock client is not set up yet", !WarbandSelectService.IsSetUp(exe) && Backups().Length == 0);
        check("warband: a stock client is changed", WarbandSelectService.Ensure(client, exe) && WarbandSelectService.IsSetUp(exe));
        var edited = File.ReadAllBytes(exe);
        var changed = Enumerable.Range(0, stock.Length).Where(i => stock[i] != edited[i]).ToArray();
        var expected = WarbandSelectService.Places.Sum(p => Enumerable.Range(0, p.StockBytes.Length).Count(i => p.StockBytes[i] != p.EditedBytes[i]));
        check("warband: only the places change, 112 bytes", changed.Length == expected && expected == 112
            && changed.All(i => WarbandSelectService.Places.Any(p => i >= p.Offset && i < p.Offset + p.StockBytes.Length)));
        check("warband: SelectCharacter's first-argument call goes to the spot stub",
            edited.AsSpan(0xE39AC, 5).SequenceEqual(Convert.FromHexString("E84FD88E00")));
        check("warband: the file it replaced is kept", Backups().Length == 1 && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("warband: a client that's set up is left alone", !WarbandSelectService.Ensure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1);

        // With the character limit, either order gives the same file.
        File.WriteAllBytes(exe, stock);
        var limitFirst = CharacterListLimitService.Ensure(client, exe) && WarbandSelectService.Ensure(client, exe);
        var both = File.ReadAllBytes(exe);
        File.WriteAllBytes(exe, stock);
        var warbandFirst = WarbandSelectService.Ensure(client, exe) && CharacterListLimitService.Ensure(client, exe);
        check("warband: with the character limit, either order gives the same file", limitFirst && warbandFirst
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(both) && WarbandSelectService.IsSetUp(exe) && CharacterListLimitService.IsSetUp(exe));

        // Anything else is refused and left as it was.
        var foreign = Path.Combine(client, "Other.exe");
        bool Refused(byte[] bytes)
        {
            File.WriteAllBytes(foreign, bytes);
            var ensured = false;
            var asked = false;
            try { WarbandSelectService.Ensure(client, foreign); } catch (InvalidDataException) { ensured = true; }
            try { WarbandSelectService.IsSetUp(foreign); } catch (InvalidDataException) { asked = true; }
            return ensured && asked && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(bytes);
        }
        var partly = (byte[])stock.Clone();
        WarbandSelectService.Places[0].EditedBytes.CopyTo(partly, WarbandSelectService.Places[0].Offset);
        check("warband: a partly edited client is refused", Refused(partly));
        var otherCall = (byte[])stock.Clone();
        otherCall[0xE311C + 1] ^= 0x10;
        check("warband: a different call is refused", Refused(otherCall));
        var usedPlace = (byte[])stock.Clone();
        usedPlace[0x72DC00 + 3] = 0x90;
        check("warband: a place in .zdata that isn't empty is refused", Refused(usedPlace));
        var keptPlace = (byte[])stock.Clone();
        keptPlace[0xE2732] ^= 0x01; // the bytes after GetCharacterInfo's hook, never changed but checked
        check("warband: a changed byte where nothing is written is refused", Refused(keptPlace));
        check("warband: a truncated file is refused", Refused(stock.AsSpan(0, 0x72DC10).ToArray()));

        // The launcher's softer calls: a client it can't edit is left alone and still counts as done.
        File.WriteAllBytes(foreign, partly);
        check("warband: TryEnsure leaves a client it can't edit alone", !WarbandSelectService.TryEnsure(client, foreign)
            && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(partly) && WarbandSelectService.IsSetUpOrRefused(foreign));
        File.WriteAllBytes(foreign, stock);
        check("warband: TryEnsure edits a stock client", !WarbandSelectService.IsSetUpOrRefused(foreign)
            && WarbandSelectService.TryEnsure(client, foreign) && WarbandSelectService.IsSetUp(foreign));
        check("warband: a PE with another image base is refused", Refused(WarbandStandInClient(imageBase: 0x10000000)));
        check("warband: a PE whose .zdata sits elsewhere in the file is refused", Refused(WarbandStandInClient(zdataRaw: 0x72D000)));
        check("warband: a file that isn't a PE is refused", Refused(Encoding.ASCII.GetBytes("not a client").Concat(new byte[0x72E000]).ToArray()));
    }

    /// <summary>`warband-exe Wow.exe`: applies the edits to a copy next to it and prints both SHA-256s.</summary>
    private static int RunWarbandExe(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: warband-exe <Wow.exe>"); return 2; }
        var source = Path.GetFullPath(args[1]);
        var root = Path.Combine(Path.GetTempPath(), "pk-warband-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Wow.exe");
        File.Copy(source, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  " + source);
        var changed = WarbandSelectService.Ensure(root, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  edited" + (changed ? "" : " (already)"));
        Directory.Delete(root, true);
        return 0;
    }
}
