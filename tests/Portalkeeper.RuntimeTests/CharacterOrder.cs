using System.Net;
using System.Text;
using System.Text.Json;
using Portalkeeper.Models;
using Portalkeeper.Services;
using Portalkeeper.ViewModels;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: more than 10 characters per realm and their order. The Wow.exe edit on a stand-in PE
// (CharacterListLimitService), the order the character select screen leaves in Config.wtf, and
// CharacterOrderService / CharactersViewModel against a stand-in for the portal's characters.php.
internal static partial class Program
{
    private const string CharacterListJson = """
        {"characters":[
          {"guid":12,"name":"Pawster","race":1,"class":1,"gender":0,"level":60,"lastPlayed":1761400000,"online":false,"order":0},
          {"guid":14,"name":"Ironhide","race":3,"class":1,"gender":0,"level":30,"lastPlayed":1761300000,"online":true,"order":1},
          {"guid":15,"name":"Oldhc","race":2,"class":3,"gender":1,"level":12,"lastPlayed":1761000000,"online":false,"order":null},
          {"guid":16,"name":"Zaph","race":10,"class":8,"gender":1,"level":70,"lastPlayed":0,"online":false,"order":null},
          {"guid":15,"name":"Oldhc","race":2,"class":3,"gender":1,"level":12,"lastPlayed":1761000000,"online":false,"order":null},
          {"guid":0,"name":"Broken"}]}
        """;

    /// <summary>A PE32 stand-in for Wow.exe: one .text section mapped like build 12340's, both guards at their places.</summary>
    private static byte[] StandInClient(uint pointerToRawData = 0x400, uint imageBase = 0x400000, bool longNamePatterns = true)
    {
        const int textSize = 0x368000;
        var bytes = new byte[0x400 + textSize];
        new Random(70).NextBytes(bytes);
        // No 'p': the long-name patterns are only where they're put.
        for (var i = 0; i < bytes.Length; i++) if (bytes[i] == (byte)'p') bytes[i] = (byte)'q';
        Array.Clear(bytes, 0, 0x400);
        void U16(int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(bytes, at);
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        U32(0x3C, 0x80);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, 0x80);
        U16(0x84, 0x014C); // i386
        U16(0x86, 1); // one section
        U16(0x94, 0xE0); // optional header size
        U16(0x96, 0x0102);
        const int optional = 0x98;
        U16(optional, 0x10B); // PE32
        U32(optional + 4, textSize);
        U32(optional + 16, 0x1000);
        U32(optional + 20, 0x1000);
        U32(optional + 28, imageBase);
        U32(optional + 32, 0x1000);
        U32(optional + 36, 0x200);
        U16(optional + 40, 4);
        U16(optional + 48, 4);
        U32(optional + 56, 0x1000 + textSize);
        U32(optional + 60, 0x400);
        U16(optional + 68, 2);
        U32(optional + 92, 16);
        const int section = optional + 0xE0;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(bytes, section);
        U32(section + 8, textSize);
        U32(section + 12, 0x1000);
        U32(section + 16, textSize);
        U32(section + 20, pointerToRawData);
        U32(section + 36, 0x60000020);
        Convert.FromHexString("807DFF0AC745F800000000760B").CopyTo(bytes, 0x6404C);
        Convert.FromHexString("6A006A006A016A05").CopyTo(bytes, 0x367707);
        if (longNamePatterns)
            Encoding.ASCII.GetBytes("patch-%s-?.MPQ\0\0patch-?.MPQ\0patch-%s.MPQ\0").CopyTo(bytes, 0x2000);
        return bytes;
    }

    private static void CharacterListLimitChecks(string root, Action<string, bool> check)
    {
        var client = Path.Combine(root, "charlimit");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "Wow.exe");
        var stock = StandInClient();
        File.WriteAllBytes(exe, stock);
        string[] Backups() => Directory.Exists(Path.Combine(client, ".portalkeeper", "backups", "executable"))
            ? Directory.EnumerateFiles(Path.Combine(client, ".portalkeeper", "backups", "executable"), "*", SearchOption.AllDirectories).ToArray()
            : [];

