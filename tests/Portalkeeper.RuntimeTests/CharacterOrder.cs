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

    /// <summary>A PE32 stand-in for Wow.exe: one .text section mapped like build 12340's, the guard at its place.</summary>
    private static byte[] StandInClient(uint pointerToRawData = 0x400, uint imageBase = 0x400000, bool longNamePatterns = true)
    {
        const int textSize = 0x64000;
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
        Convert.FromHexString("807DFF0AC745F800000000760B").CopyTo(bytes, CharacterListLimitService.GuardOffset);
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
        check("char limit: exactly the jbe at 0x64057 becomes a jmp", edited.Length == stock.Length && differences.Length == 1
            && differences[0] == 0x64057 && stock[0x64057] == 0x76 && edited[0x64057] == 0xEB);
        check("char limit: the file it replaced is kept", Backups().Length == 1 && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("char limit: a client that's set up is left alone", !CharacterListLimitService.Ensure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1
            && Directory.EnumerateFiles(client).Count() == 1);

        // Both edits, in either order, give the same file: a client torrent can carry it.
        File.WriteAllBytes(exe, stock);
        var longFirst = LongPatchNameService.Ensure(client, exe) && CharacterListLimitService.Ensure(client, exe);
        var both = File.ReadAllBytes(exe);
        File.WriteAllBytes(exe, stock);
        var limitFirst = CharacterListLimitService.Ensure(client, exe) && LongPatchNameService.Ensure(client, exe);
        check("char limit: with the long-name edit, either order gives the same file", longFirst && limitFirst
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(both)
            && LongPatchNameService.IsSetUp(exe) && CharacterListLimitService.IsSetUp(exe)
            && Enumerable.Range(0, stock.Length).Count(i => stock[i] != both[i]) == 3);
        // stock, stock + long names, stock + char limit: one backup each, keyed by what was replaced.
        check("char limit: every version it replaced is kept once", Backups().Length == 3);

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
        wrongGuard[CharacterListLimitService.GuardOffset + 3] = 0x0B; // cmp ..., 0x0b
        check("char limit: a different guard is refused", Refused(wrongGuard));
        var otherJump = (byte[])stock.Clone();
        otherJump[CharacterListLimitService.JumpOffset] = 0x75; // jne
        check("char limit: another jump is refused", Refused(otherJump));
        check("char limit: a file that isn't a PE is refused", Refused(Encoding.ASCII.GetBytes("not a client").Concat(new byte[0x70000]).ToArray()));
        check("char limit: a PE with another image base is refused", Refused(StandInClient(imageBase: 0x10000000)));
        check("char limit: a PE whose .text sits elsewhere in the file is refused", Refused(StandInClient(pointerToRawData: 0x600)));
        check("char limit: a refused file leaves no backup and no temp file", Backups().Length == backupsSoFar
            && Directory.EnumerateFiles(client).Count() == 2);
    }

    private static async Task CharacterOrderChecks(string root, Action<string, bool> check)
    {
        // accountList in Config.wtf: the login handoff's "portalkeeper:" value and the game's "evermoreorder:" one.
        const string name = CharacterOrderService.ConfigSetting;
        const string order = "evermoreorder:ALICE:4:2f2b11:4,1";
        check("char order: accountList is read without its quotes",
            RealmLaunchService.ConfigSetting("SET realmName \"Azeroth\"\nSET accountList \"" + order + "\"\n", name) == order);
        check("char order: a missing accountList is null, an empty one empty",
            RealmLaunchService.ConfigSetting("SET realmName \"Azeroth\"\n", name) is null
            && RealmLaunchService.ConfigSetting("SET accountList \"\"\r\nSET x \"1\"\r\n", name) == "");
        check("char order: the CVar name has no case, a longer name isn't it, the last line wins",
            RealmLaunchService.ConfigSetting("set ACCOUNTLIST \"a\"", name) == "a"
            && RealmLaunchService.ConfigSetting("SET accountListOld \"a\"", name) is null
            && RealmLaunchService.ConfigSetting("SET accountList \"a\"\nSET accountList \"b\"\n", name) == "b");

        var client = Path.Combine(root, "charorder");
        var wtf = Path.Combine(client, "WTF");
        Directory.CreateDirectory(wtf);
        var config = Path.Combine(wtf, "Config.wtf");
        check("char order: no Config.wtf, no value", RealmLaunchService.ReadConfigSetting(client, name) is null
            && !RealmLaunchService.ClearConfigSetting(client, name, order));

        File.WriteAllText(config, "SET realmName \"Azeroth\"\r\nSET accountList \"" + order + "\"\r\nSET gxWindow \"1\"\r\n");
        RealmLaunchService.ForgetGamePassword(client);
        check("char order: forgetting the game password leaves an order alone", RealmLaunchService.ReadConfigSetting(client, name) == order);
        check("char order: only a launcher password is blanked",
            RealmLaunchService.WithoutGamePassword("SET accountList \"portalkeeper:hunter2\"") == "SET accountList \"\""
            && RealmLaunchService.WithoutGamePassword("SET accountList \"" + order + "\"") == "SET accountList \"" + order + "\"");
        check("char order: a password or an order alone in accountList is no reason for a backup",
            RealmLaunchService.WithoutLauncherValues("SET a \"1\"\nSET accountList \"" + order + "\"\n")
                == RealmLaunchService.WithoutLauncherValues("SET a \"1\"\nSET accountList \"portalkeeper:hunter2\"\n")
            && RealmLaunchService.WithoutLauncherValues("SET a \"1\"\nSET accountList \"BOB\"\n")
                != RealmLaunchService.WithoutLauncherValues("SET a \"1\"\nSET accountList \"\"\n"));
        check("char order: a newer value isn't cleared", !RealmLaunchService.ClearConfigSetting(client, name, "evermoreorder:ALICE:4:2f2b11:1")
            && RealmLaunchService.ReadConfigSetting(client, name) == order);
        check("char order: the value sent is cleared, quoted, and nothing else changes",
            RealmLaunchService.ClearConfigSetting(client, name, order)
            && File.ReadAllText(config) == "SET realmName \"Azeroth\"\r\nSET accountList \"\"\r\nSET gxWindow \"1\"\r\n"
            && RealmLaunchService.ClearConfigSetting(client, name, order));

        // A Config.wtf that isn't UTF-8 keeps its bytes; one that is stays UTF-8.
        var latin1 = Encoding.Latin1.GetBytes("SET realmName \"Café\"\r\nSET accountList \"" + order + "\"\r\n");
        File.WriteAllBytes(config, latin1);
        check("char order: a Latin-1 Config.wtf keeps its other bytes when an order is cleared",
            RealmLaunchService.ClearConfigSetting(client, name, order)
            && File.ReadAllBytes(config).AsSpan().SequenceEqual(Encoding.Latin1.GetBytes("SET realmName \"Café\"\r\nSET accountList \"\"\r\n")));
        File.WriteAllBytes(config, Encoding.UTF8.GetBytes("SET realmName \"Ærith\"\r\nSET accountList \"" + order + "\"\r\n"));
        check("char order: a UTF-8 Config.wtf stays UTF-8",
            RealmLaunchService.ClearConfigSetting(client, name, order)
            && File.ReadAllBytes(config).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("SET realmName \"Ærith\"\r\nSET accountList \"\"\r\n")));

        // The list hash, as the character select screen makes it.
        check("char order: the list hash matches the game's (Ragnar, Pawster)",
            CharacterOrderService.FormatHash(CharacterOrderService.ListHash(["Ragnar", "Pawster"])) == "9c89f9");
        check("char order: the list hash is over UTF-8 bytes (Ærith, Bob)",
            CharacterOrderService.FormatHash(CharacterOrderService.ListHash(["Ærith", "Bob"])) == "f1a751");
        var hash = CharacterOrderService.FormatHash(CharacterOrderService.ListHash(["Pawster", "Ironhide", "Oldhc", "Zaph"]));

        // The value: "evermoreorder:ACCOUNT:COUNT:HASH:p1,p2,..."
        check("char order: the account's order is read, the account without case",
            CharacterOrderService.ParsePending($"evermoreorder:alice:4:{hash}:4,1,3", "ALICE") is { Count: 4, Account: "alice" } read
            && read.Positions.SequenceEqual([4, 1, 3]) && CharacterOrderService.FormatHash(read.Hash) == hash);
        check("char order: a partial list is an order",
            CharacterOrderService.ParsePending($"evermoreorder:ALICE:4:{hash}:2", "ALICE") is { Count: 4 } partial && partial.Positions.SequenceEqual([2]));
        check("char order: another account's order is still read as theirs",
            CharacterOrderService.ParseOrder($"evermoreorder:BOB:3:{hash}:2,1") is { Account: "BOB", Count: 3 }
            && CharacterOrderService.ParsePending($"evermoreorder:BOB:3:{hash}:2,1", "ALICE") is null);
        check("char order: a login handoff, no prefix, no hash (the old form): nothing pending",
            CharacterOrderService.ParseOrder("portalkeeper:hunter2") is null
            && CharacterOrderService.ParseOrder($"ALICE:4:{hash}:2,1") is null
            && CharacterOrderService.ParseOrder("evermoreorder:ALICE:4:2,1") is null
            && CharacterOrderService.ParseOrder("") is null && CharacterOrderService.ParseOrder(null) is null
            && CharacterOrderService.ParsePending($"evermoreorder:ALICE:4:{hash}:2,1", null) is null);
        check("char order: positions that can't be are refused: 0, past the count, twice, not a number",
            CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:0,1") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:5") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:2,2") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:2,x") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:-1") is null);
        check("char order: the four-part value says nothing about favourites",
            CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1") is { Favourites: null });
        check("char order: a fifth part is how many of the first positions are favourites",
            CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1,3,2:2") is { Favourites: 2 } starred
            && starred.Positions.SequenceEqual([4, 1, 3, 2])
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1,3,2:0") is { Favourites: 0 }
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1,3,2:4") is { Favourites: 4 });
        check("char order: favourites past the positions given are all of them, as the screen reads it",
            CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1:3") is { Favourites: 2 });
        check("char order: a favourites count past COUNT, not a number, empty, or a sixth part: nothing pending",
            CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1:5") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1:x") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1:-1") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1:") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:4,1:1:1") is null);
        check("char order: no count, a bad hash, no positions, no account: nothing pending",
            CharacterOrderService.ParseOrder($"evermoreorder:ALICE:0:{hash}:") is null
            && CharacterOrderService.ParseOrder("evermoreorder:ALICE:4:zzzzzz:1") is null
            && CharacterOrderService.ParseOrder($"evermoreorder:ALICE:4:{hash}:") is null
            && CharacterOrderService.ParseOrder($"evermoreorder::4:{hash}:1") is null);

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

        PendingCharacterPositions Positions(int count, string listHash, params int[] at) =>
            CharacterOrderService.ParseOrder($"evermoreorder:ALICE:{count}:{listHash}:{string.Join(",", at)}")!;
        check("char order: positions map to the guids at those places in the realm's list",
            CharacterOrderService.GuidsFor(Positions(4, hash, 4, 1), list)?.SequenceEqual([16u, 12u]) == true);
        var otherHash = CharacterOrderService.FormatHash(CharacterOrderService.ListHash(["Pawster", "Oldhc", "Ironhide", "Zaph"]));
        check("char order: a list of another length, or the same length in another order, is stale",
            CharacterOrderService.GuidsFor(Positions(5, hash, 4, 1), list) is null
            && CharacterOrderService.GuidsFor(Positions(3, hash, 1), list) is null
            && CharacterOrderService.GuidsFor(Positions(4, otherHash, 4, 1), list) is null);

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
        check("char order: a save without favourites doesn't send them (the realm keeps its own)",
            portal.LastSave?.TryGetProperty("favourites", out _) == false);
        await Service(portal).SaveAsync(session, [16u, 12u], [16u, 16u]);
        check("char order: a save with favourites sends them once each",
            portal.LastSave?.GetProperty("favourites").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([16u]) == true);
        await Service(portal).SaveAsync(session, [16u, 12u], []);
        check("char order: no favourites is an empty list, which clears them",
            portal.LastSave?.GetProperty("favourites").GetArrayLength() == 0);
        check("char order: a list without favourites (an older portal) reads none", list!.All(c => !c.Favourite));
        portal.ListJson = CharacterListJson.Replace("\"order\":1}", "\"order\":1,\"favourite\":true}");
        check("char order: a character's favourite is read",
            (await Service(portal).GetCharactersAsync(session))?.Select(c => c.Favourite).SequenceEqual([false, true, false, false]) == true);
        portal.ListJson = CharacterListJson;

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

        // An order the character select screen left (CharacterOrderSync).
        var syncClient = Path.Combine(root, "charorder-sync");
        Directory.CreateDirectory(Path.Combine(syncClient, "WTF"));
        var syncConfig = Path.Combine(syncClient, "WTF", "Config.wtf");
        void SetAccountList(string value) => File.WriteAllText(syncConfig, "SET realmName \"Azeroth\"\r\nSET accountList \"" + value + "\"\r\n");
        string? AccountList() => RealmLaunchService.ReadConfigSetting(syncClient, name);
        var syncSettings = new PortalkeeperSettings();
        var settingsSaves = 0;
        CharacterOrderSync NewSync(FakeCharacterPortal p) => new(Service(p), () => syncSettings, () => settingsSaves++);
        uint[] Posted(FakeCharacterPortal p) => p.LastSave?.GetProperty("order").EnumerateArray().Select(e => e.GetUInt32()).ToArray() ?? [];
        bool Always() => true;
        string AliceOrder(string positions, string? listHash = null) => $"evermoreorder:ALICE:4:{listHash ?? hash}:{positions}";

        portal = new FakeCharacterPortal();
        var sync = NewSync(portal);
        check("char order sync: nothing waiting, nothing sent", await sync.SyncAsync(session, syncClient, Always) is null && portal.Saves == 0);

        SetAccountList(AliceOrder("4,2"));
        var sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: the game's order is sent as guids and accountList emptied",
            sent?.Outcome == CharacterOrderOutcome.Saved && Posted(portal).SequenceEqual([16u, 14u]) && AccountList() == ""
            && sync.Waiting("alice") is null && settingsSaves > 0);

        SetAccountList(AliceOrder("4,2,1:2"));
        sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: favourites in the game's order are sent as the first guids",
            sent?.Outcome == CharacterOrderOutcome.Saved && Posted(portal).SequenceEqual([16u, 14u, 12u])
            && portal.LastSave?.GetProperty("favourites").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([16u, 14u]) == true
            && AccountList() == "");
        SetAccountList(AliceOrder("4,2:0"));
        sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: no favourites in the game's order clears the realm's",
            sent?.Outcome == CharacterOrderOutcome.Saved && portal.LastSave?.GetProperty("favourites").GetArrayLength() == 0);
        SetAccountList(AliceOrder("4,2"));
        sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: an order without the favourites part leaves the realm's alone",
            sent?.Outcome == CharacterOrderOutcome.Saved && portal.LastSave?.TryGetProperty("favourites", out _) == false);

        // Out of time during the POST: the guids are kept, accountList is already empty.
        portal = new FakeCharacterPortal { HoldSave = new TaskCompletionSource() };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("3,1"));
        using (var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            check("char order sync: out of time during the POST answers nothing",
                await sync.SyncAsync(session, syncClient, Always, shortWait.Token) is null);
        check("char order sync: the guids are kept and accountList is emptied anyway",
            sync.Waiting("ALICE") is { } timedOut && timedOut.Guids.SequenceEqual([15u, 12u]) && timedOut.SourceValue == AliceOrder("3,1")
            && AccountList() == "");
        portal.HoldSave = null;
        var listings = portal.Listings;
        sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: the next try sends the kept guids, without asking for the list again",
            sent?.Outcome == CharacterOrderOutcome.Saved && Posted(portal).SequenceEqual([15u, 12u]) && portal.Listings == listings
            && sync.Waiting("ALICE") is null);

        // No answer (502) after the realm applied it: no second scramble.
        portal = new FakeCharacterPortal { SaveStatus = 502, SaveJson = "" };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("4,1"));
        check("char order sync: a lost answer keeps the guids", (await sync.SyncAsync(session, syncClient, Always))?.Outcome == CharacterOrderOutcome.Unknown
            && sync.Waiting("ALICE")?.Guids.SequenceEqual([16u, 12u]) == true && AccountList() == "");
        var applied = """
            {"characters":[
              {"guid":16,"name":"Zaph","race":10,"class":8,"gender":1,"level":70,"lastPlayed":0,"online":false,"order":0},
              {"guid":12,"name":"Pawster","race":1,"class":1,"gender":0,"level":60,"lastPlayed":1761400000,"online":false,"order":1},
              {"guid":14,"name":"Ironhide","race":3,"class":1,"gender":0,"level":30,"lastPlayed":1761300000,"online":true,"order":null},
              {"guid":15,"name":"Oldhc","race":2,"class":3,"gender":1,"level":12,"lastPlayed":1761000000,"online":false,"order":null}]}
            """;
        portal.ListJson = applied;
        portal.SaveStatus = 200;
        portal.SaveJson = applied;
        SetAccountList(AliceOrder("4,1")); // the same value back (the game wrote it again)
        sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: the same value again is the kept order: its guids are sent, not its positions",
            sent?.Outcome == CharacterOrderOutcome.Saved && Posted(portal).SequenceEqual([16u, 12u]) && AccountList() == "");
        var saves = portal.Saves;
        SetAccountList(AliceOrder("4,1")); // once more, after the realm has it: its hash is the old list's
        sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: an order the realm already applied is stale and not sent",
            sent?.Outcome == CharacterOrderOutcome.Stale && portal.Saves == saves && AccountList() == "" && sync.Waiting("ALICE") is null);

        // Config.wtf is left alone when it may not be written (a launch under way).
        portal = new FakeCharacterPortal();
        sync = NewSync(portal);
        SetAccountList(AliceOrder("2"));
        sent = await sync.SyncAsync(session, syncClient, () => false);
        check("char order sync: with a launch under way accountList isn't written",
            sent?.Outcome == CharacterOrderOutcome.Saved && AccountList() == AliceOrder("2"));
        SetAccountList("");

        // One at a time: a launch waits for its turn only as long as it may.
        portal = new FakeCharacterPortal { HoldSave = new TaskCompletionSource() };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("1,4"));
        var first = sync.SyncAsync(session, syncClient, Always);
        await portal.SaveArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        listings = portal.Listings;
        CharacterOrderResult? second;
        using (var launchWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            second = await sync.SyncAsync(session, syncClient, Always, launchWait.Token);
        check("char order sync: a launch that doesn't get its turn in time goes on without it",
            second is null && portal.Listings == listings && !first.IsCompleted);
        portal.HoldSave.SetResult();
        check("char order sync: the sync that had the turn finishes", (await first)?.Outcome == CharacterOrderOutcome.Saved);

        // A page save while an after-game send is on its way: it waits, and the realm ends with the page's order.
        portal = new FakeCharacterPortal { HoldSave = new TaskCompletionSource() };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("2,3"));
        var afterGame = sync.SyncAsync(session, syncClient, Always);
        await portal.SaveArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pageSave = sync.SavePageOrderAsync(session, [15u, 16u, 12u, 14u], null, syncClient, Always);
        check("char order sync: a page save waits for the game's order on its way", !pageSave.IsCompleted && portal.Saves == 1);
        portal.HoldSave.SetResult();
        var overtaken = await afterGame;
        var pageResult = await pageSave;
        check("char order sync: the realm ends with the page's order, and the overtaken sync changes nothing after it",
            overtaken is null && pageResult.Outcome == CharacterOrderOutcome.Saved && portal.Applied.Count == 2
            && portal.Applied[0].SequenceEqual([14u, 15u]) && portal.Applied[^1].SequenceEqual([15u, 16u, 12u, 14u])
            && sync.Waiting("ALICE") is null && AccountList() == "");

        // Every account keeps its own.
        portal = new FakeCharacterPortal { SaveStatus = 503, SaveJson = """{"error":"Character ordering isn't available right now."}""" };
        sync = NewSync(portal);
        syncSettings.PendingCharacterOrders.Clear();
        var bobOrder = $"evermoreorder:BOB:3:{hash}:2,1";
        syncSettings.PendingCharacterOrders["ALICE"] = new PendingCharacterOrder { SourceValue = AliceOrder("1"), Guids = [12] };
        SetAccountList(bobOrder);
        sent = await sync.SyncAsync(session, syncClient, Always);
        check("char order sync: another account's order is kept for it, Config.wtf untouched, this account's kept too",
            sent?.Outcome == CharacterOrderOutcome.NotTried && sync.Waiting("bob")?.SourceValue == bobOrder
            && sync.Waiting("bob")?.Guids.Count == 0 && sync.Waiting("ALICE")?.Guids.SequenceEqual([12u]) == true && AccountList() == bobOrder);
        portal.SaveStatus = 200;
        portal.SaveJson = CharacterListJson;
        sent = await sync.SyncAsync(session with { AccountName = "bob" }, syncClient, Always);
        check("char order sync: that account's order is checked against its own list when it logs in (3 there, 4 here: stale)",
            sent?.Outcome == CharacterOrderOutcome.Stale && sync.Waiting("BOB") is null && AccountList() == ""
            && sync.Waiting("ALICE") is not null);

        // What ends a waiting order and what doesn't.
        syncSettings.PendingCharacterOrders.Clear();
        portal = new FakeCharacterPortal { SaveStatus = 409, SaveJson = """{"error":"not_yours","message":"That isn't one of your characters any more."}""" };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("1"));
        check("char order sync: a character that isn't the account's any more ends it",
            (await sync.SyncAsync(session, syncClient, Always))?.Outcome == CharacterOrderOutcome.Refused && sync.Waiting("ALICE") is null);
        portal = new FakeCharacterPortal { SaveStatus = 409, SaveJson = """{"error":"duplicate","message":"A character is in the order twice."}""" };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("1"));
        check("char order sync: any other refusal (409) ends it too, not sent forever",
            (await sync.SyncAsync(session, syncClient, Always))?.Outcome == CharacterOrderOutcome.Refused && sync.Waiting("ALICE") is null
            && await sync.SyncAsync(session, syncClient, Always) is null && portal.Saves == 1);
        portal = new FakeCharacterPortal { SaveStatus = 400, SaveJson = """{"error":"Send the order as a list of character guids."}""" };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("1"));
        check("char order sync: a malformed order (400) ends it",
            (await sync.SyncAsync(session, syncClient, Always)) is { Outcome: CharacterOrderOutcome.NotTried, Code: CharacterOrderService.BadRequestCode }
            && sync.Waiting("ALICE") is null && await sync.SyncAsync(session, syncClient, Always) is null && portal.Saves == 1);
        portal = new FakeCharacterPortal { SaveStatus = 429, SaveJson = """{"error":"Your last save is still going through. Try again in a moment."}""" };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("1"));
        check("char order sync: still saving keeps it for the next try",
            (await sync.SyncAsync(session, syncClient, Always))?.Outcome == CharacterOrderOutcome.NotTried && sync.Waiting("ALICE")?.Guids.Count == 1);
        syncSettings.PendingCharacterOrders.Clear();
        SetAccountList(AliceOrder("1", "000000"));
        check("char order sync: an order made against another list is stale: dropped, not sent",
            (await sync.SyncAsync(session, syncClient, Always))?.Outcome == CharacterOrderOutcome.Stale
            && sync.Waiting("ALICE") is null && AccountList() == "");
        portal = new FakeCharacterPortal { Status = 503 };
        sync = NewSync(portal);
        SetAccountList(AliceOrder("1"));
        check("char order sync: no list yet keeps the value to translate later",
            (await sync.SyncAsync(session, syncClient, Always))?.Outcome == CharacterOrderOutcome.NotTried
            && sync.Waiting("ALICE") is { Guids.Count: 0 } untranslated && untranslated.SourceValue == AliceOrder("1") && AccountList() == AliceOrder("1"));
        check("char order sync: an expired login is a logout, the order kept",
            (await sync.SyncAsync(session with { Token = "old" }, syncClient, Always))?.Outcome == CharacterOrderOutcome.LoggedOut
            && sync.Waiting("ALICE") is not null);
        portal.SaveStatus = 503;
        portal.SaveJson = """{"error":"Character ordering isn't available right now."}""";
        check("char order sync: a page save the realm didn't take keeps the waiting order",
            (await sync.SavePageOrderAsync(session, [12u], null, syncClient, Always)).Outcome == CharacterOrderOutcome.NotTried
            && sync.Waiting("ALICE") is not null && AccountList() == AliceOrder("1"));
        portal.SaveStatus = 200;
        portal.SaveJson = CharacterListJson;
        check("char order sync: a page save drops the waiting order and the account's accountList",
            (await sync.SavePageOrderAsync(session, [12u], null, syncClient, Always)).Outcome == CharacterOrderOutcome.Saved
            && sync.Waiting("ALICE") is null && AccountList() == "");
        File.WriteAllText(syncConfig, "SET accountList \"portalkeeper:hunter2\"\r\n");
        await sync.SavePageOrderAsync(session, [12u], null, syncClient, Always);
        check("char order sync: a page save never touches a login handoff", AccountList() == "portalkeeper:hunter2");
        var reloaded = new SettingsService(Path.Combine(root, "charorder-settings2", "settings.json"));
        var kept = reloaded.Load();
        kept.PendingCharacterOrders["ALICE"] = new PendingCharacterOrder { SourceValue = AliceOrder("2"), Guids = [14] };
        kept.PendingCharacterOrders["BOB"] = new PendingCharacterOrder { SourceValue = AliceOrder("2,1:1"), Guids = [14, 12], FavouriteGuids = [14] };
        reloaded.Save(kept);
        check("char order sync: a waiting order survives a restart, with its favourites or without",
            reloaded.Load().PendingCharacterOrders is var back
            && back.GetValueOrDefault("ALICE") is { FavouriteGuids: null } alice && alice.Guids.SequenceEqual([14u]) && alice.SourceValue == AliceOrder("2")
            && back.GetValueOrDefault("BOB")?.FavouriteGuids?.SequenceEqual([14u]) == true);

        // The page.
        portal = new FakeCharacterPortal();
        var page = new CharactersViewModel(Service(portal), session);
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
        var pageSaves = 0;
        page = new CharactersViewModel(Service(portal), session, save: (guids, favourites, token) => { pageSaves++; return Service(portal).SaveAsync(session, guids, favourites, token); });
        await page.LoadAsync();
        page.Sort(CharacterSort.Level);
        await page.SaveAsync();
        check("char order page: SAVE goes through the save it's given", pageSaves == 1);
        check("char order page: save sends the draft and shows what the realm has",
            portal.LastSave?.GetProperty("order").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([16u, 12u, 14u, 15u]) == true
            && Order() == "Zaph,Pawster,Ironhide,Oldhc" && !page.HasUnsavedChanges && page is { HasStatus: true, StatusIsError: false });

        page.Move(page.Characters[3], 0);
        portal.SaveStatus = 409;
        portal.SaveJson = """{"error":"not_yours","message":"That isn't one of your characters any more."}""";
        await page.SaveAsync();
        check("char order page: a refusal is shown and the draft stays", page is { StatusIsError: true, HasUnsavedChanges: true }
            && page.StatusMessage.StartsWith("That isn't one of your characters any more.", StringComparison.Ordinal)
            && Order() == "Oldhc,Zaph,Pawster,Ironhide");

        // Favourites on the page: first, like the character select screen.
        var starPortal = new FakeCharacterPortal
        {
            ListJson = CharacterListJson.Replace("\"order\":null}", "\"order\":null,\"favourite\":true}"),
        };
        var starPage = new CharactersViewModel(Service(starPortal), session);
        await starPage.LoadAsync();
        string Stars() => string.Join(",", starPage.Characters.Select(c => (c.IsFavourite ? "*" : "") + c.Name));
        check("char order page: the realm's favourites show first, as the screen shows them, nothing to save",
            Stars() == "*Oldhc,*Zaph,Pawster,Ironhide" && starPage.FavouriteCount == 2 && !starPage.HasUnsavedChanges);
        starPage.ToggleFavourite(starPage.Characters[3]);
        check("char order page: starring puts it last of the favourites", Stars() == "*Oldhc,*Zaph,*Ironhide,Pawster"
            && starPage.HasUnsavedChanges && starPage.SelectedCharacter?.Name == "Ironhide");
        starPage.ToggleFavourite(starPage.Characters[0]);
        check("char order page: unstarring puts it right under them", Stars() == "*Zaph,*Ironhide,Oldhc,Pawster");
        starPage.Move(starPage.Characters[3], 0);
        check("char order page: a row dropped among the favourites is one", Stars() == "*Pawster,*Zaph,*Ironhide,Oldhc");
        starPage.Move(starPage.Characters[0], 3);
        check("char order page: one dropped under them isn't", Stars() == "*Zaph,*Ironhide,Oldhc,Pawster");
        starPage.SelectedCharacter = starPage.Characters[1];
        starPage.MoveDown();
        check("char order page: a step down from the last favourite only crosses the line (unstars)", Stars() == "*Zaph,Ironhide,Oldhc,Pawster");
        starPage.MoveUp();
        check("char order page: a step up from under the line only crosses it (stars)", Stars() == "*Zaph,*Ironhide,Oldhc,Pawster");
        starPage.Sort(CharacterSort.Name);
        check("char order page: a sort keeps the favourites on top", Stars() == "*Ironhide,*Zaph,Oldhc,Pawster");
        starPage.Revert();
        check("char order page: revert brings back the realm's order and stars", Stars() == "*Oldhc,*Zaph,Pawster,Ironhide" && !starPage.HasUnsavedChanges);
        starPage.ToggleFavourite(starPage.Characters[1]);
        starPage.ToggleFavourite(starPage.Characters[1]);
        check("char order page: unstar and star again, same place: nothing to save", Stars() == "*Oldhc,*Zaph,Pawster,Ironhide" && !starPage.HasUnsavedChanges);
        starPage.SelectedCharacter = starPage.Characters[1];
        starPage.MoveDown();
        check("char order page: a star alone is a change", Stars() == "*Oldhc,Zaph,Pawster,Ironhide" && starPage is { HasUnsavedChanges: true, CanSave: true });
        starPortal.SaveJson = starPortal.ListJson.Replace("\"name\":\"Zaph\",\"race\":10,\"class\":8,\"gender\":1,\"level\":70,\"lastPlayed\":0,\"online\":false,\"order\":null,\"favourite\":true",
            "\"name\":\"Zaph\",\"race\":10,\"class\":8,\"gender\":1,\"level\":70,\"lastPlayed\":0,\"online\":false,\"order\":null");
        await starPage.SaveAsync();
        check("char order page: SAVE sends the order and the favourites",
            starPortal.LastSave?.GetProperty("order").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([15u, 16u, 12u, 14u]) == true
            && starPortal.LastSave?.GetProperty("favourites").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([15u]) == true
            && !starPage.HasUnsavedChanges);
        starPage.ToggleFavourite(starPage.Characters[1]);
        await starPage.SaveAsync();
        check("char order page: stars the realm didn't keep (an older core) are said, not called saved",
            starPage is { StatusIsError: true, HasUnsavedChanges: false } && starPage.StatusMessage.Contains("favourites", StringComparison.Ordinal)
            && Stars() == "*Oldhc,Pawster,Ironhide,Zaph");
        starPage.SetGameRunning(true);
        check("char order page: no starring while the game runs", !starPage.CanStar && !starPage.ToggleFavourite(starPage.Characters[2]));
        starPage.Dispose();

        var onePortal = new FakeCharacterPortal
        {
            ListJson = """{"characters":[{"guid":12,"name":"Pawster","race":1,"class":1,"gender":0,"level":60,"lastPlayed":0,"online":false,"order":0}]}""",
        };
        onePortal.SaveJson = onePortal.ListJson.Replace("\"order\":0}", "\"order\":0,\"favourite\":true}");
        var onePage = new CharactersViewModel(Service(onePortal), session);
        await onePage.LoadAsync();
        onePage.ToggleFavourite(onePage.Characters[0]);
        check("char order page: one character can't move but can be starred and saved", onePage is { CanEdit: false, CanSave: true });
        await onePage.SaveAsync();
        check("char order page: and the star is saved",
            onePortal.LastSave?.GetProperty("favourites").EnumerateArray().Select(e => e.GetUInt32()).SequenceEqual([12u]) == true
            && onePage is { HasUnsavedChanges: false, StatusIsError: false });
        onePage.Dispose();

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
        public int Authorized, Unauthorized, Listings, Saves;
        /// <summary>Every POSTed order, in the order the stand-in realm applied them.</summary>
        public readonly List<uint[]> Applied = [];
        /// <summary>A POST waits for this (or its cancellation) once it has arrived.</summary>
        public TaskCompletionSource? HoldSave;
        public readonly TaskCompletionSource SaveArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                Listings++;
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
            SaveArrived.TrySetResult();
            if (HoldSave is { } hold) await hold.Task.WaitAsync(cancellationToken);
            if (SaveStatus == 200) Applied.Add(LastSave.Value.GetProperty("order").EnumerateArray().Select(e => e.GetUInt32()).ToArray());
            return Json(SaveStatus, SaveJson);
        }

        private static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
