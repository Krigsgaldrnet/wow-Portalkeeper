using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <summary>What one login had: the character and the addons it had loaded ("DragonUI 2.19").</summary>
public sealed record LuaErrorSession(string Character, string Realm, string Build, string Locale, long? Started, IReadOnlyList<string> Addons);

/// <param name="Kind">"error", or "blocked" for an action the game stopped an addon from taking.</param>
/// <param name="Session">Its login's place in <see cref="LuaErrorBatch.Sessions"/>, or null.</param>
public sealed record LuaError(string Kind, string Message, string Stack, string Locals, long Count, long? First, long? Last, int? Session);

/// <param name="Dropped">Errors past the addon's limit, only counted.</param>
/// <param name="Suppressed">Errors in a storm or raised while one was handled, only counted.</param>
public sealed record LuaErrorBatch(IReadOnlyList<LuaErrorSession> Sessions, IReadOnlyList<LuaError> Errors, long Dropped, long Suppressed);

/// <param name="Sent">Distinct errors the portal confirmed.</param>
/// <param name="Left">Files that wait for the next run: the portal couldn't be reached or asked to wait.</param>
public sealed record LuaErrorSendResult(int Sent, int Left);

/// <summary>
/// Lua errors from the player's game, for players who leave "Send Lua errors to the realm" on. Two
/// halves:
///
/// The launcher writes its own addon into the client (Interface/AddOns/!PortalkeeperErrors, from
/// Addons/PortalkeeperErrors) while the option is on, and deletes it when it's off. The addon records
/// each distinct error once in its SavedVariables, which the game writes when it logs out or closes.
///
/// After the game closes those files (one per game account under WTF/Account) are read and sent to
/// the portal's luaerrors.php, one request each, and deleted once the portal confirms. A file the
/// portal itself says it can never take is deleted too; anything else stays for the next run.
/// </summary>
public sealed class LuaErrorService
{
    public const string AddonFolder = "!PortalkeeperErrors";
    public const string Endpoint = "luaerrors.php";

    // The addon's limits make a file of a few hundred KB at the very most.
    public const int MaxFileBytes = 2 << 20;

    private const string SavedVariable = "PortalkeeperErrorsDB";
    private const int SavedFormat = 1;
    private const string ResourcePrefix = "Portalkeeper.Addons.PortalkeeperErrors.";
    // A file this new may be one the game is still writing: it isn't judged broken yet.
    private static readonly TimeSpan BrokenFileGrace = TimeSpan.FromMinutes(1);
    // Non-English text as it is, not six bytes a letter: the portal limits a request's size.
    private static readonly JsonSerializerOptions SendOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly Uri _api;
    private readonly Func<DateTimeOffset> _now;

    public LuaErrorService(Uri? api = null, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // ---------------------------------------------------------
    // The addon
    // ---------------------------------------------------------

    /// <summary>
    /// Writes the addon into <paramref name="clientDirectory"/> (the folder the game runs from) when
    /// <paramref name="enabled"/>, and deletes its folder otherwise. The realm's addon list never names
    /// this folder, so nothing else in the launcher installs, checks or removes it.
    /// </summary>
    public static void SyncAddon(string clientDirectory, bool enabled)
    {
        var folder = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", AddonFolder));
        ManagedRuntimeWriteGuard.Check(clientDirectory, folder);
        if (!enabled)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return;
        }
        Directory.CreateDirectory(folder);
        WriteIfChanged(Path.Combine(folder, AddonFolder + ".toc"), AddonFile("PortalkeeperErrors.toc"));
        WriteIfChanged(Path.Combine(folder, "Errors.lua"), AddonFile("Errors.lua"));
    }

    private static byte[] AddonFile(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidDataException("The launcher's error addon is missing: " + name);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }

    // ---------------------------------------------------------
    // What the addon recorded
    // ---------------------------------------------------------

