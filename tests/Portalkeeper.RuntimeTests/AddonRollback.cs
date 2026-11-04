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
            stateError is IOException or UnauthorizedAccessException && stateError.Message.Contains("pair.json"));
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
        var holdWorks = HoldStopsAMove(root);
        if (!holdWorks)
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

        // An uninstall that can't finish leaves the addon as it was: in place, and still the launcher's.
        var states = new AddonInstallStateService();
        bool AsItWas(string client) =>
            Holds(client, "Alpha", "1.0") && Holds(client, "Beta", "1.0") && BackupSets(client).Length == 0 &&
            states.Load(client, "pair") is { Version: "1.0" } state && state.InstalledFolders.Contains("Alpha") && state.InstalledFolders.Contains("Beta");

        // The last folder the state file lists is refused, so the others would already be moved.
        foreach (var badName in new[] { "../Escape", "Nested/Folder", "" })
        {
            var refused = await NewClientAtVersion1();
            states.Save(refused, "pair", "1.0", "", ["Alpha", "Beta", badName]);
            Exception? refusedError = null;
            try { installer.Remove(refused, Pair("1.0")); }
            catch (Exception ex) { refusedError = ex; }
            check($"an uninstall listing the folder '{badName}' says so and moves nothing", refusedError is InvalidDataException && AsItWas(refused));
        }

        // A state file with no folder list: the one folder the addon names, or nothing at all when that's gone.
        var legacy = await NewClientAtVersion1();
        states.Save(legacy, "pair", "1.0", "", []);
        installer.Remove(legacy, Pair("1.0"));
        check("an uninstall with no folder list moves the addon's own folder and forgets the state",
            !Directory.Exists(AddOn(legacy, "Alpha")) && Holds(legacy, "Beta", "1.0") && BackupSets(legacy).Length == 1 &&
            !File.Exists(Path.Combine(legacy, ".portalkeeper", "addons", "pair.json")));
        states.Save(legacy, "pair", "1.0", "", []);
        installer.Remove(legacy, Pair("1.0"));
        check("an uninstall with no folder list and no folder changes nothing",
            BackupSets(legacy).Length == 1 && File.Exists(Path.Combine(legacy, ".portalkeeper", "addons", "pair.json")));

        if (holdWorks)
        {
            foreach (var held in new[] { "Alpha", "Beta" })
            {
                var stuck = await NewClientAtVersion1();
                Exception? removeError = null;
                using (HoldFolder(AddOn(stuck, held), held))
                {
                    try { installer.Remove(stuck, Pair("1.0")); }
                    catch (Exception ex) { removeError = ex; }
                }
                check($"an uninstall stopped by a stuck folder ({held}) says which",
                    removeError is not null && removeError.Message.Contains(Path.Combine("AddOns", held)));
                check($"an uninstall stopped by a stuck folder ({held}) leaves the addon as it was", AsItWas(stuck));
                installer.Remove(stuck, Pair("1.0"));
                check($"an uninstall stopped by a stuck folder ({held}) works once it's free",
                    !Directory.Exists(AddOn(stuck, "Alpha")) && !Directory.Exists(AddOn(stuck, "Beta")) &&
                    BackupSets(stuck) is [var only] && Directory.GetDirectories(only).Length == 2 &&
                    states.Load(stuck, "pair").InstalledFolders.Count == 0);
            }
        }

        // The state file is replaced whole, so a write that stops part way can't cost the launcher the one it had.
        // Both of these rely on Unix: its read-only folders, and replacing a file that's open.
        if (!OperatingSystem.IsWindows())
        {
            if (holdWorks)
            {
                var unsaved = await NewClientAtVersion1();
                Exception? saveError;
                using (HoldFolder(Path.Combine(unsaved, ".portalkeeper", "addons"), "pair"))
                    saveError = await Update(unsaved, "2.0");
                check("a state file that can't be saved fails the install", saveError is not null);
                check("a state file that can't be saved leaves the old one and the old folders", AsItWas(unsaved));

                var undeleted = await NewClientAtVersion1();
                Exception? deleteError = null;
                using (HoldFolder(Path.Combine(undeleted, ".portalkeeper", "addons"), "pair"))
                {
                    try { installer.Remove(undeleted, Pair("1.0")); }
                    catch (Exception ex) { deleteError = ex; }
                }
                check("a state file that can't be deleted fails the uninstall and leaves the addon as it was",
                    deleteError is not null && AsItWas(undeleted));
            }

            var replaced = await NewClientAtVersion1();
            var replacedPath = Path.Combine(replaced, ".portalkeeper", "addons", "pair.json");
            using var before = new StreamReader(new FileStream(replacedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            states.Save(replaced, "pair", "2.0", "", ["Alpha", "Beta"]);
            check("saving the state file replaces it rather than writing over it",
                before.ReadToEnd().Contains("\"Version\": \"1.0\"") && states.Load(replaced, "pair").Version == "2.0");
            check("saving the state file leaves no temporary file",
                Directory.GetFiles(Path.GetDirectoryName(replacedPath)!).Length == 1);
        }
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
