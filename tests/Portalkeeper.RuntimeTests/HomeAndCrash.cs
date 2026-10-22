using System.Net;
using System.Text;
using System.Text.Json;
using Portalkeeper.Services;
using Portalkeeper.ViewModels;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: crash reports (CrashReportService) and the home screen's live info and patch notes
// (RealmLiveService), against stand-ins for the portal's crashreport.php, live.php and whatsnew.php.
internal static partial class Program
{
    private const string CrashText = """
        ==============================================================================
        World of WarCraft: Retail Build (build 12340)

        Exe:      C:\Users\Alice\Games\WoW\Wow.exe
        Time:     Oct 21, 2026  4:05:12.123 PM
        User:     Alice
        Computer: DESKTOP-ABC123
        ------------------------------------------------------------------------------

        This application has encountered a critical error:

        ERROR #132 (0x85100084) Fatal Exception
        Program:	C:\Users\Alice\Games\WoW\Wow.exe
        Exception:	0xC0000005 (ACCESS_VIOLATION) at 0023:0081d559

        The instruction at "0x0081D559" referenced memory at "0x00000000".
        Loaded from DESKTOP-ABC123 by alice.
        """;

    private static async Task HomeAndCrashChecks(string root, Action<string, bool> check)
    {
        // ---- The game's error reports
        var client = Path.Combine(root, "crash", "client");
        var errors = Path.Combine(client, "errors"); // lower case: found whatever its case
        Directory.CreateDirectory(errors);
        var launched = DateTimeOffset.UtcNow;
        check("no Errors files: no crash", CrashReportService.Collect(client, launched) is null);
        var old = Path.Combine(errors, "2026-10-01 10.00.00.txt");
        File.WriteAllText(old, "ERROR #134 from an earlier session");
        File.SetLastWriteTimeUtc(old, launched.UtcDateTime.AddHours(-2));
        check("an error file from before the launch isn't this session's crash", CrashReportService.Collect(client, launched) is null);

        File.WriteAllText(Path.Combine(errors, "2026-10-21 16.05.12 [x].txt"), CrashText);
        var crash = CrashReportService.Collect(client, launched);
        check("a crash during the session is picked up with its summary",
            crash is { Files.Count: 1, Summary: "ERROR #132 · ACCESS_VIOLATION at 0081D559" });
        check("its file name is one the portal takes", crash?.Files[0].Name == "2026-10-21 16.05.12 _x_.txt");

        var redacted = CrashReportService.Redact(CrashText, "alice", "DESKTOP-ABC123");
        check("the User and Computer lines are taken out", redacted.Contains("User: (removed)") && redacted.Contains("Computer: (removed)"));
        check("the user folder in paths is taken out", redacted.Contains(@"C:\Users\(user)\Games") && !redacted.Contains("Alice"));
        check("the user and computer names are taken out anywhere else",
            redacted.Contains("Loaded from (removed) by (removed).") && !redacted.Contains("DESKTOP-ABC123"));
        check("a Wine home folder is taken out", CrashReportService.Redact("Z:/home/alice/wine/Wow.exe", "x", "y") == "Z:/home/(user)/wine/Wow.exe");
        check("names too short to be sure of are left alone", CrashReportService.Redact("me at pc", "me", "pc") == "me at pc");
        check("a long computer name is also found as Windows reports it (15 characters)",
            CrashReportService.Redact("Host: BOBS-MACBOOK-PR here", "x", "Bobs-MacBook-Pro") == "Host: (removed) here");
        check("the error is kept", redacted.Contains("ERROR #132") && redacted.Contains("0x0081D559"));

        check("a report without ERROR/Exception lines is summed up by its first error line",
            CrashReportService.Summarize("hello\nSomething error happened here\nbye") == "Something error happened here");
        check("nothing that looks like an error: no summary", CrashReportService.Summarize("all good") == "");

        var big = Path.Combine(errors, "big.txt");
        File.WriteAllText(big, "ERROR #132\n" + new string('é', CrashReportService.MaxFileBytes));
        crash = CrashReportService.Collect(client, launched);
        check("a long error file is cut to the portal's limit",
            crash is { Files.Count: 2 } && crash.Files.All(f => Encoding.UTF8.GetByteCount(f.Text) <= CrashReportService.MaxFileBytes)
            && crash.Files.Any(f => f.Text.EndsWith("[... the rest was cut by Portalkeeper]")));
        File.Delete(big);
        File.WriteAllBytes(Path.Combine(errors, "latin.txt"), Encoding.Latin1.GetBytes("ERROR #132 in Rüstung"));
        crash = CrashReportService.Collect(client, launched);
        check("a file in the Windows code page is read as such", crash?.Files.Any(f => f.Text == "ERROR #132 in Rüstung") == true);

        // ---- Sending
        var api = new Uri("https://portal.test/api/launcher/");
        var session = new LauncherSession("crash-test-token-0123456789", "ALICE", DateTimeOffset.UtcNow.AddDays(30));
        var portal = new StubPortal();
        var crashes = new CrashReportService(api, new HttpClient(portal));
        portal.Answer = _ => (404, """{"error":"This realm doesn't collect crash reports."}""");
        check("404: the realm doesn't take crash reports", !await crashes.IsOfferedAsync(session));
        portal.Answer = _ => (200, """{"enabled":true,"max_files":4,"max_file_size":262144}""");
        check("200: it does", await crashes.IsOfferedAsync(session) && portal.Last?.Authorization == "Bearer crash-test-token-0123456789");
        portal.Answer = _ => (200, """{"id":"0123456789abcdef0123456789abcdef","summary":"ERROR #132 · ACCESS_VIOLATION at 0081D559"}""");
        var report = new CrashReport(launched, [new CrashReportFile("a.txt", "ERROR #132")], "ERROR #132");
        var summary = await crashes.SendAsync(session, report);
        var sent = JsonDocument.Parse(portal.Last!.Body).RootElement;
        check("a report goes out as the portal expects it",
            portal.Last.Method == "POST" && portal.Last.Path == "/api/launcher/crashreport.php"
            && sent.GetProperty("happened_at").GetInt64() == launched.ToUnixTimeSeconds()
            && sent.GetProperty("client").GetString()!.StartsWith("Portalkeeper/")
            && sent.GetProperty("files")[0].GetProperty("name").GetString() == "a.txt"
            && sent.GetProperty("files")[0].GetProperty("text").GetString() == "ERROR #132");
        check("the portal's summary comes back", summary == "ERROR #132 · ACCESS_VIOLATION at 0081D559");
        portal.Answer = _ => (429, """{"error":"That's enough crash reports for today. Thank you!"}""");
        LauncherApiException? refused = null;
        try { await crashes.SendAsync(session, report); } catch (LauncherApiException ex) { refused = ex; }
        check("a refusal carries the portal's message", refused?.Message == "That's enough crash reports for today. Thank you!");

        // ---- Live info
        var live = new RealmLiveService(api, new HttpClient(portal));
        portal.Answer = _ => (200, """
            {"generated":1792613879,"players":{"online":2,"bots":3},"server":{"uptime":93784,"restart_in":570},
             "events":[{"id":24,"name":"Brewfest"}],
             "rares":{"up":2,"total":3,"world_bosses":[{"name":"Azuregos","zone":"Azshara","up":true,"respawn_at":null},
                                                       {"name":"Ysondre","zone":"Duskwood","up":false,"respawn_at":1900000000}]}}
            """);
        var now = await live.GetLiveAsync();
        check("live info is read", now is { Players: { Online: 2, Bots: 3 }, Server.Uptime: 93784, Events.Count: 1, Rares: { Up: 2, Total: 3 } }
            && now.Rares.WorldBosses[1] is { Name: "Ysondre", Up: false, RespawnAt: 1900000000 } && portal.Last?.Authorization is null);
        check("a pending restart counts from when the portal made the answer",
            now?.RestartAt == DateTimeOffset.FromUnixTimeSeconds(1792613879 + 570));
        portal.Answer = _ => (200, """{"generated":1792613879,"players":null,"server":{"uptime":5,"restart_in":null},"events":null,"rares":null}""");
        now = await live.GetLiveAsync();
        check("parts the portal doesn't have are null, and no restart is no restart", now is { Players: null, Events: null, Rares: null, RestartAt: null });
        portal.Answer = _ => (404, """{"error":"The launcher service is turned off."}""");
        check("an older portal without live.php: nothing to show", await live.GetLiveAsync() is null);

        // ---- What's new
        portal.Answer = _ => (200, """
            {"since":"2026-10-07T20:00:00+00:00","url":"https://portal.test/changelog.php","more":3,
             "notes":[{"date":"2026-10-20T12:00:00Z","kind":"feature","section":"Collections","sub":null,"title":"Red Pandas","text":["Red panda companions roam the world."],"url":"u"},
                      {"date":"2026-10-19T12:00:00Z","kind":"change","section":"Classes","sub":"Druid","title":null,"text":["Aquatic Form swims faster.","And more."],"url":"u"},
                      {"date":"2026-10-18T12:00:00Z","kind":"fix","section":"Auction House","sub":null,"title":null,"text":["Fixed an issue."],"url":"u"}]}
            """);
        var since = new DateTimeOffset(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);
        var news = await live.GetWhatsNewAsync(since, 12);
        check("patch notes since a time are asked with that time and a limit",
            portal.Last?.Path == "/api/launcher/whatsnew.php" && portal.Last.Query == $"?limit=12&since={since.ToUnixTimeSeconds()}");
        check("patch notes are read", news is { Notes.Count: 3, More: 3, Url: "https://portal.test/changelog.php" } && news.Notes[1].Sub == "Druid");
        await live.GetWhatsNewAsync(null, 5);
        check("never played: no since, the portal's default", portal.Last?.Query == "?limit=5");
        var items = news!.Notes.Select(MainViewModel.ToItem).ToArray();
        check("a new feature shows as NEW with its name and first line",
            items[0] is { Tag: "NEW", Headline: "Red Pandas", Detail: "Red panda companions roam the world." });
        check("a change is tagged with its class or section", items[1] is { Tag: "DRUID", Headline: "Aquatic Form swims faster.", Detail: "And more." });
        check("a fix shows as FIX", items[2] is { Tag: "FIX", Headline: "Fixed an issue.", Detail: null });
        portal.Answer = _ => (503, """{"error":"The patch notes aren't ready yet."}""");
        check("patch notes not built yet: nothing to show", await live.GetWhatsNewAsync(since, 12) is null);

        check("restart times read naturally",
            MainViewModel.DescribeIn(TimeSpan.FromSeconds(570)) == "in about 10 min"
            && MainViewModel.DescribeIn(TimeSpan.FromSeconds(20)) == "in under a minute"
            && MainViewModel.DescribeIn(TimeSpan.FromMinutes(125)) == "in about 2 h 5 min"
            && MainViewModel.DescribeIn(TimeSpan.FromSeconds(-5)) == "now");
        check("events read naturally",
            MainViewModel.DescribeEvents([new(1, "Brewfest")]) == "Brewfest is on"
            && MainViewModel.DescribeEvents([new(1, "Brewfest"), new(2, "Hallow's End")]) == "Brewfest and Hallow's End are on"
            && MainViewModel.DescribeEvents([new(1, "A"), new(2, "B"), new(3, "C"), new(4, "D")]) == "A, B and 2 more are on");
        check("world bosses read as a count, not names",
            MainViewModel.DescribeBosses([new("Azuregos", "Azshara", true, null), new("Ysondre", "Duskwood", false, 1900000000)]) == "1 of 2 world bosses up"
            && MainViewModel.DescribeBosses([new("Azuregos", "Azshara", false, 1900000000)]) == "0 of 1 world boss up");

        // ---- Realm booting (realm-status.json)
        var bootNow = DateTimeOffset.FromUnixTimeSeconds(1792688600);
        var bots = RealmHealthService.ParseBootStatus(
            """{"state":"booting","stage":"bots","bots_online":812,"bots_target":2154,"since":1792688400,"updated":1792688595}""", bootNow);
        check("a current booting status is read", bots is { Stage: "bots", BotsOnline: 812, BotsTarget: 2154, Since: 1792688400 });
        check("booting reads as the bot count", MainViewModel.DescribeBoot(bots) == "bots logging in (812 of 2154)");
        check("the world stage reads as loading", MainViewModel.DescribeBoot(RealmHealthService.ParseBootStatus(
            """{"state":"booting","stage":"world","bots_online":0,"bots_target":0,"since":1792688400,"updated":1792688600}""", bootNow)) == "the world is loading");
        check("the last stage reads as login opening", MainViewModel.DescribeBoot(RealmHealthService.ParseBootStatus(
            """{"state":"booting","stage":"login","bots_online":0,"bots_target":0,"since":1792688400,"updated":1792688600}""", bootNow)) == "login is opening");
        check("a booting status left by a server that stopped mid-boot is ignored", RealmHealthService.ParseBootStatus(
            """{"state":"booting","stage":"world","bots_online":0,"bots_target":0,"since":1792688000,"updated":1792688300}""", bootNow) is null);
        check("online and offline aren't booting",
            RealmHealthService.ParseBootStatus("""{"state":"online","since":1792688400,"updated":1792688600}""", bootNow) is null
            && RealmHealthService.ParseBootStatus("""{"state":"offline","since":1792688400,"updated":1792688600}""", bootNow) is null);
        check("a broken file isn't booting", RealmHealthService.ParseBootStatus("<html>502</html>", bootNow) is null);

        var closedPort = FreeTcpPort();
        var authListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        authListener.Start();
        try
        {
            var openPort = ((IPEndPoint)authListener.LocalEndpoint).Port;
            var closedRealm = new Portalkeeper.Models.RealmInfo { SchemaVersion = 1, Name = "Test", Address = "127.0.0.1", AuthPort = closedPort, WorldPort = closedPort };
            var openRealm = new Portalkeeper.Models.RealmInfo { SchemaVersion = 1, Name = "Test", Address = "127.0.0.1", AuthPort = openPort, WorldPort = closedPort };
            var health = new RealmHealthService(new HttpClient(portal));
            var current = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            portal.Answer = _ => (200, $$"""{"state":"booting","stage":"bots","bots_online":5,"bots_target":9,"since":{{current - 60}},"updated":{{current}}}""");
            var booting = await health.CheckAsync(closedRealm, "https://portal.test/realm/realm-status.json");
            check("login closed + the realm says booting: Booting", booting is { State: Portalkeeper.Models.RealmHealthState.Booting, Boot.BotsOnline: 5 }
                && portal.Last?.Path == "/realm/realm-status.json");
            check("without a status URL the ports decide",
                (await health.CheckAsync(closedRealm)).State == Portalkeeper.Models.RealmHealthState.Offline);
            check("login open: the status file doesn't matter",
                (await health.CheckAsync(openRealm, "https://portal.test/realm/realm-status.json")).State == Portalkeeper.Models.RealmHealthState.AuthOnly);
            portal.Answer = _ => (404, "{}");
            check("no status file: the ports decide",
                (await health.CheckAsync(closedRealm, "https://portal.test/realm/realm-status.json")).State == Portalkeeper.Models.RealmHealthState.Offline);
        }
        finally
        {
            authListener.Stop();
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A portal that answers whatever <see cref="Answer"/> says and remembers the last request.</summary>
    private sealed class StubPortal : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Query, string? Authorization, string Body);

        public Func<HttpRequestMessage, (int Status, string Json)> Answer = _ => (404, "{}");
        public Request? Last;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Last = new Request(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                request.Headers.Authorization?.ToString(), body);
            var (status, json) = Answer(request);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