    /// <summary>The addon's SavedVariables, one per game account, whatever the folders' case on a case-sensitive file system.</summary>
    public static IReadOnlyList<string> FindFiles(string clientDirectory)
    {
        var files = new List<string>();
        var accounts = Child(Child(clientDirectory, "WTF"), "Account");
        if (accounts is null) return files;
        foreach (var account in Directory.EnumerateDirectories(accounts))
        {
            var saved = Child(account, "SavedVariables");
            if (saved is null) continue;
            var file = Directory.EnumerateFiles(saved)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), AddonFolder + ".lua", StringComparison.OrdinalIgnoreCase));
            if (file is not null) files.Add(file);
        }
        return files;
    }

    private static string? Child(string? parent, string name)
    {
        if (parent is null || !Directory.Exists(parent)) return null;
        return Directory.EnumerateDirectories(parent)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes the file and the backup the game keeps next to it. A file that won't go is tried again next time.</summary>
    public static void DeleteFile(string file)
    {
        foreach (var path in new[] { file, file + ".bak" })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes what the addon recorded in every game account: the option is off.</summary>
    public static void DeletePending(string clientDirectory)
    {
        foreach (var file in FindFiles(clientDirectory)) DeleteFile(file);
    }

    /// <summary>
    /// What a SavedVariables file holds, or null when it holds no errors or isn't what the addon writes.
    /// Text the game stored as anything but UTF-8 comes through with replacement characters.
    /// </summary>
    public static LuaErrorBatch? ReadBatch(byte[] file)
    {
        if (file.Length > MaxFileBytes) return null;
        LuaTable? saved;
        try { saved = LuaSavedVariables.Read(file, SavedVariable) as LuaTable; }
        catch (FormatException) { return null; }
        if (saved is null || Whole(saved.Field("format")) != SavedFormat) return null;

        var sessions = new List<LuaErrorSession>();
        var sessionPlaces = new Dictionary<long, int>();
        if (saved.Field("sessions") is LuaTable savedSessions)
        {
            foreach (var (position, value) in savedSessions.Numbered)
            {
                if (value is not LuaTable session) continue;
                sessionPlaces[position] = sessions.Count;
                var addons = session.Field("addons") is LuaTable list
                    ? list.Numbered.Select(a => a.Value).OfType<string>().ToArray()
                    : [];
                sessions.Add(new LuaErrorSession(Text(session, "character"), Text(session, "realm"), Text(session, "build"),
                    Text(session, "locale"), Whole(session.Field("started")), addons));
            }
        }

        var errors = new List<LuaError>();
        if (saved.Field("errors") is LuaTable savedErrors)
        {
            foreach (var (_, value) in savedErrors.Numbered)
            {
                if (value is not LuaTable error || Text(error, "message").Length == 0) continue;
                int? place = Whole(error.Field("session")) is { } position && sessionPlaces.TryGetValue(position, out var found) ? found : null;
                errors.Add(new LuaError(Text(error, "kind") == "blocked" ? "blocked" : "error", Text(error, "message"),
                    Text(error, "stack"), Text(error, "locals"), Math.Max(1, Whole(error.Field("count")) ?? 1),
                    Whole(error.Field("first")), Whole(error.Field("last")), place));
            }
        }
        if (errors.Count == 0) return null;
        return new LuaErrorBatch(sessions, errors, Math.Max(0, Whole(saved.Field("dropped")) ?? 0), Math.Max(0, Whole(saved.Field("suppressed")) ?? 0));
    }

    private static string Text(LuaTable table, string field) => table.Field(field) as string ?? "";

    private static long? Whole(object? value) =>
        value is double number && number >= long.MinValue && number <= long.MaxValue ? (long)number : null;

    // ---------------------------------------------------------
    // The portal
    // ---------------------------------------------------------

    /// <summary>
    /// Whether the realm takes Lua errors. False when the portal says it doesn't (404 with its answer);
    /// throws <see cref="LauncherApiException"/>, HttpRequestException or TaskCanceledException when it
    /// couldn't be asked, which includes a 404 from something in front of it.
    /// </summary>
    public async Task<bool> IsOfferedAsync(LauncherSession session, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, session.Token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return true;
        var answer = await PortalAnswerAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && answer is not null) return false;
        throw Error(response, answer);
    }

    /// <summary>
    /// Sends one batch. Sending the same batch again is harmless: the portal keeps it once. Throws
    /// <see cref="LuaErrorBatchRefusedException"/> when the portal says it can never take this batch,
    /// <see cref="LauncherApiException"/> when it refused for now and HttpRequestException /
    /// TaskCanceledException when it couldn't be reached.
    /// </summary>
    public async Task SendAsync(LauncherSession session, LuaErrorBatch batch, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Post, session.Token);
        request.Content = JsonContent.Create(new SendRequest("Portalkeeper/" + RealmConfigurationService.CurrentVersion,
            batch.Dropped, batch.Suppressed,
            batch.Sessions.Select(s => new SendSession(s.Character, s.Realm, s.Build, s.Locale, s.Started, s.Addons)).ToArray(),
            batch.Errors.Select(e => new SendError(e.Kind, e.Message, e.Stack, e.Locals, e.Count, e.First, e.Last, e.Session)).ToArray()),
            options: SendOptions);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var answer = await PortalAnswerAsync(response, cancellationToken);
        if (answer is not null && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
            throw new LuaErrorBatchRefusedException(answer);
        throw Error(response, answer);
    }

    /// <summary>
    /// Sends what the addon recorded, one file at a time, and deletes each file the portal confirmed
    /// or says it can never take, along with any file past the size limit or holding nothing to send
    /// (one written in the last minute waits: the game may still be writing it). Failures are quiet:
    /// the result says how many files are left. Throws nothing the caller has to handle.
    /// </summary>
    public async Task<LuaErrorSendResult> SendPendingAsync(string clientDirectory, LauncherSession session, CancellationToken cancellationToken = default)
    {
        int sent = 0, left = 0;
        foreach (var file in FindFiles(clientDirectory))
        {
            LuaErrorBatch? batch;
            DateTime written;
            try
            {
                var info = new FileInfo(file);
                written = info.LastWriteTimeUtc;
                if (info.Length > MaxFileBytes)
                {
                    DeleteFile(file);
                    continue;
                }
                batch = ReadBatch(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left++;
                continue;
            }
            if (batch is null)
            {
                if (_now().UtcDateTime - written < BrokenFileGrace) left++;
                else DeleteFile(file);
                continue;
            }
            try
            {
                await SendAsync(session, batch, cancellationToken);
                sent += batch.Errors.Count;
                DeleteFile(file);
            }
            catch (LuaErrorBatchRefusedException)
            {
                DeleteFile(file);
            }
            catch (Exception ex) when (ex is LauncherApiException or HttpRequestException or OperationCanceledException)
            {
                left++;
            }
        }
        return new LuaErrorSendResult(sent, left);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>The portal's own {"error": ...} message, or null for an answer from anything else (a proxy's page).</summary>
    private static async Task<string?> PortalAnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var message = (await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken))?.Error;
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }

    private static LauncherApiException Error(HttpResponseMessage response, string? answer) =>
        new(response.StatusCode, answer ?? $"The portal answered {(int)response.StatusCode} {response.ReasonPhrase}.");

    private sealed record SendSession(
        [property: JsonPropertyName("character")] string Character,
        [property: JsonPropertyName("realm")] string Realm,
        [property: JsonPropertyName("build")] string Build,
        [property: JsonPropertyName("locale")] string Locale,
        [property: JsonPropertyName("started")] long? Started,
        [property: JsonPropertyName("addons")] IReadOnlyList<string> Addons);
    private sealed record SendError(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("stack")] string Stack,
        [property: JsonPropertyName("locals")] string Locals,
        [property: JsonPropertyName("count")] long Count,
        [property: JsonPropertyName("first")] long? First,
        [property: JsonPropertyName("last")] long? Last,
        [property: JsonPropertyName("session")] int? Session);
    private sealed record SendRequest(
        [property: JsonPropertyName("client")] string Client,
        [property: JsonPropertyName("dropped")] long Dropped,
        [property: JsonPropertyName("suppressed")] long Suppressed,
        [property: JsonPropertyName("sessions")] IReadOnlyList<SendSession> Sessions,
        [property: JsonPropertyName("errors")] IReadOnlyList<SendError> Errors);
    private sealed record ErrorBody([property: JsonPropertyName("error")] string? Error);
}

