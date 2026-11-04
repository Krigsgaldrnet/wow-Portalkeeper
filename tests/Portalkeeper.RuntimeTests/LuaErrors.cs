using System.Net;
using System.Text;
using System.Text.Json;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: the game's Lua errors sent to the realm (LuaErrorService), from a file as the game
// writes it to the portal's luaerrors.php. The addon itself has tests/lua/ErrorsAddonTest.lua.
internal static partial class Program
{
    // As the 3.3.5a client writes SavedVariables: list items with "-- [1]" comments, keys in brackets.
    private const string SavedErrors = """

        PortalkeeperErrorsDB = {
        	["format"] = 1,
        	["suppressed"] = 3,
        	["dropped"] = 2,
        	["sessions"] = {
        		{
        			["locale"] = "enUS",
        			["started"] = 1800000100,
        			["character"] = "Alice",
        			["realm"] = "Test Realm",
        			["addons"] = {
        				"!PortalkeeperErrors 1.0", -- [1]
        				"DragonUI 2.19", -- [2]
        			},
        			["build"] = "3.3.5.12340",
        		}, -- [1]
        	},
        	["errors"] = {
        		{
        			["count"] = 12,
        			["first"] = 1800000200,
        			["session"] = 1,
        			["kind"] = "error",
        			["stack"] = "Interface\\AddOns\\Foo\\Foo.lua:12: in function <Foo.lua:10>\n[C]: ?",
        			["last"] = 1800000300,
        			["locals"] = "self = <table>\nname = \"quoted \\\" text\"",
        			["message"] = "Interface\\AddOns\\Foo\\Foo.lua:12: attempt to index \"caf\195\169\"",
        		}, -- [1]
        		{
        			["message"] = "DragonUI tried to call the protected function 'CastSpellByName()'.",
        			["kind"] = "blocked",
        			["count"] = 1,
        			["stack"] = "",
        			["session"] = 9,
        		}, -- [2]
        	},
        }

        OtherVariable = "not this one"

        """;

    private static async Task LuaErrorChecks(string root, Action<string, bool> check)
    {
        // ---- The addon
        var client = Path.Combine(root, "luaerrors", "client");
        Directory.CreateDirectory(client);
        var addon = Path.Combine(client, "Interface", "AddOns", LuaErrorService.AddonFolder);
        check("the addon's folder loads first and isn't one the client renames",
            LuaErrorService.AddonFolder.StartsWith('!') && !LuaErrorService.AddonFolder.Contains("Blizzard", StringComparison.OrdinalIgnoreCase));
        LuaErrorService.SyncAddon(client, enabled: true);
        var toc = Path.Combine(addon, LuaErrorService.AddonFolder + ".toc");
        var lua = Path.Combine(addon, "Errors.lua");
        check("the addon is written, its toc named after the folder and naming its file",
            File.Exists(toc) && File.Exists(lua) && File.ReadAllText(toc).Contains("Errors.lua") && File.ReadAllText(toc).Contains("## Interface: 30300"));
        check("it saves the table the launcher reads",
            File.ReadAllText(toc).Contains("## SavedVariables: PortalkeeperErrorsDB") && File.ReadAllText(lua).Contains("seterrorhandler"));
        File.WriteAllText(lua, "changed");
        LuaErrorService.SyncAddon(client, enabled: true);
        check("a changed file is put back", File.ReadAllText(lua).Contains("seterrorhandler"));
        LuaErrorService.SyncAddon(client, enabled: false);
        check("off deletes the folder", !Directory.Exists(addon));
        LuaErrorService.SyncAddon(client, enabled: false);
        check("off twice is fine", !Directory.Exists(addon));

        // ---- Reading the file
        var batch = LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes(SavedErrors));
        check("a file as the game writes it is read", batch is { Errors.Count: 2, Sessions.Count: 1, Dropped: 2, Suppressed: 3 });
        var first = batch!.Errors[0];
        check("an error's fields", first is { Kind: "error", Count: 12, First: 1800000200, Last: 1800000300, Session: 0 });
        check("escapes become text", first.Stack.Contains('\n') && first.Stack.StartsWith(@"Interface\AddOns\Foo\Foo.lua:12:")
            && first.Locals == "self = <table>\nname = \"quoted \\\" text\"");
        check("a byte escape is UTF-8", first.Message.EndsWith("attempt to index \"café\""));
        var login = batch.Sessions[0];
        check("a login's fields", login is { Character: "Alice", Realm: "Test Realm", Build: "3.3.5.12340", Locale: "enUS", Started: 1800000100 }
            && login.Addons.SequenceEqual(["!PortalkeeperErrors 1.0", "DragonUI 2.19"]));
        var blocked = batch.Errors[1];
        check("a login that isn't there is no login, and missing times are none", blocked is { Kind: "blocked", Session: null, First: null, Last: null, Locals: "" });