        check("char limit: a stock client is not set up yet", !CharacterListLimitService.IsSetUp(exe) && Backups().Length == 0);
        check("char limit: a stock client is changed", CharacterListLimitService.Ensure(client, exe) && CharacterListLimitService.IsSetUp(exe));
        var edited = File.ReadAllBytes(exe);
        var differences = Enumerable.Range(0, stock.Length).Where(i => stock[i] != edited[i]).ToArray();
        check("char limit: exactly the jbe at 0x64057 becomes a jmp and the CVar push 1 at 0x36770C a push 0",
            edited.Length == stock.Length && differences.SequenceEqual([0x64057, 0x36770C])
            && stock[0x64057] == 0x76 && edited[0x64057] == 0xEB && stock[0x36770C] == 0x01 && edited[0x36770C] == 0x00);
        check("char limit: the file it replaced is kept", Backups().Length == 1 && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("char limit: a client that's set up is left alone", !CharacterListLimitService.Ensure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1
            && Directory.EnumerateFiles(client).Count() == 1);

        // A client with only one of the two (the first release had only the limit) is finished.
        foreach (var only in new[] { 0x64057, 0x36770C })
        {
            var half = (byte[])stock.Clone();
            half[only] = edited[only];
            File.WriteAllBytes(exe, half);
            check($"char limit: a client with only the edit at 0x{only:X} gets the other one",
                !CharacterListLimitService.IsSetUp(exe) && CharacterListLimitService.Ensure(client, exe)
                && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited));
        }

        // Both edits, in either order, give the same file: a client torrent can carry it.
        File.WriteAllBytes(exe, stock);
        var longFirst = LongPatchNameService.Ensure(client, exe) && CharacterListLimitService.Ensure(client, exe);
        var both = File.ReadAllBytes(exe);
        File.WriteAllBytes(exe, stock);
        var limitFirst = CharacterListLimitService.Ensure(client, exe) && LongPatchNameService.Ensure(client, exe);
        check("char limit: with the long-name edit, either order gives the same file", longFirst && limitFirst
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(both)
            && LongPatchNameService.IsSetUp(exe) && CharacterListLimitService.IsSetUp(exe)
            && Enumerable.Range(0, stock.Length).Count(i => stock[i] != both[i]) == 4);
        // stock, the two half-edited ones, stock + long names, stock + char edits: one backup each, keyed by what was replaced.
        check("char limit: every version it replaced is kept once", Backups().Length == 5);