/// <summary>The portal says it can never take this batch: malformed or too big.</summary>
public sealed class LuaErrorBatchRefusedException(string message) : Exception(message);

/// <summary>A Lua table as a SavedVariables file writes it.</summary>
public sealed class LuaTable
{
    private readonly Dictionary<string, object?> _fields = new();
    private readonly Dictionary<long, object?> _numbered = new();

    public object? Field(string name) => _fields.GetValueOrDefault(name);

    /// <summary>The values with whole-number keys, in key order.</summary>
    public IEnumerable<KeyValuePair<long, object?>> Numbered => _numbered.OrderBy(entry => entry.Key);

    internal void Set(string name, object? value) => _fields[name] = value;
    internal void Set(long position, object? value)
    {
        _numbered[position] = value;
        if (position >= NextPosition) NextPosition = position + 1;
    }

    internal long NextPosition { get; private set; } = 1;
}

/// <summary>
/// Reads the Lua the game writes for SavedVariables: "name = value" lines with values of tables,
/// strings, numbers, booleans and nil, and "-- [1]" comments after list items. Nothing is run. Strings
/// are bytes in Lua and become UTF-8 text here.
/// </summary>
public static class LuaSavedVariables
{
    private const int MaxDepth = 16;

    /// <summary>The value given to <paramref name="variable"/>, or null when the file doesn't set it. FormatException when it isn't such a file.</summary>
    public static object? Read(byte[] file, string variable)
    {
        var parser = new Parser(file);
        object? found = null;
        parser.Skip();
        while (!parser.AtEnd)
        {
            var name = parser.Name();
            parser.Skip();
            parser.Expect((byte)'=');
            var value = parser.Value(0);
            if (name == variable) found = value;
            parser.Skip();
        }
        return found;
    }