        check("bracketed list keys read like plain ones", LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes(
            """PortalkeeperErrorsDB = { ["format"] = 1, ["errors"] = { [1] = { ["message"] = "m", ["stack"] = "" }, [2] = { ["message"] = "n" } } }"""))
            is { Errors: [{ Message: "m" }, { Message: "n" }] });
        check("a table with no errors is nothing to send", LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes(
            """PortalkeeperErrorsDB = { ["format"] = 1, ["errors"] = {}, ["sessions"] = {} }""")) is null);
        check("another format is nothing to send", LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes(
            """PortalkeeperErrorsDB = { ["format"] = 2, ["errors"] = { { ["message"] = "m" } } }""")) is null);
        check("no such variable is nothing to send", LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes("Other = { 1, 2 }")) is null);
        foreach (var (name, text) in new[]
        {
            ("cut short", "PortalkeeperErrorsDB = { [\"format\"] = 1, [\"errors\"] = {"),
            ("an open string", "PortalkeeperErrorsDB = { [\"format\"] = \"1"),
            ("code", "PortalkeeperErrorsDB = os.execute(\"x\")"),
            ("a call in a table", "PortalkeeperErrorsDB = { evil() }"),
            ("text", "not lua at all"),
            ("a bad escape", "PortalkeeperErrorsDB = \"\\999\"")
        })
            check("a broken file is nothing to send: " + name, LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes(text)) is null);
        check("nesting deeper than any file of ours is refused, not followed",
            LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes("PortalkeeperErrorsDB = " + new string('{', 200000))) is null);
        check("a file past the limit is nothing to send",
            LuaErrorService.ReadBatch(new byte[LuaErrorService.MaxFileBytes + 1]) is null);

        // ---- Finding the files: any game account, whatever the folders' case
        var saved = Path.Combine(client, "wtf", "account", "ALICE", "savedvariables");
        Directory.CreateDirectory(saved);
        var other = Path.Combine(client, "wtf", "account", "BOB", "SavedVariables");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(Path.Combine(client, "wtf", "account", "EMPTY"));
        var aliceFile = Path.Combine(saved, LuaErrorService.AddonFolder + ".lua");
        var bobFile = Path.Combine(other, LuaErrorService.AddonFolder + ".lua");
        File.WriteAllText(aliceFile, SavedErrors);
        File.WriteAllText(aliceFile + ".bak", SavedErrors);
        File.WriteAllText(bobFile, "not lua at all");
        File.SetLastWriteTimeUtc(bobFile, DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllText(Path.Combine(saved, "DragonUI.lua"), "DragonUIDB = {}");
        check("every game account's file is found, and no other addon's", LuaErrorService.FindFiles(client).Count == 2);
        check("a client with no WTF has none", LuaErrorService.FindFiles(Path.Combine(root, "luaerrors")).Count == 0);

        // ---- Sending
        var api = new Uri("https://portal.test/api/launcher/");
        var session = new LauncherSession("lua-test-token-0123456789", "ALICE", DateTimeOffset.UtcNow.AddDays(30));
        var portal = new StubPortal();
        var errors = new LuaErrorService(api, new HttpClient(portal));
        portal.Answer = _ => (404, """{"error":"This realm doesn't collect Lua errors."}""");
        check("404 from the portal: the realm doesn't take Lua errors", !await errors.IsOfferedAsync(session));
        portal.Answer = _ => (404, "<html>404 page not found</html>");
        LauncherApiException? notAnswered = null;
        try { await errors.IsOfferedAsync(session); } catch (LauncherApiException ex) { notAnswered = ex; }
        check("404 from a proxy: the portal wasn't asked", notAnswered is not null);
        portal.Answer = _ => (200, """{"enabled":true}""");
        check("200: it does", await errors.IsOfferedAsync(session) && portal.Last?.Authorization == "Bearer lua-test-token-0123456789");

        portal.Answer = _ => (503, """{"error":"Can't store."}""");
        var result = await errors.SendPendingAsync(client, session);
        check("the portal not storing keeps the file for next time", result is { Sent: 0, Left: 1 } && File.Exists(aliceFile));
        check("an old file that isn't ours goes without being sent", !File.Exists(bobFile));
        File.WriteAllText(bobFile, "PortalkeeperErrorsDB = { [\"format\"] = 1, [\"err");
        result = await errors.SendPendingAsync(client, session);
        check("a broken file that's new may still be being written: it stays", result is { Sent: 0, Left: 2 } && File.Exists(bobFile));
        File.SetLastWriteTimeUtc(bobFile, DateTime.UtcNow.AddMinutes(-10));
        result = await errors.SendPendingAsync(client, session);
        check("a minute later it goes", result.Left == 1 && !File.Exists(bobFile));
        portal.Answer = _ => throw new HttpRequestException("offline");
        result = await errors.SendPendingAsync(client, session);
        check("no portal keeps the file too", result is { Sent: 0, Left: 1 } && File.Exists(aliceFile));
        portal.Answer = _ => (429, """{"error":"That's enough Lua errors for today. Thank you!"}""");
        result = await errors.SendPendingAsync(client, session);
        check("enough for today keeps the file too", result is { Sent: 0, Left: 1 } && File.Exists(aliceFile));

        portal.Answer = _ => (200, """{"id":"0123456789abcdef0123456789abcdef","errors":2}""");
        result = await errors.SendPendingAsync(client, session);
        var sent = JsonDocument.Parse(portal.Last!.Body).RootElement;
        check("a batch goes out as the portal expects it",
            portal.Last.Method == "POST" && portal.Last.Path == "/api/launcher/luaerrors.php"
            && sent.GetProperty("client").GetString()!.StartsWith("Portalkeeper/")
            && sent.GetProperty("dropped").GetInt64() == 2 && sent.GetProperty("suppressed").GetInt64() == 3
            && sent.GetProperty("sessions")[0].GetProperty("character").GetString() == "Alice"
            && sent.GetProperty("sessions")[0].GetProperty("addons")[1].GetString() == "DragonUI 2.19"
            && sent.GetProperty("errors")[0].GetProperty("count").GetInt64() == 12
            && sent.GetProperty("errors")[0].GetProperty("session").GetInt32() == 0
            && sent.GetProperty("errors")[1].GetProperty("kind").GetString() == "blocked"
            && sent.GetProperty("errors")[1].GetProperty("session").ValueKind == JsonValueKind.Null);
        check("the portal's yes deletes the file and its backup", result is { Sent: 2, Left: 0 } && !File.Exists(aliceFile) && !File.Exists(aliceFile + ".bak"));
        check("other addons' files stay", File.Exists(Path.Combine(saved, "DragonUI.lua")));
        portal.Last = null;
        result = await errors.SendPendingAsync(client, session);
        check("nothing recorded: nothing sent", result is { Sent: 0, Left: 0 } && portal.Last is null);

        File.WriteAllText(aliceFile, SavedErrors);
        portal.Answer = _ => (400, """{"error":"That isn't a batch of Lua errors."}""");
        result = await errors.SendPendingAsync(client, session);
        check("a file the portal can never take isn't kept", result is { Sent: 0, Left: 0 } && !File.Exists(aliceFile));
        File.WriteAllText(aliceFile, SavedErrors);
        portal.Answer = _ => (413, """{"error":"Too big."}""");
        await errors.SendPendingAsync(client, session);
        check("nor one that's too big", !File.Exists(aliceFile));
        File.WriteAllText(aliceFile, SavedErrors);
        portal.Answer = _ => (413, "<html>Request Entity Too Large</html>");
        result = await errors.SendPendingAsync(client, session);
        check("a proxy's 413 keeps the file: the portal never saw it", result is { Sent: 0, Left: 1 } && File.Exists(aliceFile));
        portal.Answer = _ => (400, "<html>Bad Request</html>");
        result = await errors.SendPendingAsync(client, session);
        check("so does a proxy's 400", result is { Sent: 0, Left: 1 } && File.Exists(aliceFile));

        using (var stream = File.OpenWrite(aliceFile)) stream.SetLength(LuaErrorService.MaxFileBytes + 1L);
        portal.Last = null;
        result = await errors.SendPendingAsync(client, session);
        check("a file past the limit goes without being read or sent", result is { Sent: 0, Left: 0 } && !File.Exists(aliceFile) && portal.Last is null);

        // A long list parses in time proportional to its length.
        var longList = new StringBuilder("PortalkeeperErrorsDB = { [\"format\"] = 1, [\"errors\"] = { ");
        while (longList.Length < LuaErrorService.MaxFileBytes - 16) longList.Append("1,");
        longList.Append("} }");
        var parsing = System.Diagnostics.Stopwatch.StartNew();
        LuaErrorService.ReadBatch(Encoding.UTF8.GetBytes(longList.ToString()));
        // Minutes when each item looks through the list; well under this when the machine is busy.
        check("the largest list a file can hold parses in time proportional to its size", parsing.Elapsed < TimeSpan.FromSeconds(20));

        File.WriteAllText(aliceFile, SavedErrors);
        portal.Answer = _ => (200, """{"id":"0123456789abcdef0123456789abcdef","errors":2}""");
        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel();
            portal.Answer = request =>
            {
                throw new TaskCanceledException();
            };
            result = await errors.SendPendingAsync(client, session, stopped.Token);
        }
        check("a send stopped for a launch keeps the file", result is { Sent: 0, Left: 1 } && File.Exists(aliceFile));

        portal.Answer = _ => (200, """{"id":"0123456789abcdef0123456789abcdef","errors":2}""");
        File.WriteAllText(aliceFile, "PortalkeeperErrorsDB = { [\"format\"] = 1, [\"errors\"] = { { [\"message\"] = \"привет\" } } }");
        await errors.SendPendingAsync(client, session);
        check("text goes out as it is, not six bytes a letter", portal.Last!.Body.Contains("привет"));

        File.WriteAllText(aliceFile, SavedErrors);
        File.WriteAllText(aliceFile + ".bak", SavedErrors);
        LuaErrorService.DeletePending(client);
        check("turning the option off takes what was recorded", !File.Exists(aliceFile) && !File.Exists(aliceFile + ".bak"));
    }
}