        // Anything else is refused and left as it was.
        var backupsSoFar = Backups().Length;
        var foreign = Path.Combine(client, "Other.exe");
        bool Refused(byte[] bytes)
        {
            File.WriteAllBytes(foreign, bytes);
            var ensured = false;
            var asked = false;
            try { CharacterListLimitService.Ensure(client, foreign); } catch (InvalidDataException) { ensured = true; }
            try { CharacterListLimitService.IsSetUp(foreign); } catch (InvalidDataException) { asked = true; }
            return ensured && asked && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(bytes);
        }
        var wrongGuard = (byte[])stock.Clone();
        wrongGuard[0x6404C + 3] = 0x0B; // cmp ..., 0x0b
        check("char limit: a different guard is refused", Refused(wrongGuard));
        var otherJump = (byte[])stock.Clone();
        otherJump[0x64057] = 0x75; // jne
        check("char limit: another jump is refused", Refused(otherJump));
        var wrongCVarGuard = (byte[])stock.Clone();
        wrongCVarGuard[0x367707 + 7] = 0x06; // push 6
        check("char limit: a different CVar guard is refused", Refused(wrongCVarGuard));
        var otherFlag = (byte[])stock.Clone();
        otherFlag[0x36770C] = 0x02; // push 2
        check("char limit: another CVar flag is refused", Refused(otherFlag));
        check("char limit: a file that isn't a PE is refused", Refused(Encoding.ASCII.GetBytes("not a client").Concat(new byte[0x370000]).ToArray()));
        check("char limit: a PE with another image base is refused", Refused(StandInClient(imageBase: 0x10000000)));
        check("char limit: a PE whose .text sits elsewhere in the file is refused", Refused(StandInClient(pointerToRawData: 0x600)));
        check("char limit: a refused file leaves no backup and no temp file", Backups().Length == backupsSoFar
            && Directory.EnumerateFiles(client).Count() == 2);
    }

    private static async Task CharacterOrderChecks(string root, Action<string, bool> check)
    {
        // The CVar in Config.wtf.
        const string name = CharacterOrderService.ConfigSetting;
        check("char order: the CVar is read without its quotes",
            RealmLaunchService.ConfigSetting("SET realmName \"Azeroth\"\nSET evermoreCharOrder \"ALICE:Pawster,Zaph\"\n", name) == "ALICE:Pawster,Zaph");
        check("char order: a missing CVar is null, an empty one empty",
            RealmLaunchService.ConfigSetting("SET realmName \"Azeroth\"\n", name) is null
            && RealmLaunchService.ConfigSetting("SET evermoreCharOrder \"\"\r\nSET x \"1\"\r\n", name) == "");
        check("char order: the CVar name has no case, a longer name isn't it, the last line wins",
            RealmLaunchService.ConfigSetting("set EVERMORECHARORDER \"a:B\"", name) == "a:B"
            && RealmLaunchService.ConfigSetting("SET evermoreCharOrderOld \"a:B\"", name) is null
            && RealmLaunchService.ConfigSetting("SET evermoreCharOrder \"a:B\"\nSET evermoreCharOrder \"a:C\"\n", name) == "a:C");

        var client = Path.Combine(root, "charorder");
        var wtf = Path.Combine(client, "WTF");
        Directory.CreateDirectory(wtf);
        var config = Path.Combine(wtf, "Config.wtf");
        check("char order: no Config.wtf, no value", RealmLaunchService.ReadConfigSetting(client, name) is null
            && !RealmLaunchService.ClearConfigSetting(client, name, "ALICE:Pawster"));

        var builtIn = new RealmInfo { ConfigUrl = RealmBranding.ConfigUrl };
        var other = new RealmInfo { ConfigUrl = "https://example.com/realm.conf" };
        check("char order: the built-in realm registers the CVar, other realms don't",
            RealmLaunchService.NeedsCharacterOrderSetting(builtIn, config) && !RealmLaunchService.NeedsCharacterOrderSetting(other, config));
        File.WriteAllText(config, "SET realmName \"Azeroth\"\r\nSET accountName \"ALICE\"\r\n");
        RealmLaunchService.WriteConfigSettings(config, [(name, "")], client);
        var written = File.ReadAllText(config);
        check("char order: a missing CVar is written empty and quoted, other lines kept",
            written == "SET realmName \"Azeroth\"\r\nSET accountName \"ALICE\"\r\nSET evermoreCharOrder \"\"\r\n"
            && RealmLaunchService.ReadConfigSetting(client, name) == ""
            && !RealmLaunchService.NeedsCharacterOrderSetting(builtIn, config));

        File.WriteAllText(config, "SET realmName \"Azeroth\"\r\nSET evermoreCharOrder \"ALICE:Zaph,Pawster\"\r\nSET gxWindow \"1\"\r\n");
        check("char order: a value the game left isn't overwritten at launch", !RealmLaunchService.NeedsCharacterOrderSetting(builtIn, config));
        check("char order: a newer value isn't cleared", !RealmLaunchService.ClearConfigSetting(client, name, "ALICE:Pawster")
            && RealmLaunchService.ReadConfigSetting(client, name) == "ALICE:Zaph,Pawster");
        check("char order: the value sent is cleared, quoted, and nothing else changes",
            RealmLaunchService.ClearConfigSetting(client, name, "ALICE:Zaph,Pawster")
            && File.ReadAllText(config) == "SET realmName \"Azeroth\"\r\nSET evermoreCharOrder \"\"\r\nSET gxWindow \"1\"\r\n"
            && RealmLaunchService.ClearConfigSetting(client, name, "ALICE:Zaph,Pawster"));

        // The value: "ACCOUNT:Name1,Name2,..."
        check("char order: the account's order is read, names trimmed, the account without case",
            CharacterOrderService.ParsePending("alice:Zaph, Pawster ,Ironhide", "ALICE") is { } names
            && names.SequenceEqual(["Zaph", "Pawster", "Ironhide"]));
        check("char order: another account's order is not this one's", CharacterOrderService.ParsePending("BOB:Zaph,Pawster", "ALICE") is null);
        check("char order: empty, no account, no names: nothing pending",
            CharacterOrderService.ParsePending("", "ALICE") is null && CharacterOrderService.ParsePending(null, "ALICE") is null
            && CharacterOrderService.ParsePending("Zaph,Pawster", "ALICE") is null && CharacterOrderService.ParsePending(":Zaph", "ALICE") is null
            && CharacterOrderService.ParsePending("ALICE:", "ALICE") is null && CharacterOrderService.ParsePending("ALICE: , ", "ALICE") is null
            && CharacterOrderService.ParsePending("ALICE:Zaph", null) is null);
        check("char order: a name twice counts once", CharacterOrderService.ParsePending("ALICE:Zaph,zaph,Pawster", "ALICE") is { } twice
            && twice.SequenceEqual(["Zaph", "Pawster"]));

        // The launcher's own copy of an unsent order.
        var kept = new PendingCharacterOrder { Account = "alice", Names = ["Ironhide", " Zaph", "", "ironhide"] };
        check("char order: an order in Config.wtf wins over the launcher's copy",
            CharacterOrderService.PendingOrder(["Pawster"], kept, "ALICE") is { } newer && newer.SequenceEqual(["Pawster"]));
        check("char order: without one, the launcher's copy for this account is sent, cleaned up",
            CharacterOrderService.PendingOrder(null, kept, "ALICE") is { } old && old.SequenceEqual(["Ironhide", "Zaph"]));
        check("char order: another account's copy, an empty copy, or none: nothing",
            CharacterOrderService.PendingOrder(null, kept, "BOB") is null
            && CharacterOrderService.PendingOrder(null, new PendingCharacterOrder { Account = "ALICE" }, "ALICE") is null
            && CharacterOrderService.PendingOrder(null, null, "ALICE") is null);
        var settingsPath = Path.Combine(root, "charorder-settings", "settings.json");
        var settingsService = new SettingsService(settingsPath);
        var settings = settingsService.Load();
        settings.PendingCharacterOrder = new PendingCharacterOrder { Account = "ALICE", Names = ["Zaph", "Pawster"] };
        settingsService.Save(settings);
        check("char order: the launcher's copy survives a restart",
            settingsService.Load().PendingCharacterOrder is { Account: "ALICE" } reloaded && reloaded.Names.SequenceEqual(["Zaph", "Pawster"]));

        // The portal.
        var api = new Uri("https://portal.test/api/launcher/");
        var session = new LauncherSession(FakeCharacterPortal.Token, "ALICE", DateTimeOffset.UtcNow.AddDays(30));
        CharacterOrderService Service(FakeCharacterPortal portal) => new(api, new HttpClient(portal));

        var portal = new FakeCharacterPortal();
        var list = await Service(portal).GetCharactersAsync(session);
        check("char order: the list is read in display order, each character once",
            list is { Count: 4 } && list.Select(c => c.Guid).SequenceEqual([12u, 14u, 15u, 16u]) && portal.Authorized == 1);
        check("char order: a character is read with last played, online and its place",
            list![0] is { Name: "Pawster", Level: 60, Race: 1, Class: 1, Online: false, Order: 0 }
            && list[0].LastPlayed == DateTimeOffset.FromUnixTimeSeconds(1761400000)
            && list[1] is { Online: true, Order: 1 } && list[2].Order is null && list[3].LastPlayed is null);

        check("char order: names map to guids in their order, unknown names left out, no case",
            CharacterOrderService.GuidsFor(["zaph", "Nobody", "Pawster", "ZAPH"], list).SequenceEqual([16u, 12u]));

        portal.Status = 404;
        check("char order: a realm without character order answers null", await Service(portal).GetCharactersAsync(session) is null);
        portal.Status = 503;
        LauncherApiException? notSetUp = null;
        try { await Service(portal).GetCharactersAsync(session); } catch (LauncherApiException ex) { notSetUp = ex; }
        check("char order: a 503 list throws with the portal's words",
            notSetUp is { Status: HttpStatusCode.ServiceUnavailable, Message: "Character ordering isn't set up on this realm yet." });
        portal.Status = 200;
        LauncherApiException? expired = null;
        try { await Service(portal).GetCharactersAsync(session with { Token = "old" }); } catch (LauncherApiException ex) { expired = ex; }
        check("char order: an expired login throws 401", expired is { Status: HttpStatusCode.Unauthorized });

        var saved = await Service(portal).SaveAsync(session, [16u, 12u, 16u]);
        check("char order: a save posts the guids once each, as JSON", saved.Outcome == CharacterOrderOutcome.Saved
            && portal.LastSaveContentType == "application/json"
            && portal.LastSave?.GetProperty("order").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([16u, 12u]) == true
            && saved.Characters is { Count: 4 });

        async Task<CharacterOrderResult> SaveWith(int status, string json)
        {
            portal.SaveStatus = status;
            portal.SaveJson = json;
            return await Service(portal).SaveAsync(session, [12u]);
        }
        var notYours = await SaveWith(409, """{"error":"not_yours","message":"That isn't one of your characters any more."}""");
        check("char order: a 409 is a refusal with its code and message", notYours is { Outcome: CharacterOrderOutcome.Refused, Code: "not_yours",
            Message: "That isn't one of your characters any more." });
        var duplicate = await SaveWith(409, """{"error":"duplicate"}""");
        check("char order: a refusal without a message gets one for its code", duplicate is { Outcome: CharacterOrderOutcome.Refused, Code: "duplicate" }
            && duplicate.Message == CharacterOrderService.RefusalMessage("duplicate", null));
        check("char order: 400, 429 and 503 are not tried, with the portal's words",
            await SaveWith(400, """{"error":"Send the order as a list of character guids."}""") is { Outcome: CharacterOrderOutcome.NotTried, Message: "Send the order as a list of character guids." }
            && await SaveWith(429, """{"error":"Your last save is still going through. Try again in a moment."}""") is { Outcome: CharacterOrderOutcome.NotTried }
            && await SaveWith(503, """{"error":"Character ordering isn't available right now."}""") is { Outcome: CharacterOrderOutcome.NotTried, Message: "Character ordering isn't available right now." });
        check("char order: 401 is a logout, 404 unavailable, 502 unknown",
            await SaveWith(401, """{"error":"Your login has expired. Log in again."}""") is { Outcome: CharacterOrderOutcome.LoggedOut }
            && await SaveWith(404, """{"error":"Not found."}""") is { Outcome: CharacterOrderOutcome.Unavailable }
            && await SaveWith(502, "") is { Outcome: CharacterOrderOutcome.Unknown });
        check("char order: a 200 without the list may or may not have saved", await SaveWith(200, """{"ok":true}""") is { Outcome: CharacterOrderOutcome.Unknown });
        portal.SaveStatus = 200;
        portal.SaveJson = CharacterListJson;
        portal.Offline = true;
        portal.OfflineError = HttpRequestError.ConnectionError;
        check("char order: no connection is not tried", (await Service(portal).SaveAsync(session, [12u])).Outcome == CharacterOrderOutcome.NotTried);
        portal.OfflineError = HttpRequestError.Unknown;
        check("char order: a lost answer may or may not have saved", (await Service(portal).SaveAsync(session, [12u])).Outcome == CharacterOrderOutcome.Unknown);
        portal.Offline = false;

        // An order the character select screen left.
        portal.Saves = 0;
        var pending = await Service(portal).SendPendingAsync(session, ["Zaph", "Gone", "ironhide"]);
        check("char order: an order from the game is sent by guid, unknown names left out",
            pending.Outcome == CharacterOrderOutcome.Saved && portal.Saves == 1
            && portal.LastSave?.GetProperty("order").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([16u, 14u]) == true);
        var nothing = await Service(portal).SendPendingAsync(session, ["Gone"]);
        check("char order: an order of names the realm doesn't list has nothing to send", nothing.Outcome == CharacterOrderOutcome.Saved && portal.Saves == 1);
        portal.Status = 404;
        check("char order: a realm without character order leaves it", (await Service(portal).SendPendingAsync(session, ["Zaph"])).Outcome == CharacterOrderOutcome.Unavailable);
        portal.Status = 200;
        check("char order: an expired login is a logout",
            (await Service(portal).SendPendingAsync(session with { Token = "old" }, ["Zaph"])).Outcome == CharacterOrderOutcome.LoggedOut);

        // The page.
        portal = new FakeCharacterPortal();
        var page = new CharactersViewModel(Service(portal), session);
        var savedEvents = 0;
        page.OrderSaved += () => savedEvents++;
        await page.LoadAsync();
        string Order() => string.Join(",", page.Characters.Select(c => c.Name));
        check("char order page: the list shows in the realm's order, numbered",
            Order() == "Pawster,Ironhide,Oldhc,Zaph" && page.Characters.Select(c => c.Position).SequenceEqual([1, 2, 3, 4])
            && page is { HasCharacters: true, CanEdit: true, HasUnsavedChanges: false, CanSave: false });
        check("char order page: rows say race, class, level and last played",
            page.Characters[0] is { Detail: "Human Warrior", LevelText: "60" } && page.Characters[1].LastPlayedText == "Online now"
            && page.Characters[3].LastPlayedText == "Never played");
        page.SelectedCharacter = page.Characters[2];
        page.MoveUp();
        check("char order page: the picked character moves up", Order() == "Pawster,Oldhc,Ironhide,Zaph"
            && page.SelectedCharacter?.Name == "Oldhc" && page.Characters[1].Position == 2 && page is { HasUnsavedChanges: true, CanSave: true });
        page.MoveDown();
        check("char order page: and back down, which is the realm's order again", Order() == "Pawster,Ironhide,Oldhc,Zaph" && !page.HasUnsavedChanges);
        page.SelectedCharacter = page.Characters[0];
        check("char order page: the top one can't go up", !page.CanMoveUp && page.CanMoveDown);
        check("char order page: a drop puts the row in that place", page.Move(page.Characters[3], 0) && Order() == "Zaph,Pawster,Ironhide,Oldhc");
        page.Sort(CharacterSort.Name);
        check("char order page: sort by name", Order() == "Ironhide,Oldhc,Pawster,Zaph");
        page.Sort(CharacterSort.Level);
        check("char order page: sort by level", Order() == "Zaph,Pawster,Ironhide,Oldhc");
        page.Sort(CharacterSort.Class);
        check("char order page: sort by class, then level", Order() == "Oldhc,Zaph,Pawster,Ironhide");
        page.Sort(CharacterSort.LastPlayed);
        check("char order page: sort by last played, online first, never played last", Order() == "Ironhide,Pawster,Oldhc,Zaph");
        page.Revert();
        check("char order page: revert goes back to the realm's order", Order() == "Pawster,Ironhide,Oldhc,Zaph" && !page.HasUnsavedChanges);

        page.Sort(CharacterSort.Level);
        page.SetGameRunning(true);
        check("char order page: no editing while the game runs", page is { CanEdit: false, CanSave: false, CanMoveUp: false, CanMoveDown: false }
            && !page.Move(page.Characters[0], 3) && Order() == "Zaph,Pawster,Ironhide,Oldhc");
        page.SetGameRunning(false);
        portal.SaveJson = CharacterListJson.Replace("""{"guid":16,"name":"Zaph","race":10,"class":8,"gender":1,"level":70,"lastPlayed":0,"online":false,"order":null},""", "")
            .Replace("""{"characters":[""", """{"characters":[{"guid":16,"name":"Zaph","race":10,"class":8,"gender":1,"level":70,"lastPlayed":0,"online":false,"order":0},""");
        await page.SaveAsync();
        check("char order page: save sends the draft and shows what the realm has",
            portal.LastSave?.GetProperty("order").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([16u, 12u, 14u, 15u]) == true
            && Order() == "Zaph,Pawster,Ironhide,Oldhc" && !page.HasUnsavedChanges && savedEvents == 1 && page is { HasStatus: true, StatusIsError: false });

        page.Move(page.Characters[3], 0);
        portal.SaveStatus = 409;
        portal.SaveJson = """{"error":"not_yours","message":"That isn't one of your characters any more."}""";
        await page.SaveAsync();
        check("char order page: a refusal is shown and the draft stays", page is { StatusIsError: true, HasUnsavedChanges: true }
            && page.StatusMessage.StartsWith("That isn't one of your characters any more.", StringComparison.Ordinal)
            && Order() == "Oldhc,Zaph,Pawster,Ironhide" && savedEvents == 1);

        portal.Status = 404;
        var gone = false;
        page.Unavailable += () => gone = true;
        await page.LoadAsync();
        check("char order page: a 404 says the realm has no character order", gone);
        page.Dispose();
    }

    private sealed class FakeCharacterPortal : HttpMessageHandler
    {
        public const string Token = "characters-test-token";
        public int Status = 200;
        public string ListJson = CharacterListJson;
        public int SaveStatus = 200;
        public string SaveJson = CharacterListJson;
        public bool Offline;
        public HttpRequestError OfflineError = HttpRequestError.Unknown;
        public int Authorized, Unauthorized, Saves;
        public JsonElement? LastSave;
        public string? LastSaveContentType;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException(OfflineError, "No route to host.");
            if (request.RequestUri!.AbsolutePath != "/api/launcher/characters.php") return Json(404, """{"error":"Not found."}""");
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
            {
                Unauthorized++;
                return Json(401, """{"error":"Your login has expired. Log in again."}""");
            }
            Authorized++;
            if (request.Method == HttpMethod.Get)
            {
                return Json(Status, Status switch
                {
                    404 => """{"error":"Not found."}""",
                    503 => """{"error":"Character ordering isn't set up on this realm yet."}""",
                    _ => ListJson,
                });
            }
            Saves++;
            LastSaveContentType = request.Content?.Headers.ContentType?.MediaType;
            LastSave = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return Json(SaveStatus, SaveJson);
        }

        private static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