    private sealed class Parser(byte[] data)
    {
        private int _at;

        public bool AtEnd => _at >= data.Length;

        private static FormatException Bad(string what) => new("Not a SavedVariables file: " + what);

        public void Skip()
        {
            while (!AtEnd)
            {
                var c = data[_at];
                if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') _at++;
                else if (c == (byte)'-' && _at + 1 < data.Length && data[_at + 1] == (byte)'-')
                    while (!AtEnd && data[_at] != (byte)'\n') _at++;
                else if (_at == 0 && data.Length >= 3 && c == 0xEF && data[1] == 0xBB && data[2] == 0xBF) _at = 3;
                else break;
            }
        }

        public void Expect(byte c)
        {
            if (AtEnd || data[_at] != c) throw Bad($"expected '{(char)c}' at {_at}");
            _at++;
        }

        private static bool IsNameByte(byte c) =>
            c is (byte)'_' or >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9';

        public string Name()
        {
            var start = _at;
            while (!AtEnd && IsNameByte(data[_at])) _at++;
            if (_at == start || data[start] is >= (byte)'0' and <= (byte)'9') throw Bad($"expected a name at {start}");
            return Encoding.ASCII.GetString(data, start, _at - start);
        }

        public object? Value(int depth)
        {
            if (depth > MaxDepth) throw Bad("nested too deep");
            Skip();
            if (AtEnd) throw Bad("it ends too early");
            var c = data[_at];
            if (c == (byte)'{') return Table(depth);
            if (c == (byte)'"') return String();
            if (c is (byte)'-' or >= (byte)'0' and <= (byte)'9') return Number();
            return Name() switch
            {
                "true" => true,
                "false" => false,
                "nil" => null,
                var word => throw Bad($"unexpected '{word}'")
            };
        }

        private LuaTable Table(int depth)
        {
            var table = new LuaTable();
            Expect((byte)'{');
            while (true)
            {
                Skip();
                if (AtEnd) throw Bad("a table isn't closed");
                if (data[_at] == (byte)'}')
                {
                    _at++;
                    return table;
                }
                if (data[_at] == (byte)'[')
                {
                    _at++;
                    var key = Value(depth + 1);
                    Skip();
                    Expect((byte)']');
                    Skip();
                    Expect((byte)'=');
                    var value = Value(depth + 1);
                    if (key is string field) table.Set(field, value);
                    else if (key is double number && number == Math.Floor(number) && Math.Abs(number) < 1e15) table.Set((long)number, value);
                }
                else
                {
                    var value = Value(depth + 1);
                    table.Set(table.NextPosition, value);
                }
                Skip();
                if (!AtEnd && data[_at] is (byte)',' or (byte)';') _at++;
            }
        }

        private double Number()
        {
            var start = _at;
            while (!AtEnd && data[_at] is (byte)'-' or (byte)'+' or (byte)'.' or (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z')) _at++;
            var text = Encoding.ASCII.GetString(data, start, _at - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) throw Bad($"bad number '{text}'");
            return number;
        }

        private string String()
        {
            Expect((byte)'"');
            var text = new List<byte>();
            while (true)
            {
                if (AtEnd) throw Bad("a string isn't closed");
                var c = data[_at++];
                if (c == (byte)'"') return Encoding.UTF8.GetString(text.ToArray());
                if (c != (byte)'\\')
                {
                    text.Add(c);
                    continue;
                }
                if (AtEnd) throw Bad("a string isn't closed");
                c = data[_at++];
                switch (c)
                {
                    case (byte)'n': text.Add((byte)'\n'); break;
                    case (byte)'r': text.Add((byte)'\r'); break;
                    case (byte)'t': text.Add((byte)'\t'); break;
                    case (byte)'a': text.Add(7); break;
                    case (byte)'b': text.Add(8); break;
                    case (byte)'f': text.Add(12); break;
                    case (byte)'v': text.Add(11); break;
                    case >= (byte)'0' and <= (byte)'9':
                        var value = c - '0';
                        for (var digits = 1; digits < 3 && !AtEnd && data[_at] is >= (byte)'0' and <= (byte)'9'; digits++)
                            value = value * 10 + (data[_at++] - '0');
                        if (value > 255) throw Bad("a bad escape in a string");
                        text.Add((byte)value);
                        break;
                    default:
                        text.Add(c); // \\ \" \' and an escaped line break
                        break;
                }
            }
        }
    }
}
