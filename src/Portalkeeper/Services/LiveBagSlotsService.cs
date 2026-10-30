using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: lets the client see a bag's size change while the player is in the world (mod-legacy's
/// Deep Pockets raises CONTAINER_FIELD_NUM_SLOTS of the bags in the four bag slots).
///
/// The same bytes as wow-server's tools/client-patches/live_bag_slots.py, 2 places in build 12340's .text.
/// A stock client reads a bag's size once, in CGContainer_C's constructor, and keeps a copy (+0x760); a
/// bag sent again isn't built again (the client re-enables the object it had). GetContainerNumSlots'
/// accessor (0x706AD0) and the free-slot count (0x7069ED) read the bag's own field instead.
///
/// Like the warband edit this one is optional: a stock client is safe on the realm and sees the new size
/// after a relog, so a Wow.exe it can't edit still launches.
/// Every place has to hold its stock or its edited bytes, all the same way, and the PE has to map each
/// where build 12340 has it in .text; anything else is refused and the file is left as it was.
/// </summary>
public static class LiveBagSlotsService
{
    /// <summary>File offset and RVA of a place, its stock bytes and its edited bytes.</summary>
    internal sealed record Place(int Offset, int Rva, string Stock, string Edited)
    {
        public byte[] StockBytes { get; } = Convert.FromHexString(Stock);
        public byte[] EditedBytes { get; } = Convert.FromHexString(Edited);
    }

    // From live_bag_slots.EDITS (VA - 0x400C00 = file offset, VA - 0x400000 = RVA):
    // lea eax, [ecx+760h] -> mov eax, [ecx+3E0h] (0x706AD0); mov eax, [ecx+760h] -> mov eax, [edx+0] (0x7069ED).
    internal static readonly Place[] Places =
    [
        new(0x305ED0, 0x306AD0, "8D8160070000", "8B81E0030000"),
        new(0x305DED, 0x3069ED, "8B8160070000", "8B8200000000"),
    ];

    /// <summary>
    /// True when <paramref name="executable"/> already reads bag sizes live. Throws when the file isn't a
    /// client this knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Makes <paramref name="executable"/> (inside the client <paramref name="root"/>) read bag sizes live.
    /// Returns true when it changed the file, false when it did already. The file it replaces is kept
    /// under .portalkeeper/backups (<see cref="ClientExecutableEdit"/>). Throws when the file isn't a
    /// client this knows how to edit; a failure leaves the file as it was.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        var edited = Edited(original, Path.GetFileName(executable));
        if (edited is null) return false;

        ClientExecutableEdit.Replace(root, executable, original, edited);
        return true;
    }

    /// <summary><see cref="IsSetUp"/>, except that a client it can't edit counts as done.</summary>
    public static bool IsSetUpOrRefused(string executable)
    {
        try { return IsSetUp(executable); }
        catch (InvalidDataException) { return true; }
    }

    /// <summary>
    /// <see cref="Ensure"/>, except that a client it can't edit is left alone (false) rather than refused.
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
        var refused = new InvalidDataException(name + " isn't a 3.3.5a client that can be set up for bag sizes that change.");
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
