using System.IO.Compression;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Vaultrona fork: a failed install of a two-folder addon puts the player's old folders back and reports what really
// went wrong, and one update or uninstall is one backup set. Part of `RuntimeTests launcher`; `addon-rollback` runs
// them alone.
internal static partial class Program
{
    private static async Task<int> RunAddonRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-rollback-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failures++; }
        try { await AddonRollbackChecks(root, Check); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 2;
    }

    private static async Task AddonRollbackChecks(string root, Action<string, bool> check)
    {
        var installer = new AddonInstallerService(new HttpClient(new TwoFolderAddonServer()));
        var clients = 0;
        AddonDefinition Pair(string version) => new()
        {
            Id = "pair", Name = "Pair", Folder = "Alpha", Version = version,
            DownloadUrl = "https://fixture.invalid/" + version
        };
        static string AddOn(string client, string folder) => Path.Combine(client, "Interface", "AddOns", folder);
        static bool Holds(string client, string folder, string version)
        {
            var toc = Path.Combine(AddOn(client, folder), folder + ".toc");
            return File.Exists(toc) && File.ReadAllText(toc).Contains("## Version: " + version);
        }
        static string[] BackupSets(string client)
        {
            var backups = Path.Combine(client, ".portalkeeper", "backups", "pair");
            return Directory.Exists(backups) ? Directory.GetDirectories(backups) : [];
        }
        async Task<string> NewClientAtVersion1()
        {
            var client = Path.Combine(root, "rollback-client-" + ++clients);
            Directory.CreateDirectory(Path.Combine(client, "Interface", "AddOns"));
            await installer.InstallOrUpdateAsync(client, Pair("1.0"));
            return client;
        }
        async Task<Exception?> Update(string client, string version)
        {
            try { await installer.InstallOrUpdateAsync(client, Pair(version)); return null; }
            catch (Exception ex) { return ex; }
        }

        // Fails last, with both new folders in place: the state file's path is taken by a folder.
        var stateFailed = await NewClientAtVersion1();
        var statePath = Path.Combine(stateFailed, ".portalkeeper", "addons", "pair.json");
        File.Delete(statePath);
        Directory.CreateDirectory(statePath);
        var stateError = await Update(stateFailed, "2.0");
        check("a failed install reports the error that stopped it, not one from the rollback",
            stateError is UnauthorizedAccessException && stateError.Message.Contains("pair.json"));
        check("a failed install puts every old folder back under its own name",
            Holds(stateFailed, "Alpha", "1.0") && Holds(stateFailed, "Beta", "1.0"));
        check("a rolled back install leaves no backup set behind", BackupSets(stateFailed).Length == 0);

        // Fails while copying: a stray file sits where one new folder goes, and the other old folder is already moved away.
        var copyFailed = await NewClientAtVersion1();
        Directory.Delete(AddOn(copyFailed, "Beta"), true);
        File.WriteAllText(AddOn(copyFailed, "Beta"), "stray");
        var copyError = await Update(copyFailed, "2.0");
        check("a failed copy reports the error that stopped it", copyError is IOException && copyError.Message.Contains("Beta"));
        check("a failed copy puts the old folder back and leaves the stray file",
            Holds(copyFailed, "Alpha", "1.0") && File.ReadAllText(AddOn(copyFailed, "Beta")) == "stray" && BackupSets(copyFailed).Length == 0);

        // Fails while backing up. The archive's folder order isn't fixed, so hold each folder in turn: one of the two
        // runs has the other folder already moved to a backup, the other has it still waiting to move.
        if (!HoldStopsAMove(root))
        {
            Console.WriteLine("SKIP  a folder that can't be moved (here a held folder still moves, as it does for root)");
        }
        else
        {
            foreach (var held in new[] { "Alpha", "Beta" })
            {
                var backupFailed = await NewClientAtVersion1();
                Exception? backupError;
                using (HoldFolder(AddOn(backupFailed, held), held))
                    backupError = await Update(backupFailed, "2.0");
                check($"a folder that can't be moved ({held}) fails the install and says which",
                    backupError is not null && backupError.Message.Contains(Path.Combine("AddOns", held)));
                check($"a folder that can't be moved ({held}) costs the player nothing",
                    Holds(backupFailed, "Alpha", "1.0") && Holds(backupFailed, "Beta", "1.0") && BackupSets(backupFailed).Length == 0);
            }
        }

        // One update or uninstall is one set, so a two-folder addon keeps as many as a one-folder addon.
        var updated = await NewClientAtVersion1();
        await installer.InstallOrUpdateAsync(updated, Pair("2.0"));
        var firstSet = BackupSets(updated);
        check("an update of a two-folder addon makes one backup set with both folders",
            firstSet.Length == 1 && Directory.Exists(Path.Combine(firstSet[0], "Alpha")) && Directory.Exists(Path.Combine(firstSet[0], "Beta")));
        for (var version = 3; version <= 6; version++)
            await installer.InstallOrUpdateAsync(updated, Pair(version + ".0"));
        var kept = BackupSets(updated);
        check("the 3 kept backup sets of a two-folder addon each hold both folders",
            kept.Length == 3 && kept.All(set => Directory.GetDirectories(set).Length == 2));
        var removed = await NewClientAtVersion1();
        installer.Remove(removed, Pair("1.0"));
        check("uninstalling a two-folder addon makes one backup set with both folders",
            BackupSets(removed) is [var set] && Directory.GetDirectories(set).Length == 2 && !Directory.Exists(AddOn(removed, "Alpha")));
    }

    // Tries HoldFolder on a folder of its own, so a machine where it doesn't work skips the checks that need it instead
    // of leaving a read-only folder in a backup, where the test's cleanup can't delete it.
    private static bool HoldStopsAMove(string root)
    {
        var folder = Path.Combine(root, "hold-probe", "Probe");
        var elsewhere = Path.Combine(root, "hold-probe-moved");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(folder, "Probe.toc"), "");
        using (HoldFolder(folder, "Probe"))
        {
            try
            {
                Directory.Move(folder, Path.Combine(elsewhere, "Probe"));
                Directory.Move(Path.Combine(elsewhere, "Probe"), folder);
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }
    }

    // Makes a folder impossible to move until disposed: Windows refuses while a file in it is open, Unix refuses to
    // give a folder a new parent without write permission on the folder itself.
    private static IDisposable HoldFolder(string folder, string name)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(Path.Combine(folder, name + ".toc"), FileMode.Open, FileAccess.Read, FileShare.None);
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new RestoreFolderMode(folder, mode);
    }

    private sealed class RestoreFolderMode(string folder, UnixFileMode mode) : IDisposable
    {
        public void Dispose()
        {
#pragma warning disable CA1416 // Only made on Unix.
            if (Directory.Exists(folder)) File.SetUnixFileMode(folder, mode);
#pragma warning restore CA1416
        }
    }

    // Any path is a zip of the folders Alpha and Beta at that version.
    private sealed class TwoFolderAddonServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var version = request.RequestUri!.AbsolutePath.TrimStart('/');
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            {
                foreach (var folder in new[] { "Alpha", "Beta" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry($"{folder}/{folder}.toc").Open());
                    writer.Write("## Version: " + version + "\n");
                }
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) });
        }
    }
}
