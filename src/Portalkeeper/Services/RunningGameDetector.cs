using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Portalkeeper.Services;

/// <summary>
/// Finds a game running from a client folder, whoever started it: a shortcut, or a launcher that has
/// since closed. It goes by the path the process runs from, so a Wow.exe in another folder doesn't count.
/// A copy of the game under another file name isn't seen.
/// </summary>
internal static class RunningGameDetector
{
    private const string DefaultExecutable = "Wow.exe";

    /// <summary>
    /// True when <paramref name="executable"/> in one of the folders is running, false when it isn't,
    /// and null when that couldn't be found out. Never throws: a failed look never stops the launcher.
    /// </summary>
    public static bool? IsRunningFrom(IEnumerable<string> folders, string? executable)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(executable) ? DefaultExecutable : executable;
            var targets = Targets(folders, name);
            if (targets.Count == 0) return false;
            if (OperatingSystem.IsWindows()) return ScanWindows(targets, name);
            if (OperatingSystem.IsLinux()) return ScanProc("/proc", targets, name);
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Portalkeeper: looking for a running game failed: " + ex.Message);
            return null;
        }
    }

    internal static HashSet<string> Targets(IEnumerable<string> folders, string executable) =>
        folders.Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => RealPath(Path.Combine(folder, executable)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The path with every symbolic link and junction followed, so a client reached through a link is
    /// still the same client. Case is left to the callers: Wine finds a file whatever its case.
    /// </summary>
    internal static string RealPath(string path, int depth = 0)
    {
        var full = Path.GetFullPath(path);
        if (depth > 16) return full; // a loop of links
        var current = Path.GetPathRoot(full) ?? "";
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            var target = LinkTarget(next);
            current = target is null ? next : RealPath(target, depth + 1);
        }
        return current;
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            return (Directory.Exists(path) ? Directory.ResolveLinkTarget(path, true) : File.ResolveLinkTarget(path, true))?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------------------------------------------------------
    // Linux: the game runs under Wine, so its process is Wine's and only the command line names the game
    // ---------------------------------------------------------

    /// <summary>
    /// Goes through a /proc tree for a process running one of the targets. Null without a /proc to read.
    /// A process that can't be read (it exited, or it's another user's) doesn't count.
    /// </summary>
    internal static bool? ScanProc(string procRoot, IReadOnlySet<string> targets, string executable)
    {
        if (!Directory.Exists(procRoot)) return null;
        foreach (var process in Directory.EnumerateDirectories(procRoot))
        {
            if (!Path.GetFileName(process).All(char.IsDigit)) continue;
            try
            {
                var program = ProgramOf(File.ReadAllBytes(Path.Combine(process, "cmdline")));
                if (program is null || !FileNameOf(program).Equals(executable, StringComparison.OrdinalIgnoreCase)) continue;
                if (UnixPathOf(program, process) is { } path && targets.Contains(RealPath(path))) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return false;
    }

    /// <summary>
    /// What a process runs: its first argument, or the first one after Wine's own programs. Wine takes
    /// itself out of the command line once it's up, and until then it's "wine Wow.exe". Later arguments
    /// don't count: "sha256sum Wow.exe" isn't the game.
    /// </summary>
    internal static string? ProgramOf(byte[] commandLine)
    {
        foreach (var argument in Encoding.UTF8.GetString(commandLine).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!FileNameOf(argument).StartsWith("wine", StringComparison.OrdinalIgnoreCase)) return argument.Trim().Trim('"');
        }
        return null;
    }

    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>The file a Windows path, Unix path or relative path on a Wine command line means.</summary>
    private static string? UnixPathOf(string program, string process)
    {
        if (program.Length > 2 && char.IsAsciiLetter(program[0]) && program[1] == ':' && program[2] is '\\' or '/')
        {
            var drive = WineDriveRoot(char.ToLowerInvariant(program[0]), process);
            return drive is null ? null : Path.Combine(drive, program[3..].Replace('\\', '/'));
        }
        if (program.StartsWith('/')) return program;
        // Relative: Wine starts a program with its working directory set to the Windows one.
        var cwd = Directory.ResolveLinkTarget(Path.Combine(process, "cwd"), false)?.FullName;
        return cwd is null ? null : Path.Combine(cwd, program.Replace('\\', '/'));
    }

    /// <summary>Where a Wine drive letter points in the process's prefix. Z: is / unless the prefix says otherwise.</summary>
    private static string? WineDriveRoot(char drive, string process)
    {
        var link = Path.Combine(WinePrefixOf(process), "dosdevices", drive + ":");
        var target = LinkTarget(link);
        return target ?? (drive == 'z' ? "/" : null);
    }

    private static string WinePrefixOf(string process)
    {
        var environment = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(process, "environ"))).Split('\0');
        string? Value(string name) => environment.FirstOrDefault(e => e.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        if (Value("WINEPREFIX") is { Length: > 0 } prefix) return prefix;
        var home = Value("HOME") is { Length: > 0 } theirs ? theirs : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".wine");
    }

    // ---------------------------------------------------------
    // Windows: ask for the file the process was started from
    // ---------------------------------------------------------

    private static bool ScanWindows(IReadOnlySet<string> targets, string executable)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try
        {
            return processes.Any(process => WindowsImagePath(process.Id) is { } image && targets.Contains(RealPath(image)));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    // Not Process.MainModule: it needs more access than a shortcut set to run as administrator gives,
    // and this launcher is 64-bit while Wow.exe is 32-bit. Limited query access is enough for this call.
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static string? WindowsImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32767);
            var length = path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Turns looks at the process list into whether a game holds the client. Seen once is enough to hold it.
/// Letting go takes two looks in a row that don't find it, because Wine can hand over between processes
/// and writing under a game is the expensive mistake. Looks that fail keep the hold for a while, but not
/// for good: a process list that stays unreadable mustn't keep the client from updating forever.
/// </summary>
internal sealed class RunningGameTracker
{
    internal const int MissesToRelease = 2;
    internal const int FailuresToRelease = 12;

    private int _misses;
    private int _failures;

    public bool Running { get; private set; }

    public bool Observe(bool? seen)
    {
        if (seen == true)
        {
            Running = true;
            _misses = 0;
            _failures = 0;
        }
        else if (seen == false)
        {
            _failures = 0;
            if (++_misses >= MissesToRelease) Running = false;
        }
        else if (++_failures >= FailuresToRelease)
        {
            Running = false;
        }
        if (!Running)
        {
            _misses = 0;
            _failures = 0;
        }
        return Running;
    }
}
