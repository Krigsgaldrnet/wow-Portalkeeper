using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Portalkeeper.Services;

/// <summary>
/// Evermore fork: lets the character select screen list more than 10 characters on the realm.
///
/// The stock client (build 12340) checks the count in its SMSG_CHAR_ENUM handler (0x464C10):
/// <c>cmp byte [ebp-1], 0x0a</c> / <c>jbe</c>. With more than 10 it empties the whole list, and the
/// player sees no characters at all. Everything after that grows with the list (up to 255), so turning
/// the <c>jbe</c> (0x76) into a <c>jmp</c> (0xEB) is the whole edit: one byte, always the same file.
///
/// Only that exact place is changed: the PE has to map it to .text where build 12340 has it and the
/// bytes around it have to be the stock or the edited ones, else the file is refused.
/// </summary>
public static class CharacterListLimitService
{
    /// <summary>Where the guard starts: <c>cmp byte [ebp-1],0x0a</c>, then <c>mov dword [ebp-8],0</c>, then the jump.</summary>
    public const int GuardOffset = 0x6404C;
    public const int GuardRva = 0x64C4C;
    /// <summary>The jump's opcode byte.</summary>
    public const int JumpOffset = 0x64057;
    public const byte StockJump = 0x76; // jbe rel8
    public const byte EditedJump = 0xEB; // jmp rel8

    private static readonly byte[] StockGuard = Convert.FromHexString("807DFF0AC745F800000000760B");

    /// <summary>
    /// True when <paramref name="executable"/> already lists more than 10 characters. Throws when the
    /// file isn't a client this knows how to edit.
    /// </summary>
    public static bool IsSetUp(string executable) =>
        Edited(File.ReadAllBytes(executable), Path.GetFileName(executable)) is null;

    /// <summary>
    /// Makes <paramref name="executable"/> (inside the client <paramref name="root"/>) list more than
    /// 10 characters. Returns true when it changed the file, false when it already did. The file it
    /// replaces is kept under .portalkeeper/backups (<see cref="ClientExecutableEdit"/>). Throws when
    /// the file isn't a client this knows how to edit; a failure leaves the file as it was.
    /// </summary>
    public static bool Ensure(string root, string executable)
    {
        var original = File.ReadAllBytes(executable);
        var edited = Edited(original, Path.GetFileName(executable));
        if (edited is null) return false;

        ClientExecutableEdit.Replace(root, executable, original, edited);
        return true;
    }

    /// <summary>The file with the jump edited, or null when it has it already.</summary>
    internal static byte[]? Edited(byte[] original, string name)
    {
        CheckGuard(original, name);
        if (original[JumpOffset] == EditedJump) return null;
        var bytes = (byte[])original.Clone();
        bytes[JumpOffset] = EditedJump;
        return bytes;
    }

    /// <summary>Throws unless the image maps the guard where build 12340 has it, in its stock or edited form.</summary>
    private static void CheckGuard(byte[] bytes, string name)
    {
        var refused = new InvalidDataException(name + " isn't a 3.3.5a client that can be set up for more than 10 characters.");
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
        var matches = headers.SectionHeaders.Where(s => GuardRva >= s.VirtualAddress &&
            (long)GuardRva + StockGuard.Length <= (long)s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)).ToArray();
        if (matches.Length != 1 || matches[0].Name != ".text") throw refused;
        var section = matches[0];
        var delta = (long)GuardRva - section.VirtualAddress;
        var offset = (long)section.PointerToRawData + delta;
        if (delta + StockGuard.Length > section.SizeOfRawData || offset != GuardOffset || offset + StockGuard.Length > bytes.Length)
            throw refused;

        // The guard in either form: everything but the jump's opcode, and that one stock or edited.
        var guard = bytes.AsSpan(GuardOffset, StockGuard.Length);
        var jumpAt = JumpOffset - GuardOffset;
        for (var i = 0; i < StockGuard.Length; i++)
        {
            if (i == jumpAt ? guard[i] is not (StockJump or EditedJump) : guard[i] != StockGuard[i])
                throw refused;
        }
    }
}
