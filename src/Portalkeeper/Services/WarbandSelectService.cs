using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: lets the character select screen show a warband camp (patch-z20's
/// EvermoreWarband.lua): several characters and their pets in one scene, and each character's era.
///
/// Two edits to build 12340, the same bytes as wow-server's tools/client-patches/character_select_warband.py
/// (112 bytes; the new code goes in .zdata, a 4 KB section that is all zeros in this exe):
/// <list type="bullet">
/// <item><c>SelectCharacter(index[, spot])</c>: the character goes on scene attachment 2 * spot, its pet on
/// 2 * spot + 1, and only those are cleared first. Five calls go through three stubs; no spot is the stock
/// behaviour.</item>
/// <item><c>GetCharacterInfo</c> returns an 11th value: the era (and warband camp) the realm sends in the
/// last bag slot.</item>
/// </list>
/// Every place has to hold its stock or its edited bytes, all the same way, and the PE has to map each where
/// build 12340 has it; anything else is refused and the file is left as it was.
/// </summary>
public static class WarbandSelectService
{
    /// <summary>File offset and RVA of a place, its stock bytes and its edited bytes (the same when it is only checked).</summary>
    internal sealed record Place(int Offset, int Rva, string Stock, string Edited)
    {
        public byte[] StockBytes { get; } = Convert.FromHexString(Stock);
        public byte[] EditedBytes { get; } = Convert.FromHexString(Edited);
        public bool Changes => Stock != Edited;
        public string Section => Rva < ZdataRva ? ".text" : ".zdata";
    }

    public const int ZdataRva = 0x9D1000;

    // Generated from character_select_warband.edits(): the call sites in SelectCharacter (0x4E45AC) and the
    // model build (detach 0x4E3D1C/0x4E3D3E, attach 0x4E4469/0x4E44AC), GetCharacterInfo's epilogue
    // (0x4E332D, and the 9 bytes after it, which stay), the stubs at 0xDD1E00/0xDD1E40/0xDD1E60/0xDD1F00
    // and the spot variable at 0xDD1FFC (stays 0).
    internal static readonly Place[] Places =
    [
        new(0xE39AC, 0xE45AC, "E87F9A3600", "E84FD88E00"),
        new(0xE311C, 0xE3D1C, "E83F383400", "E81FE18E00"),
        new(0xE313E, 0xE3D3E, "E81D383400", "E8FDE08E00"),
        new(0xE3869, 0xE4469, "E8C2D13400", "E8F2D98E00"),
        new(0xE38AC, 0xE44AC, "E87FD13400", "E8AFD98E00"),
        new(0xE272D, 0xE332D, "83C42C5EB8", "E9CEEB8E00"),
        new(0xE2732, 0xE3332, "0A0000005F8BE55DC3", "0A0000005F8BE55DC3"),
        new(0x72DC00, 0x9D1E00, "00000000000000000000000000000000000000000000000000000000000000",
            "6A0256E828C2A7FF83C408E8B09BABFF83E00F01C0A3FC1FDD00E911C2A7FF"),
        new(0x72DC40, 0x9D1E40, "0000000000000000000000000000", "A1FC1FDD0001442404E91257A5FF"),
        new(0x72DC60, 0x9D1E60, "0000000000000000000000000000", "A1FC1FDD0001442408E9C2F7A5FF"),
        new(0x72DD00, 0x9D1F00, "0000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "83C42C8B86600100008945FCDB45FC83EC08DD1C2457E885C3A7FF83C40C5EB80B0000005F8BE55DC3"),
        new(0x72DDFC, 0x9D1FFC, "00000000", "00000000"),
    ];

    /// <summary>
    /// True when <paramref name="executable"/> already has the warband edits. Throws when the file isn't a
    /// client this knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Gives <paramref name="executable"/> (inside the client <paramref name="root"/>) the warband edits.
    /// Returns true when it changed the file, false when it had them already. The file it replaces is kept
    /// under .portalkeeper/backups (<see cref="ClientExecutableEdit"/>). Throws when the file isn't a client
    /// this knows how to edit; a failure leaves the file as it was.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        var edited = Edited(original, Path.GetFileName(executable));
        if (edited is null) return false;

        ClientExecutableEdit.Replace(root, executable, original, edited);
        return true;
    }

    /// <summary>
    /// True when <paramref name="executable"/> has the edits or can't have them (not a client this knows):
    /// the camp is only a look, and a client it can't edit keeps the stock screen and still launches.
    /// </summary>
    public static bool IsSetUpOrRefused(string executable)
    {
        try { return IsSetUp(executable); }
        catch (InvalidDataException) { return true; }
    }

    /// <summary>
    /// <see cref="Ensure"/>, except that a client it can't edit is left alone (false) rather than refused.
    /// A failure putting the file in place still throws.
    /// </summary>
    public static bool TryEnsure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        byte[]? edited;
        try { edited = Edited(original, Path.GetFileName(executable)); }
        catch (InvalidDataException) { return false; }
        if (edited is null) return false;

        ClientExecutableEdit.Replace(root, executable, original, edited);
        return true;
    }

    /// <summary>The file with the edits, or null when it has them already.</summary>
    internal static byte[]? Edited(byte[] original, string name)
    {
        var refused = new InvalidDataException(name + " isn't a 3.3.5a client that can be set up for the warband camp.");
        CheckLayout(original, refused);
        bool? edited = null;
        foreach (var place in Places)
        {
            var found = original.AsSpan(place.Offset, place.StockBytes.Length);
            var isStock = found.SequenceEqual(place.StockBytes);
            var isEdited = found.SequenceEqual(place.EditedBytes);
            if (!isStock && !isEdited) throw refused;
            if (!place.Changes) continue;
            // Partly edited: some places stock, some edited. Not touched.
            if (edited is bool state && state != isEdited) throw refused;
            edited = isEdited;
        }
        if (edited == true) return null;

        var bytes = (byte[])original.Clone();
        foreach (var place in Places) place.EditedBytes.CopyTo(bytes, place.Offset);
        return bytes;
    }

    /// <summary>Throws unless the image maps every place where build 12340 has it, in the section it belongs to.</summary>
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
            if (matches.Length != 1 || matches[0].Name != place.Section) throw refused;
            var section = matches[0];
            var delta = (long)place.Rva - section.VirtualAddress;
            if (delta + length > section.SizeOfRawData || section.PointerToRawData + delta != place.Offset ||
                place.Offset + length > bytes.Length)
                throw refused;
        }
    }
}
