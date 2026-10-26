using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: sets the character select screen up for the realm's character limit and order.
/// Two one-byte edits, always the same file:
///
/// - More than 10 characters. The stock client (build 12340) checks the count in its SMSG_CHAR_ENUM
///   handler (0x464C10): <c>cmp byte [ebp-1], 0x0a</c> / <c>jbe</c>. With more than 10 it empties the
///   whole list, and the player sees no characters at all. Everything after that grows with the list
///   (up to 255), so the <c>jbe</c> (0x76) becomes a <c>jmp</c> (0xEB).
/// - The order CVar. A <c>SET name "value"</c> line in Config.wtf for a CVar the client doesn't know
///   makes a placeholder: the console's set command registers it with "unregistered" = 1
///   (<c>push 1</c> at 0x76830B, the call to CVar::Register at 0x767FC0), and GetCVar / SetCVar skip
///   placeholders, so the character select screen can't read or write evermoreCharOrder. Pushing 0
///   registers it as a real CVar. Nothing in the client looks a CVar up to decide whether to register it.
///
/// Only those exact places are changed: the PE has to map each to .text where build 12340 has it and
/// the bytes around it have to be the stock or the edited ones, else the file is refused.
/// </summary>
public static class CharacterListLimitService
{
    /// <param name="GuardOffset">Where the guard bytes start in the file.</param>
    /// <param name="GuardRva">The same place in the image.</param>
    /// <param name="Stock">The guard bytes in the stock client.</param>
    /// <param name="ByteOffset">The byte that changes, in the file.</param>
    /// <param name="Edited">What it becomes.</param>
    public sealed record Edit(string Name, int GuardOffset, int GuardRva, byte[] Stock, int ByteOffset, byte Edited);

    /// <summary><c>cmp byte [ebp-1],0x0a</c>, <c>mov dword [ebp-8],0</c>, <c>jbe</c> (0x76) → <c>jmp</c> (0xEB).</summary>
    public static readonly Edit CharacterLimit = new("character limit", 0x6404C, 0x64C4C,
        Convert.FromHexString("807DFF0AC745F800000000760B"), 0x64057, 0xEB);

    /// <summary>The console set command's <c>push 0, push 0, push 1, push 5</c>: the 1 ("unregistered") → 0.</summary>
    public static readonly Edit RegisteredCVars = new("registered CVars", 0x367707, 0x368307,
        Convert.FromHexString("6A006A006A016A05"), 0x36770C, 0x00);

    public static readonly Edit[] Edits = [CharacterLimit, RegisteredCVars];

    /// <summary>
    /// True when <paramref name="executable"/> has both edits. Throws when the file isn't a client this
    /// knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Makes <paramref name="executable"/> (inside the client <paramref name="root"/>) list more than
    /// 10 characters and keep the order CVar. Returns true when it changed the file, false when it
    /// already had both edits. The file it replaces is kept under .portalkeeper/backups
    /// (<see cref="ClientExecutableEdit"/>). Throws when the file isn't a client this knows how to edit;
    /// a failure leaves the file as it was.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        var edited = Edited(original, Path.GetFileName(executable));
        if (edited is null) return false;

        ClientExecutableEdit.Replace(root, executable, original, edited);
        return true;
    }

    /// <summary>The file with every edit made, or null when it has them all already.</summary>
    internal static byte[]? Edited(byte[] original, string name)
    {
        var headers = Headers(original, name);
        foreach (var edit in Edits) CheckGuard(original, headers, edit, name);
        var missing = Edits.Where(e => original[e.ByteOffset] != e.Edited).ToArray();
        if (missing.Length == 0) return null;
        var bytes = (byte[])original.Clone();
        foreach (var edit in missing) bytes[edit.ByteOffset] = edit.Edited;
        return bytes;
    }

    private static InvalidDataException Refused(string name) =>
        new(name + " isn't a 3.3.5a client that can be set up for the realm's characters.");

    private static PEHeaders Headers(byte[] bytes, string name)
    {
        PEHeaders headers;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            headers = pe.PEHeaders;
        }
        catch (BadImageFormatException) { throw Refused(name); }
        if (headers.CoffHeader.Machine != Machine.I386 || headers.PEHeader?.Magic != PEMagic.PE32 ||
            headers.PEHeader.ImageBase != 0x400000)
            throw Refused(name);
        return headers;
    }

    /// <summary>Throws unless the image maps the guard where build 12340 has it, in its stock or edited form.</summary>
    private static void CheckGuard(byte[] bytes, PEHeaders headers, Edit edit, string name)
    {
        var length = edit.Stock.Length;
        var matches = headers.SectionHeaders.Where(s => edit.GuardRva >= s.VirtualAddress &&
            (long)edit.GuardRva + length <= (long)s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)).ToArray();
        if (matches.Length != 1 || matches[0].Name != ".text") throw Refused(name);
        var section = matches[0];
        var delta = (long)edit.GuardRva - section.VirtualAddress;
        var offset = (long)section.PointerToRawData + delta;
        if (delta + length > section.SizeOfRawData || offset != edit.GuardOffset || offset + length > bytes.Length)
            throw Refused(name);

        // Everything but the edited byte as in the stock client, and that one stock or edited.
        var guard = bytes.AsSpan(edit.GuardOffset, length);
        var changed = edit.ByteOffset - edit.GuardOffset;
        for (var i = 0; i < length; i++)
        {
            if (i == changed ? guard[i] != edit.Stock[i] && guard[i] != edit.Edited : guard[i] != edit.Stock[i])
                throw Refused(name);
        }
    }
}
