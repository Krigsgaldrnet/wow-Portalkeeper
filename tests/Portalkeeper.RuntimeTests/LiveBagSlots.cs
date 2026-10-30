using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: the live bag sizes' Wow.exe edit (LiveBagSlotsService) on the guild bank's stand-in PE,
// and `live-bag-exe <Wow.exe>` to apply it to a copy of a real client.
internal static partial class Program
{
    private static void LiveBagSlotsChecks(string root, Action<string, bool> check)
    {
        var client = Path.Combine(root, "live-bags");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "Wow.exe");
        var stock = GuildBankStandInClient();
        File.WriteAllBytes(exe, stock);
        string[] Backups() => Directory.Exists(Path.Combine(client, ".portalkeeper", "backups", "executable"))
            ? Directory.EnumerateFiles(Path.Combine(client, ".portalkeeper", "backups", "executable"), "*", SearchOption.AllDirectories).ToArray()
            : [];

        check("live bags: 2 places", LiveBagSlotsService.Places.Length == 2);
        check("live bags: a stock client is not set up yet", !LiveBagSlotsService.IsSetUp(exe) && !LiveBagSlotsService.IsSetUpOrRefused(exe)
            && Backups().Length == 0);
        check("live bags: a stock client is changed", LiveBagSlotsService.Ensure(client, exe) && LiveBagSlotsService.IsSetUp(exe));
        var edited = File.ReadAllBytes(exe);
        var changed = Enumerable.Range(0, stock.Length).Where(i => stock[i] != edited[i]).ToArray();
        check("live bags: only the places change, 6 bytes", changed.Length == 6
            && changed.All(i => LiveBagSlotsService.Places.Any(p => i >= p.Offset && i < p.Offset + p.StockBytes.Length)));
        check("live bags: the slot count is read from the bag's field",
            edited.AsSpan(0x305ED0, 7).SequenceEqual(Convert.FromHexString("8B81E0030000" + stock[0x305ED6].ToString("X2"))));
        check("live bags: the file it replaced is kept", Backups().Length == 1 && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("live bags: a client that's set up is left alone", !LiveBagSlotsService.Ensure(client, exe) && !LiveBagSlotsService.TryEnsure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1);

        // With the character limit and the guild bank, any order gives the same file.
        File.WriteAllBytes(exe, stock);
        var bagsLast = CharacterListLimitService.Ensure(client, exe) && GuildBankSlotsService.Ensure(client, exe)
            && LiveBagSlotsService.TryEnsure(client, exe);
        var all = File.ReadAllBytes(exe);
        File.WriteAllBytes(exe, stock);
        var bagsFirst = LiveBagSlotsService.Ensure(client, exe) && GuildBankSlotsService.Ensure(client, exe)
            && CharacterListLimitService.Ensure(client, exe);
        check("live bags: with the character limit and the guild bank, either order gives the same file", bagsLast && bagsFirst
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(all) && LiveBagSlotsService.IsSetUp(exe) && GuildBankSlotsService.IsSetUp(exe));

        // Anything else is refused by Ensure and IsSetUp, left alone by TryEnsure, and never changed.
        var foreign = Path.Combine(client, "Other.exe");
        bool Refused(byte[] bytes)
        {
            File.WriteAllBytes(foreign, bytes);
            var ensured = false;
            var asked = false;
            try { LiveBagSlotsService.Ensure(client, foreign); } catch (InvalidDataException) { ensured = true; }
            try { LiveBagSlotsService.IsSetUp(foreign); } catch (InvalidDataException) { asked = true; }
            return ensured && asked && !LiveBagSlotsService.TryEnsure(client, foreign) && LiveBagSlotsService.IsSetUpOrRefused(foreign)
                && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(bytes);
        }
        var partly = (byte[])stock.Clone();
        LiveBagSlotsService.Places[0].EditedBytes.CopyTo(partly, LiveBagSlotsService.Places[0].Offset);
        check("live bags: a partly edited client is refused", Refused(partly));
        var otherCode = (byte[])stock.Clone();
        otherCode[0x305DEE] ^= 0x01; // mov eax, [ecx+760h] at 0x7069ED
        check("live bags: different code at a place is refused", Refused(otherCode));
        check("live bags: a truncated file is refused", Refused(stock.AsSpan(0, 0x305E00).ToArray()));
        check("live bags: a PE with another image base is refused", Refused(GuildBankStandInClient(imageBase: 0x10000000)));
        check("live bags: a PE whose .text sits elsewhere in the file is refused", Refused(GuildBankStandInClient(textRaw: 0x600)));
        check("live bags: a file that isn't a PE is refused", Refused(Encoding.ASCII.GetBytes("not a client").Concat(new byte[0x310400]).ToArray()));
    }

    /// <summary>`live-bag-exe Wow.exe`: applies the edit to a copy next to it and prints both SHA-256s.</summary>
    private static int RunLiveBagExe(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: live-bag-exe <Wow.exe>"); return 2; }
        var source = Path.GetFullPath(args[1]);
        var root = Path.Combine(Path.GetTempPath(), "pk-live-bags-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Wow.exe");
        File.Copy(source, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  " + source);
        var changed = LiveBagSlotsService.Ensure(root, exe);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() + "  edited" + (changed ? "" : " (already)"));
        Directory.Delete(root, true);
        return 0;
    }
}
