using System.IO.Compression;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Vaultrona fork: what the background updates of installed optional addons rely on in the installer.
internal static partial class Program
{
    private static async Task AddonUpdateChecks(string root, Action<string, bool> check)
    {
        var client = Path.Combine(root, "addon-client");
        Directory.CreateDirectory(Path.Combine(client, "Interface", "AddOns"));
        var installer = new AddonInstallerService(new HttpClient(new FixtureAddonServer()));
        var inspector = new AddonService();
        AddonDefinition Addon(string version, string url = "") => new()
        {
            Id = "dbm", Name = "DBM", Folder = "DBM-Core", Version = version,
            DownloadUrl = url.Length > 0 ? url : "https://fixture.invalid/addon-" + version
        };
        AddonInfo Inspect(AddonDefinition addon) =>
            inspector.InspectAddons(client, new AddonManifest { Addons = [addon] })[0];
        string Toc() => File.ReadAllText(Path.Combine(client, "Interface", "AddOns", "DBM-Core", "DBM-Core.toc"));
        var backups = Path.Combine(client, ".portalkeeper", "backups", "dbm");

        // A folder the player put there by hand isn't the launcher's.
        var addOn = Path.Combine(client, "Interface", "AddOns", "DBM-Core");
        Directory.CreateDirectory(addOn);
        File.WriteAllText(Path.Combine(addOn, "DBM-Core.toc"), "## Version: 0.9\n");
        var hand = Inspect(Addon("1.0"));
        check("a hand-installed folder counts as installed but not by the launcher", hand.IsInstalled && !hand.InstalledByLauncher);

        await installer.InstallOrUpdateAsync(client, Addon("1.0"));
        check("an addon the launcher installed is marked as its own", Inspect(Addon("1.0")).InstalledByLauncher);

        for (var version = 2; version <= 6; version++)
            await installer.InstallOrUpdateAsync(client, Addon(version + ".0"));
        check("updates keep only the newest 3 backup sets", Directory.GetDirectories(backups).Length == 3);
        check("the update is in place", Toc().Contains("6.0"));

        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            var cancelled = false;
            try { await installer.InstallOrUpdateAsync(client, Addon("7.0", "https://fixture.invalid/slow"), cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check("a cancelled download stops the update", cancelled);
        }
        check("a cancelled update leaves the installed addon alone", Toc().Contains("6.0") && Directory.GetDirectories(backups).Length == 3);

        installer.Remove(client, Addon("6.0"));
        check("uninstalling keeps at most 3 backup sets", !Directory.Exists(addOn) && Directory.GetDirectories(backups).Length == 3);
        Directory.CreateDirectory(addOn);
        File.WriteAllText(Path.Combine(addOn, "DBM-Core.toc"), "## Version: 5.0\n");
        check("a folder put back by hand after uninstalling isn't the launcher's", !Inspect(Addon("6.0")).InstalledByLauncher);

        // GitHub addons: an unchanged commit is confirmed from git's ref listing, not GitHub's API,
        // whose 60 calls an hour per address the launcher's self-update needs too.
        var github = new FixtureGitHub();
        var source = new GitHubAddonSourceService(new HttpClient(github), Path.Combine(root, "github-cache"));
        var dbm = new AddonDefinition { Id = "dbm", Name = "DBM", GitUrl = "https://github.com/owner/DBM-Core" };
        var first = await source.ResolveAsync(dbm);
        check("a new GitHub addon is resolved through the API", first.Version == "1.0" && github.ApiCalls > 0);
        github.ApiCalls = 0;
        var again = await source.ResolveAsync(dbm);
        check("an unchanged GitHub addon makes no API calls", again.Version == "1.0" && github.ApiCalls == 0 && github.RefListings > 0);
        github.Commit = new string('b', 40);
        github.Version = "2.0";
        var changed = await source.ResolveAsync(dbm);
        check("a changed GitHub addon is resolved again", changed.Version == "2.0" && github.ApiCalls > 0);
    }

    // A one-repository GitHub: API, raw files and git's ref listing for owner/DBM-Core at Commit.
    private sealed class FixtureGitHub : HttpMessageHandler
    {
        public string Commit = new('a', 40);
        public string Version = "1.0";
        public int ApiCalls, RefListings;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string body;
            if (uri.Host == "api.github.com")
            {
                ApiCalls++;
                body = uri.AbsolutePath switch
                {
                    "/repos/owner/DBM-Core" => """{"default_branch":"main"}""",
                    "/repos/owner/DBM-Core/commits/main" => "{\"sha\":\"" + Commit + "\",\"commit\":{\"tree\":{\"sha\":\"tree-" + Commit + "\"}}}",
                    _ when uri.AbsolutePath == "/repos/owner/DBM-Core/git/trees/tree-" + Commit =>
                        """{"truncated":false,"tree":[{"path":"DBM-Core.toc","type":"blob"}]}""",
                    _ => ""
                };
            }
            else if (uri.Host == "raw.githubusercontent.com" && uri.AbsolutePath == $"/owner/DBM-Core/{Commit}/DBM-Core.toc")
                body = "## Version: " + Version + "\n";
            else if (uri.Host == "github.com" && uri.AbsolutePath == "/owner/DBM-Core.git/info/refs")
            {
                RefListings++;
                body = $"001e# service=git-upload-pack\n0000{Commit} HEAD\0symref=HEAD:refs/heads/main\n{Commit} refs/heads/main\n0000";
            }
            else
                body = "";
            return Task.FromResult(body.Length == 0
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    // addon-<version> is a zip of DBM-Core with that version; slow never finishes.
    private sealed class FixtureAddonServer : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (name == "slow")
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("DBM-Core/DBM-Core.toc").Open()))
                writer.Write("## Version: " + name["addon-".Length..] + "\n");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
        }
    }
}
