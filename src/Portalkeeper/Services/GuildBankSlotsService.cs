using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: lets a guild bank tab hold 196 items instead of 98 (the realm's
/// Guild.BankSlotsPerTab; DragonUI's bank window shows the rows the realm says it has).
///
/// The same bytes as wow-server's tools/client-patches/guild_bank_slots.py, 30 places in build 12340's
/// .text: the tab's item array is sized 196 when the tab first arrives (SMSG_GUILD_BANK_LIST,
/// 0x5A7250), PickupGuildBankItem, AutoStoreGuildBankItem and SplitGuildBankItem check slots against
/// 196, and the item on the cursor is kept as tab * 256 + slot instead of tab * 98 + slot (every
/// <c>imul r, r, 62h</c> becomes <c>shl r, 8</c>, every divide by 98 a shift by 8).
///
/// Unlike the warband edit this one is required: a stock client on a realm with more than 98 slots
/// writes the extra items past the end of its array.
/// Every place has to hold its stock or its edited bytes, all the same way, and the PE has to map each
/// where build 12340 has it in .text; anything else is refused and the file is left as it was.
/// </summary>
public static class GuildBankSlotsService
{
    /// <summary>File offset and RVA of a place, its stock bytes and its edited bytes.</summary>
    internal sealed record Place(int Offset, int Rva, string Stock, string Edited)
    {
        public byte[] StockBytes { get; } = Convert.FromHexString(Stock);
        public byte[] EditedBytes { get; } = Convert.FromHexString(Edited);
    }

    // Generated from guild_bank_slots.EDITS (VA - 0x400C00 = file offset, VA - 0x400000 = RVA): the array
    // size (0x5A7320, 0x5A735C), the three Lua checks (0x5A62AF, 0x5A64B9, 0x5A66E1), then the cursor
    // number's divides (mov eax, 5397829Dh -> 1000000h; shr 5 -> 0) and multiplies (imul 62h -> shl 8).
    internal static readonly Place[] Places =
    [
        new(0x1A6720, 0x1A7320, "BB62000000", "BBC4000000"),
        new(0x1A675C, 0x1A735C, "C7460462000000", "C74604C4000000"),
        new(0x1A56AF, 0x1A62AF, "80FB62", "80FBC4"),
        new(0x1A58B9, 0x1A64B9, "3C62", "3CC4"),
        new(0x1A5AE1, 0x1A66E1, "3C62", "3CC4"),
        new(0x1A36E6, 0x1A42E6, "B89D829753", "B800000001"),
        new(0x1A36F0, 0x1A42F0, "C1EA05", "C1EA00"),
        new(0x1A36F5, 0x1A42F5, "6BD262", "C1E208"),
        new(0x1A4046, 0x1A4C46, "B89D829753", "B800000001"),
        new(0x1A404D, 0x1A4C4D, "C1EA05", "C1EA00"),
        new(0x1A4052, 0x1A4C52, "6BC062", "C1E008"),
        new(0x1A4096, 0x1A4C96, "B89D829753", "B800000001"),
        new(0x1A409D, 0x1A4C9D, "C1EA05", "C1EA00"),
        new(0x1A40A2, 0x1A4CA2, "6BC062", "C1E008"),
        new(0x1A40E6, 0x1A4CE6, "B89D829753", "B800000001"),
        new(0x1A40ED, 0x1A4CED, "C1EA05", "C1EA00"),
        new(0x1A40F2, 0x1A4CF2, "6BC062", "C1E008"),
        new(0x1A4981, 0x1A5581, "B89D829753", "B800000001"),
        new(0x1A498A, 0x1A558A, "C1EE05", "C1EE00"),
        new(0x1A498F, 0x1A558F, "6BD262", "C1E208"),
        new(0x1A4A09, 0x1A5609, "6BC062", "C1E008"),
        new(0x1A5572, 0x1A6172, "6BF662", "C1E608"),
        new(0x1A5744, 0x1A6344, "6BF662", "C1E608"),
        new(0x1A5AF4, 0x1A66F4, "6BF662", "C1E608"),
        new(0x1A689B, 0x1A749B, "B89D829753", "B800000001"),
        new(0x1A68A6, 0x1A74A6, "C1EA05", "C1EA00"),
        new(0x1A68AD, 0x1A74AD, "6BD262", "C1E208"),
        new(0x2DAC92, 0x2DB892, "B89D829753", "B800000001"),
        new(0x2DACA5, 0x2DB8A5, "C1E805", "C1E800"),
        new(0x2DACAD, 0x2DB8AD, "B262", "B200"),
    ];

    /// <summary>
    /// True when <paramref name="executable"/> already holds 196 slots a tab. Throws when the file isn't
    /// a client this knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Gives <paramref name="executable"/> (inside the client <paramref name="root"/>) 196 slots a tab.
    /// Returns true when it changed the file, false when it had them already. The file it replaces is
    /// kept under .portalkeeper/backups (<see cref="ClientExecutableEdit"/>). Throws when the file isn't
    /// a client this knows how to edit; a failure leaves the file as it was.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        var edited = Edited(original, Path.GetFileName(executable));
        if (edited is null) return false;

        ClientExecutableEdit.Replace(root, executable, original, edited);
        return true;
    }

    /// <summary>The file with the edits, or null when it has them already.</summary>
    internal static byte[]? Edited(byte[] original, string name)
    {
        var refused = new InvalidDataException(name + " isn't a 3.3.5a client that can be set up for bigger guild bank tabs.");
        CheckLayout(original, refused);
        bool? edited = null;
        foreach (var place in Places)
        {
            var found = original.AsSpan(place.Offset, place.StockBytes.Length);
            var isStock = found.SequenceEqual(place.StockBytes);
            var isEdited = found.SequenceEqual(place.EditedBytes);
            if (!isStock && !isEdited) throw refused;
            // Partly edited: some places stock, some edited. Not touched.
            if (edited is bool state && state != isEdited) throw refused;
            edited = isEdited;
        }
        if (edited == true) return null;

        var bytes = (byte[])original.Clone();
        foreach (var place in Places) place.EditedBytes.CopyTo(bytes, place.Offset);
        return bytes;
    }

    /// <summary>Throws unless the image maps every place where build 12340 has it, in .text.</summary>
    private static void CheckLayout(byte[] bytes, Exception refused)
    {
        PEHeaders headers;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            headers = pe.PEHeaders;
        }
        catch (BadImageFormatException) { throw refused; }
        if (headers.CoffHeader.Machine != Machine.I386 || headers.PEHeader?.Magic != PEMagic.PE32 ||
            headers.PEHeader.ImageBase != 0x400000)
            throw refused;
        foreach (var place in Places)
        {
            var length = place.StockBytes.Length;
            var matches = headers.SectionHeaders.Where(s => place.Rva >= s.VirtualAddress &&
                (long)place.Rva + length <= (long)s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)).ToArray();
            if (matches.Length != 1 || matches[0].Name != ".text") throw refused;
            var section = matches[0];
            var delta = (long)place.Rva - section.VirtualAddress;
            if (delta + length > section.SizeOfRawData || section.PointerToRawData + delta != place.Offset ||
                place.Offset + length > bytes.Length)
                throw refused;
        }
    }
}
