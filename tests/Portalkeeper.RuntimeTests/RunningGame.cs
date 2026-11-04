using System.Text;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// A game the launcher didn't start: RunningGameDetector against a stand-in /proc tree, and what the
// launcher makes of its looks. The Windows call and a real /proc aren't run by these checks.
internal static partial class Program
{
    private static async Task RunningGameChecks(string root, Action<string, bool> check)
    {
        var top = RunningGameDetector.RealPath(Path.Combine(root, "running-game"));
        var client = Path.Combine(top, "home", "me", "Games", "wow client");
        var otherClient = Path.Combine(top, "home", "me", "Games", "other");
        Directory.CreateDirectory(client);
        Directory.CreateDirectory(otherClient);
        File.WriteAllText(Path.Combine(client, "Wow.exe"), "game");
        File.WriteAllText(Path.Combine(otherClient, "Wow.exe"), "game");
        var targets = RunningGameDetector.Targets([client], "Wow.exe");

        var proc = Path.Combine(top, "proc");
        var pid = 100;
        // Each case gets a tree of its own, with the launcher itself and a kernel thread in it.
        bool? Scan(params string[][] commandLines) => ScanWith(null, null, commandLines);
        bool? ScanWith(string? cwd, string[]? environment, params string[][] commandLines)
        {
            if (Directory.Exists(proc)) Directory.Delete(proc, true);
            Directory.CreateDirectory(Path.Combine(proc, "self"));
            File.WriteAllText(Path.Combine(proc, "cpuinfo"), "not a process");
            FakeProcess(pid++, ["/usr/bin/dotnet", "/opt/Portalkeeper/Portalkeeper.dll"]);
            FakeProcess(pid++, []);
            foreach (var commandLine in commandLines) FakeProcess(pid++, commandLine);
            return RunningGameDetector.ScanProc(proc, targets, "Wow.exe");

            void FakeProcess(int id, string[] commandLine)
            {
                var folder = Path.Combine(proc, id.ToString());
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "cmdline"), Encoding.UTF8.GetBytes(string.Concat(commandLine.Select(a => a + "\0"))));
                File.WriteAllBytes(Path.Combine(folder, "environ"), Encoding.UTF8.GetBytes(string.Concat((environment ?? []).Select(e => e + "\0"))));
                if (cwd is not null) Directory.CreateSymbolicLink(Path.Combine(folder, "cwd"), cwd);
            }
        }

        var exe = Path.Combine(client, "Wow.exe");
        check("running game: nothing running", Scan() == false);
        check("running game: the client's Wow.exe by its Unix path", Scan([exe]) == true);
        check("running game: as wine's argument, before Wine takes itself out", Scan(["/usr/bin/wine", exe, "-opengl"]) == true);
        check("running game: through Wine's preloader", Scan(["/usr/bin/wine64-preloader", "/usr/lib/wine/wine64", exe]) == true);
        check("running game: a path whose case differs", Scan([exe.Replace("wow client", "WOW Client").Replace("Wow.exe", "WOW.EXE")]) == true);
        check("running game: Wow.exe in another folder doesn't count", Scan([Path.Combine(otherClient, "Wow.exe")]) == false);
        check("running game: another program in the client folder doesn't count", Scan([Path.Combine(client, "Repair.exe")]) == false);
        check("running game: a program with the game as an argument doesn't count", Scan(["sha256sum", exe], ["/usr/bin/vim", exe]) == false);
        check("running game: wine with nothing after it doesn't count", Scan(["/usr/bin/wineserver"]) == false);

        // A relative path goes by the process's working directory.
        check("running game: relative, from the client folder", ScanWith(client, null, ["./Wow.exe"]) == true);
        check("running game: relative with a Windows separator", ScanWith(Path.GetDirectoryName(client), null, ["wow client\\Wow.exe"]) == true);
        check("running game: relative, from another folder", ScanWith(otherClient, null, ["Wow.exe"]) == false);
        check("running game: relative, with no working directory to read", Scan(["Wow.exe"]) == false);

        // Windows paths go through the process's Wine prefix.
        var prefix = Path.Combine(top, "prefix");
        var dosdevices = Path.Combine(prefix, "dosdevices");
        var driveC = Path.Combine(prefix, "drive_c");
        var prefixClient = Path.Combine(driveC, "Games", "WoW");
        Directory.CreateDirectory(dosdevices);
        Directory.CreateDirectory(prefixClient);
        File.WriteAllText(Path.Combine(prefixClient, "Wow.exe"), "game");
        Directory.CreateSymbolicLink(Path.Combine(dosdevices, "c:"), "../drive_c");
        Directory.CreateSymbolicLink(Path.Combine(dosdevices, "z:"), "/");
        var inPrefix = new[] { "WINEPREFIX=" + prefix };
        var zPath = "Z:" + exe.Replace('/', '\\');
        check("running game: Wine's Z: path", ScanWith(null, inPrefix, [zPath]) == true);
        check("running game: Wine's Z: path in lower case", ScanWith(null, inPrefix, ["z:" + exe.Replace('/', '\\').ToLowerInvariant()]) == true);
        check("running game: Z: is the root without a prefix to ask", ScanWith(null, ["HOME=" + Path.Combine(top, "nowhere")], [zPath]) == true);
        check("running game: a C: path that isn't this client", ScanWith(null, inPrefix, ["C:\\Games\\WoW\\Wow.exe"]) == false);
        var targetsBefore = targets;
        targets = RunningGameDetector.Targets([prefixClient], "Wow.exe");
        check("running game: a client inside the prefix by its C: path", ScanWith(null, inPrefix, ["C:\\Games\\WoW\\Wow.exe"]) == true);
        check("running game: a drive the prefix doesn't have", ScanWith(null, inPrefix, ["D:\\Games\\WoW\\Wow.exe"]) == false);
        var home = Path.Combine(top, "theirhome");
        Directory.CreateDirectory(home);
        Directory.CreateSymbolicLink(Path.Combine(home, ".wine"), prefix);
        check("running game: the default prefix under the process's HOME", ScanWith(null, ["HOME=" + home], ["C:\\Games\\WoW\\Wow.exe"]) == true);
        targets = targetsBefore;

        // A client reached through a link is the same client.
        var linkedClient = Path.Combine(top, "linked");
        Directory.CreateSymbolicLink(linkedClient, client);
        check("running game: the launcher's path goes through a link",
            RunningGameDetector.Targets([linkedClient], "Wow.exe").SetEquals(targets));
        check("running game: the game's path goes through a link", Scan([Path.Combine(linkedClient, "Wow.exe")]) == true);

        // Looks that can't be made.
        check("running game: no /proc is 'couldn't tell', not an error",
            RunningGameDetector.ScanProc(Path.Combine(top, "no-proc"), targets, "Wow.exe") is null);
        check("running game: no folders to look in", RunningGameDetector.IsRunningFrom([], "Wow.exe") == false);
        check("running game: a bad folder never throws",
            RunningGameDetector.IsRunningFrom(["No client installation configured.", "", "bad\0path"], "Wow.exe") is null or false);
        check("running game: a loop of links never hangs", LoopOfLinks(top));

        // What the launcher makes of its looks.
        var tracker = new RunningGameTracker();
        check("hold: starts off", !tracker.Observe(false) && !tracker.Observe(null));
        check("hold: one sighting holds", tracker.Observe(true));
        check("hold: one miss doesn't let go (Wine can hand over)", tracker.Observe(false));
        check("hold: a sighting starts the count again", tracker.Observe(true) && tracker.Observe(false));
        check("hold: two misses in a row let go", !tracker.Observe(false));
        tracker.Observe(true);
        var kept = Enumerable.Range(1, RunningGameTracker.FailuresToRelease - 1).All(_ => tracker.Observe(null));
        check("hold: failed looks keep it for a while", kept);
        check("hold: failed looks don't keep it for good", !tracker.Observe(null));
        check("hold: a failed look never starts one", !tracker.Observe(null));
        tracker.Observe(true);
        tracker.Observe(false);
        check("hold: a failed look between misses doesn't let go", tracker.Observe(null));

        // A patch whose download finished after the game was seen stays out of the client.
        var patchClient = Path.Combine(top, "patch-client");
        var patchData = Path.Combine(patchClient, "Data");
        Directory.CreateDirectory(patchData);
        var installed = Path.Combine(patchData, "patch-z.MPQ");
        File.WriteAllText(installed, "the old patch");
        var newBytes = Encoding.UTF8.GetBytes("the new patch");
        var patch = new PatchDefinition
        {
            Id = "z",
            Name = "z",
            FileName = "patch-z.MPQ",
            InstallDirectory = "Data",
            InstallMode = PatchInstallMode.File,
            SourceType = "HTTP",
            SourceUrl = "https://example.invalid/patch-z.MPQ",
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(newBytes)),
        };
        using var stop = new CancellationTokenSource();
        var patches = new PatchService
        {
            Downloader = async (_, temp) =>
            {
                await File.WriteAllBytesAsync(temp, newBytes);
                stop.Cancel();
                return true;
            }
        };
        var stopped = false;
        try { await patches.InstallAsync(patchClient, patch, null, stop.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check("hold: a patch install stops", stopped);
        check("hold: the client's patch is left as it was", File.ReadAllText(installed) == "the old patch");
        check("hold: nothing of the download is left beside it", Directory.GetFiles(patchData).Length == 1);
        patches.Downloader = async (_, temp) => { await File.WriteAllBytesAsync(temp, newBytes); return true; };
        await patches.InstallAsync(patchClient, patch);
        check("hold: the same install goes through once nothing stops it", File.ReadAllText(installed) == "the new patch");
    }

    private static bool LoopOfLinks(string folder)
    {
        var a = Path.Combine(folder, "loop-a");
        var b = Path.Combine(folder, "loop-b");
        File.CreateSymbolicLink(a, b);
        File.CreateSymbolicLink(b, a);
        return RunningGameDetector.IsRunningFrom([a], "Wow.exe") is not true;
    }
}
