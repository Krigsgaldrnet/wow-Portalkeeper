using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmLaunchService
{
    private static readonly string[] WowExecutableNames =
    {
        "Wow.exe",
        "wow.exe",
        "WoW.exe"
    };

    public string GetLaunchEnvironmentSummary(string clientDirectory)
    {
        if (OperatingSystem.IsWindows())
            return "Native Windows launch";

        if (!OperatingSystem.IsLinux())
            return "Unsupported launch platform";

        var wineExecutable = FindOnPath("wine") ?? FindOnPath("wine64");

        if (wineExecutable is null)
            return "Wine not found in PATH";

        if (string.IsNullOrWhiteSpace(clientDirectory) ||
            !Directory.Exists(clientDirectory))
        {
            return $"Wine: {wineExecutable}";
        }

        var wowExecutable = FindWowExecutable(Path.GetFullPath(clientDirectory));

        if (wowExecutable is null)
            return $"Wine: {wineExecutable}";

        var winePrefix = FindWinePrefix(wowExecutable);

        return string.IsNullOrWhiteSpace(winePrefix)
            ? $"Wine: {wineExecutable} • default prefix"
            : $"Wine: {wineExecutable} • Prefix: {winePrefix}";
    }

    public RealmLaunchResult PrepareAndLaunch(
        string clientDirectory,
        RealmInfo realm,
        string? sourceClientDirectory = null,
        string? accountName = null,
        string? gamePassword = null)
    {
        if (string.IsNullOrWhiteSpace(clientDirectory))
            throw new InvalidOperationException(
                "No World of Warcraft client directory is configured.");

        if (!realm.IsConfigured)
            throw new InvalidOperationException(
                "Realm configuration is incomplete.");

        if (realm.Address.Contains('\r') || realm.Address.Contains('\n'))
            throw new InvalidOperationException(
                "Realm address contains invalid characters.");

        var fullClientDirectory = Path.GetFullPath(clientDirectory);

        if (!Directory.Exists(fullClientDirectory))
            throw new DirectoryNotFoundException(
                "The configured World of Warcraft client directory no longer exists.");

        string wowExecutable;
        if (realm.Client.RuntimeMode == ClientRuntimeMode.Isolated)
        {
            var source = sourceClientDirectory ??
                throw new InvalidOperationException("An isolated launch requires its source client.");
            RealmRuntimeResolver.RequireReady(fullClientDirectory, source, realm);
            var manifest = new ManagedRuntimeManifestService().Load(
                RuntimePaths.Resolve(fullClientDirectory, ManagedRuntimeBuilder.ManifestRelativePath));
            wowExecutable = RealmExecutableService.SelectLaunchExecutable(fullClientDirectory, source, realm, manifest);
        }
        else
        {
            wowExecutable = ClientService.FindWowExecutable(fullClientDirectory, realm.Client.Executable)
                ?? throw new FileNotFoundException("Wow.exe was not found in the configured client directory.");
        }

        var localeDirectory = FindLocaleDirectory(fullClientDirectory);
        var locale = Path.GetFileName(localeDirectory);
        var realmlistPath = Path.Combine(localeDirectory, "realmlist.wtf");

        // A managed launch must not follow user-created links outside its root
        // or replace a baseline entry. Legacy behavior is unchanged.
        if (File.Exists(Path.Combine(fullClientDirectory, ManagedRuntimeBuilder.ManifestRelativePath)))
        {
            ManagedRuntimeWriteGuard.Check(fullClientDirectory,
                ManagedPath.Resolve(fullClientDirectory, Path.GetRelativePath(fullClientDirectory, realmlistPath)));
            ManagedRuntimeWriteGuard.Check(fullClientDirectory,
                ManagedPath.Resolve(fullClientDirectory, "WTF/Config.wtf"));
            ManagedPath.Resolve(fullClientDirectory, ".portalkeeper/backups/realmlist");
            ManagedPath.Resolve(fullClientDirectory, ".portalkeeper/backups/config");
            ManagedPath.Resolve(fullClientDirectory, RaisedSettingsPath);
        }
        WriteRealmlist(realmlistPath, realm.Address, fullClientDirectory);
        var settings = new List<(string Name, string Value)>(ClientSettings);
        if (!string.IsNullOrWhiteSpace(realm.GameRealmName))
        {
            if (!IsConfigValue(realm.GameRealmName))
                throw new InvalidOperationException("GameRealmName contains characters that cannot be written to Config.wtf.");
            settings.Add(("realmName", realm.GameRealmName));
        }
        // The launcher login's account fills the game's login screen. The password is only passed on
        // when the caller knows the realm's login patch reads it (see GamePasswordPrefix).
        if (!string.IsNullOrWhiteSpace(accountName) && IsConfigValue(accountName))
        {
            settings.Add(("accountName", accountName));
            if (!string.IsNullOrEmpty(gamePassword) && IsConfigValue(gamePassword))
            {
                settings.Add(("accountList", GamePasswordPrefix + gamePassword));
                foreach (var agreement in AgreementSettings)
                    settings.Add((agreement, "1"));
            }
        }
        WriteLaunchSettings(fullClientDirectory, settings,
            RealmBranding.IsBuiltIn(realm) && realm.Client.RuntimeMode != ClientRuntimeMode.Isolated);
        var process = LaunchWow(wowExecutable, fullClientDirectory, sourceClientDirectory, realm.Client.Executable);

        return new RealmLaunchResult
        {
            Locale = locale,
            RealmlistPath = realmlistPath,
            ExecutablePath = wowExecutable,
            Process = process
        };
    }

    private static string? FindWowExecutable(string clientDirectory)
    {
        foreach (var fileName in WowExecutableNames)
        {
            var path = Path.Combine(clientDirectory, fileName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string FindLocaleDirectory(string clientDirectory)
    {
        var dataDirectory = Path.Combine(clientDirectory, "Data");

        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException(
                "The WoW Data directory was not found.");

        var directories = Directory
            .EnumerateDirectories(dataDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToArray();

        var existingRealmlists = directories
            .Where(directory =>
                File.Exists(Path.Combine(directory, "realmlist.wtf")))
            .ToArray();

        if (existingRealmlists.Length == 1)
            return existingRealmlists[0];

        if (existingRealmlists.Length > 1)
        {
            throw new InvalidOperationException(
                "Multiple WoW locale directories contain realmlist.wtf. " +
                "Portalkeeper cannot safely choose one automatically.");
        }

        var localeCandidates = new List<string>();

        foreach (var directory in directories)
        {
            var locale = Path.GetFileName(directory);

            if (string.IsNullOrWhiteSpace(locale))
                continue;

            var hasLocaleMpq = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(file =>
                    Path.GetFileName(file).Equals(
                        $"locale-{locale}.MPQ",
                        StringComparison.OrdinalIgnoreCase));

            if (hasLocaleMpq)
                localeCandidates.Add(directory);
        }

        if (localeCandidates.Count == 1)
            return localeCandidates[0];

        if (localeCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Portalkeeper could not determine the WoW locale directory under Data.");
        }

        throw new InvalidOperationException(
            "Multiple WoW locale directories were detected. " +
            "Portalkeeper cannot safely choose one automatically.");
    }

    private static void WriteRealmlist(
        string realmlistPath,
        string realmAddress,
        string clientDirectory)
    {
        var desiredContents = $"set realmlist {realmAddress.Trim()}{Environment.NewLine}";

        if (File.Exists(realmlistPath))
        {
            var currentContents = File.ReadAllText(realmlistPath);

            if (string.Equals(
                    NormalizeLineEndings(currentContents),
                    NormalizeLineEndings(desiredContents),
                    StringComparison.Ordinal))
            {
                return;
            }

            BackupRealmlist(realmlistPath, clientDirectory);
        }

        var directory = Path.GetDirectoryName(realmlistPath)
            ?? throw new InvalidOperationException(
                "Unable to determine the realmlist directory.");

        Directory.CreateDirectory(directory);

        var temporaryPath = realmlistPath + ".portalkeeper.tmp";

        try
        {
            File.WriteAllText(temporaryPath, desiredContents);
            File.Move(temporaryPath, realmlistPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void BackupRealmlist(
        string realmlistPath,
        string clientDirectory)
    {
        var backupDirectory = Path.Combine(
            clientDirectory,
            ".portalkeeper",
            "backups",
            "realmlist");

        Directory.CreateDirectory(backupDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var backupPath = Path.Combine(
            backupDirectory,
            $"realmlist-{timestamp}.wtf");

        File.Copy(realmlistPath, backupPath, false);
    }

    /// <summary>
    /// Marks the game password in Config.wtf's accountList CVar (where the stock login screen keeps
    /// Battle.net account names). The realm's login patch (AccountLogin.lua) takes a value with this
    /// prefix, clears the CVar at once so the client never saves it back, and logs in with it.
    /// </summary>
    public const string GamePasswordPrefix = "portalkeeper:";

    // The login screen's agreement pages; the password login waits behind them otherwise.
    private static readonly string[] AgreementSettings =
        { "readEULA", "readTOS", "readTerminationWithoutNotice", "readScanning", "readContest" };

    // Game options the realm's addons rely on, put back on every launch: a lost or reset Config.wtf
    // turns both off without a word. checkAddonVersion = "Load out of date AddOns" unticked;
    // showQuestTrackingTooltips = quest progress lines on mob tooltips, which DragonUI's quest icons read.
    internal static readonly (string Name, string Value)[] ClientSettings =
        { ("checkAddonVersion", "0"), ("showQuestTrackingTooltips", "1") };

    // Game options raised once per client, then the player's: farclip 777 is View Distance at its top
    // (the client stops there), the distance the realm's clear-air fog (wow-server patch-z98) is drawn
    // for. RaisedSettingsPath keeps a "name=value" line for each one raised, so a lower value the
    // player picks afterwards stays, and a new value here raises it once more. Built-in realm only.
    internal static readonly (string Name, string Value)[] RaisedOnceSettings = { ("farclip", "777") };

    internal const string RaisedSettingsPath = ".portalkeeper/raised-settings.txt";

    /// <summary>
    /// Writes the launch's settings to the client's Config.wtf, with the <see cref="RaisedOnceSettings"/>
    /// the client hasn't had raised yet when <paramref name="raiseOnce"/> (the built-in realm). A raise
    /// is only remembered once the game has saved Config.wtf itself (<see cref="GameSavedConfig"/>): a
    /// new client's first start runs the game's hardware detection, which may lower it again, so a later
    /// launch raises it then. When the raise can't be written (its Config.wtf backup fails), the launch
    /// goes on without it, as it would have before.
    /// </summary>
    internal static void WriteLaunchSettings(string clientDirectory, IReadOnlyList<(string Name, string Value)> settings, bool raiseOnce)
    {
        var configPath = Path.Combine(clientDirectory, "WTF", "Config.wtf");
        var gameSaved = File.Exists(configPath) && GameSavedConfig(ReadConfig(configPath).Text);
        var raises = raiseOnce ? PendingRaises(clientDirectory) : [];
        var raising = raises.Where(s => NeedsRaise(ReadConfigSetting(clientDirectory, s.Name), s.Value)).ToArray();
        try
        {
            WriteConfigSettings(configPath, [.. settings, .. raising], clientDirectory);
        }
        catch (Exception ex) when (raising.Length > 0 && ex is IOException or UnauthorizedAccessException)
        {
            WriteConfigSettings(configPath, settings, clientDirectory);
            return;
        }
        if (gameSaved)
            MarkRaised(clientDirectory, raises);
    }

    /// <summary>
    /// True when Config.wtf has a line the launcher never writes (gxApi, locale, hwDetect...), so the
    /// game has saved it at least once; a file with only the launcher's lines is one a crashed or
    /// never-started first run left behind.
    /// </summary>
    internal static bool GameSavedConfig(string config)
    {
        var launcherNames = ClientSettings.Select(s => s.Name).Concat(RaisedOnceSettings.Select(s => s.Name))
            .Concat(AgreementSettings).Concat(["realmName", "accountName", "accountList"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Regex.Matches(config, @"^[ \t]*SET[ \t]+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Any(match => !launcherNames.Contains(match.Groups[1].Value));
    }

    /// <summary>
    /// The settings in <see cref="RaisedOnceSettings"/> this client hasn't had raised yet; none when the
    /// list can't be read, so a launch never fails over it.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> PendingRaises(string clientDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(clientDirectory), RaisedSettingsPath);
        try
        {
            var done = File.Exists(path)
                ? File.ReadAllLines(path).Select(line => line.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return RaisedOnceSettings.Where(s => !done.Contains($"{s.Name}={s.Value}")).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>True when Config.wtf has no number for the CVar or one below <paramref name="value"/>.</summary>
    internal static bool NeedsRaise(string? current, string value) =>
        !double.TryParse(current, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
        || number < double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Remembers <paramref name="raised"/> as done for this client. A list that can't be written doesn't
    /// stop the launch; the raise is then tried again next time.
    /// </summary>
    internal static void MarkRaised(string clientDirectory, IReadOnlyList<(string Name, string Value)> raised)
    {
        if (raised.Count == 0) return;
        var path = Path.Combine(Path.GetFullPath(clientDirectory), RaisedSettingsPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, raised.Select(s => $"{s.Name}={s.Value}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly Regex GamePasswordLine = new(
        @"^([ \t]*SET[ \t]+accountList[ \t]+)""" + Regex.Escape(GamePasswordPrefix) + @"[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Config.wtf text with a game password left by the launcher blanked out.</summary>
    public static string WithoutGamePassword(string config) =>
        GamePasswordLine.Replace(config, match => match.Groups[1].Value + "\"\"");

    private static readonly Regex CharacterOrderLine = new(
        @"^([ \t]*SET[ \t]+accountList[ \t]+)""" + Regex.Escape(CharacterOrderService.OrderPrefix) + @"[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Config.wtf text with what the launcher puts into or takes out of accountList blanked: a game
    /// password, and an order the character select screen left. A change only there needs no backup.
    /// </summary>
    internal static string WithoutLauncherValues(string config) =>
        CharacterOrderLine.Replace(WithoutGamePassword(config), match => match.Groups[1].Value + "\"\"");

    /// <summary>
    /// Blanks a game password the launcher left in Config.wtf, for when the game exited without the
    /// login patch taking it (a crash before the login screen, or the patch missing).
    /// </summary>
    public static void ForgetGamePassword(string clientDirectory)
    {
        var configPath = Path.Combine(Path.GetFullPath(clientDirectory), "WTF", "Config.wtf");
        if (!File.Exists(configPath)) return;
        var (current, encoding) = ReadConfig(configPath);
        var updated = WithoutGamePassword(current);
        if (!string.Equals(current, updated, StringComparison.Ordinal))
            WriteConfig(configPath, updated, encoding);
    }

    /// <summary>
    /// The value a <c>SET name "value"</c> line in Config.wtf text gives the CVar (quotes taken off), or
    /// null when no line names it. The last line wins, as in the game.
    /// </summary>
    public static string? ConfigSetting(string config, string name)
    {
        var pattern = new Regex(@"^[ \t]*SET[ \t]+" + Regex.Escape(name) + @"(?:[ \t]+(?:""([^""\r\n]*)""?|([^\s""]*)))?[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        string? value = null;
        foreach (Match match in pattern.Matches(config))
            value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return value;
    }

    /// <summary>The CVar's value in the client's Config.wtf, or null when the file or the line isn't there.</summary>
    public static string? ReadConfigSetting(string clientDirectory, string name)
    {
        var configPath = Path.Combine(Path.GetFullPath(clientDirectory), "WTF", "Config.wtf");
        return File.Exists(configPath) ? ConfigSetting(ReadConfig(configPath).Text, name) : null;
    }

    /// <summary>
    /// Empties the CVar in the client's Config.wtf (<c>SET name ""</c>), only while it still holds
    /// <paramref name="expected"/>: a newer value the game wrote meanwhile stays. True when it's empty now.
    /// No backup is kept: the value was the launcher's to take.
    /// </summary>
    public static bool ClearConfigSetting(string clientDirectory, string name, string expected)
    {
        var configPath = Path.Combine(Path.GetFullPath(clientDirectory), "WTF", "Config.wtf");
        if (!File.Exists(configPath)) return false;
        var (current, encoding) = ReadConfig(configPath);
        var value = ConfigSetting(current, name);
        if (value is null) return false;
        if (value.Length == 0) return true;
        if (!string.Equals(value, expected, StringComparison.Ordinal)) return false;
        var pattern = new Regex(@"^([ \t]*)SET[ \t]+" + Regex.Escape(name) + @"(?:[ \t]+[^\r\n]*)?[ \t]*(?=\r?$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var updated = pattern.Replace(current, match => match.Groups[1].Value + $"SET {name} \"\"");
        if (!string.Equals(current, updated, StringComparison.Ordinal)) WriteConfig(configPath, updated, encoding);
        return true;
    }

    private static bool IsConfigValue(string value) =>
        !value.Any(char.IsControl) && !value.Contains('"') && !value.Contains('\\');

    internal static void WriteConfigSettings(string configPath, IReadOnlyList<(string Name, string Value)> settings, string clientDirectory)
    {
        var exists = File.Exists(configPath);
        var (current, encoding) = exists ? ReadConfig(configPath) : (string.Empty, new UTF8Encoding(false));

        var updated = current;
        foreach (var (name, value) in settings)
        {
            var desiredLine = $"SET {name} \"{value}\"";
            var pattern = new Regex(@"^([ \t]*)SET[ \t]+" + Regex.Escape(name) + @"(?:[ \t]+[^\r\n]*)?[ \t]*(?=\r?$)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var found = false;
            updated = pattern.Replace(updated, match =>
            {
                found = true;
                return match.Groups[1].Value + desiredLine;
            });
            if (!found)
            {
                var newline = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
                    current.Contains('\n') ? "\n" : current.Contains('\r') ? "\r" : Environment.NewLine;
                if (updated.Length > 0 && !updated.EndsWith('\n') && !updated.EndsWith('\r'))
                    updated += newline;
                updated += desiredLine + newline;
            }
        }
        if (exists && string.Equals(current, updated, StringComparison.Ordinal))
            return;

        var directory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Unable to determine the WoW configuration directory.");
        Directory.CreateDirectory(directory);
        // Handing over the password changes Config.wtf on every launch; only other changes are backed up.
        if (exists && !string.Equals(WithoutLauncherValues(current), WithoutLauncherValues(updated), StringComparison.Ordinal))
        {
            var backupDirectory = Path.Combine(clientDirectory, ".portalkeeper", "backups", "config");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, $"Config-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.wtf");
            // A password from an earlier launch the game didn't clear never goes into a backup.
            File.WriteAllText(backupPath, WithoutGamePassword(current), encoding);
        }

        WriteConfig(configPath, updated, encoding);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static (string Text, Encoding Encoding) ReadConfig(string configPath)
    {
        var bytes = File.ReadAllBytes(configPath);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..] or [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..];
        if (!bom)
        {
            // Not UTF-8 (a Latin-1 "Café" from another tool): read and write it byte for byte, so the
            // lines the launcher doesn't touch keep their bytes.
            try { return (StrictUtf8.GetString(bytes), new UTF8Encoding(false)); }
            catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(bytes), Encoding.Latin1); }
        }
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var text = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        if (encoding.CodePage == Encoding.UTF8.CodePage &&
            !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
            encoding = new UTF8Encoding(false);
        return (text, encoding);
    }

    private static void WriteConfig(string configPath, string text, Encoding encoding)
    {
        var temporaryPath = configPath + ".portalkeeper.tmp";
        try
        {
            File.WriteAllText(temporaryPath, text, encoding);
            File.Move(temporaryPath, configPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string NormalizeLineEndings(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static Process LaunchWow(string wowExecutable, string clientDirectory,
        string? sourceClientDirectory, string executableName)
    {
        var process = Process.Start(CreateLaunchStartInfo(wowExecutable, clientDirectory, sourceClientDirectory, executableName));
        return process ?? throw new InvalidOperationException("The World of Warcraft process could not be started.");
    }

    // Resolve Wine from the source executable, but execute the effective runtime.
    // The explicit developer launch and normal ENTER REALM share this path.
    public static ProcessStartInfo CreateLaunchStartInfo(string wowExecutable, string clientDirectory,
        string? sourceClientDirectory = null, string executableName = "Wow.exe")
    {
        ProcessStartInfo startInfo;

        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = wowExecutable,
                WorkingDirectory = clientDirectory,
                UseShellExecute = false
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            var wineExecutable = FindOnPath("wine") ?? FindOnPath("wine64");

            if (wineExecutable is null)
            {
                throw new InvalidOperationException(
                    "Wine was not found in PATH. Install Wine or make the wine executable available in PATH.");
            }

            startInfo = new ProcessStartInfo
            {
                FileName = wineExecutable,
                WorkingDirectory = clientDirectory,
                UseShellExecute = false
            };

            var environmentExecutable = sourceClientDirectory is null ? wowExecutable :
                ClientService.FindWowExecutable(sourceClientDirectory, executableName)
                ?? throw new FileNotFoundException("The source launch executable was not found.");
            var winePrefix = FindWinePrefix(environmentExecutable);

            if (!string.IsNullOrWhiteSpace(winePrefix))
                startInfo.Environment["WINEPREFIX"] = winePrefix;

            startInfo.ArgumentList.Add(wowExecutable);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Portalkeeper launching is currently supported on Windows and Linux.");
        }

        return startInfo;
    }

    public async Task WaitForGameExitAsync(RealmLaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var process = result.Process
            ?? throw new InvalidOperationException(
                "No World of Warcraft process is associated with this launch.");

        // In the normal Windows and Wine paths the process returned by
        // Process.Start remains alive for the lifetime of the game.
        await Task.Delay(500);

        if (!process.HasExited)
        {
            await process.WaitForExitAsync();
            return;
        }

        // Some Wine configurations hand the executable off to another
        // process and let the original loader exit. In that case, follow
        // the process by its command line under /proc.
        if (!OperatingSystem.IsLinux())
            return;

        var foundGame = false;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (IsLinuxGameProcessRunning(result.ExecutablePath))
            {
                foundGame = true;
                break;
            }

            await Task.Delay(250);
        }

        if (!foundGame)
            return;

        while (IsLinuxGameProcessRunning(result.ExecutablePath))
            await Task.Delay(1000);
    }

    internal static bool IsLinuxGameProcessRunning(string wowExecutable)
    {
        var normalizedPath = Path.GetFullPath(wowExecutable);
        const string procDirectory = "/proc";

        if (!Directory.Exists(procDirectory))
            return false;

        foreach (var processDirectory in Directory.EnumerateDirectories(procDirectory))
        {
            var name = Path.GetFileName(processDirectory);

            if (string.IsNullOrWhiteSpace(name) ||
                !name.All(char.IsDigit))
            {
                continue;
            }

            var commandLinePath = Path.Combine(processDirectory, "cmdline");

            try
            {
                if (!File.Exists(commandLinePath))
                    continue;

                var commandLine = File.ReadAllText(commandLinePath)
                    .Replace('\0', ' ');

                if (commandLine.Contains(
                        normalizedPath,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch
            {
                // Processes may exit while /proc is being inspected.
            }
        }

        return false;
    }


    private static string? FindWinePrefix(string wowExecutable)
    {
        var configuredPrefix =
            Environment.GetEnvironmentVariable("WINEPREFIX");

        if (!string.IsNullOrWhiteSpace(configuredPrefix))
        {
            var expandedPrefix = ExpandHome(configuredPrefix);

            if (Directory.Exists(expandedPrefix))
                return expandedPrefix;
        }

        var normalizedWowPath = Path.GetFullPath(wowExecutable);

        foreach (var applicationsDirectory in GetDesktopApplicationDirectories())
        {
            if (!Directory.Exists(applicationsDirectory))
                continue;

            foreach (var desktopFile in Directory.EnumerateFiles(
                         applicationsDirectory,
                         "*.desktop",
                         SearchOption.TopDirectoryOnly))
            {
                string[] lines;

                try
                {
                    lines = File.ReadAllLines(desktopFile);
                }
                catch
                {
                    continue;
                }

                var execLine = lines.FirstOrDefault(line =>
                    line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(execLine))
                    continue;

                var command = execLine["Exec=".Length..];

                if (!command.Contains(
                        normalizedWowPath,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                const string prefixMarker = "WINEPREFIX=";
                var prefixIndex = command.IndexOf(
                    prefixMarker,
                    StringComparison.Ordinal);

                if (prefixIndex < 0)
                    continue;

                var prefixStart = prefixIndex + prefixMarker.Length;
                var prefixEnd = command.IndexOf(' ', prefixStart);

                var prefix = prefixEnd < 0
                    ? command[prefixStart..]
                    : command[prefixStart..prefixEnd];

                prefix = prefix.Trim().Trim('"', '\'');
                prefix = ExpandHomeAgainstCandidates(prefix);

                if (Directory.Exists(prefix))
                    return prefix;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDesktopApplicationDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            var directory = Path.Combine(xdgDataHome, "applications");
            if (seen.Add(directory))
                yield return directory;
        }

        foreach (var home in GetCandidateHomeDirectories())
        {
            var directory = Path.Combine(
                home,
                ".local",
                "share",
                "applications");

            if (seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string> GetCandidateHomeDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(profile) && seen.Add(profile))
            yield return profile;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && seen.Add(home))
            yield return home;

        var accountHome = GetLinuxAccountHomeDirectory();
        if (!string.IsNullOrWhiteSpace(accountHome) && seen.Add(accountHome))
            yield return accountHome;
    }

    private static string? GetLinuxAccountHomeDirectory()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/etc/passwd"))
            return null;

        try
        {
            var userName = Environment.UserName;

            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var fields = line.Split(':');
                if (fields.Length < 6)
                    continue;

                if (!fields[0].Equals(userName, StringComparison.Ordinal))
                    continue;

                return string.IsNullOrWhiteSpace(fields[5])
                    ? null
                    : fields[5];
            }
        }
        catch
        {
            // If account-home discovery fails, normal HOME/XDG discovery
            // remains available.
        }

        return null;
    }

    private static string ExpandHomeAgainstCandidates(string path)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal))
            return path;

        foreach (var home in GetCandidateHomeDirectories())
        {
            var candidate = Path.Combine(home, path[2..]);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return ExpandHome(path);
    }

    private static string ExpandHome(string path)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal))
            return path;

        var homeDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrWhiteSpace(homeDirectory)
            ? path
            : Path.Combine(homeDirectory, path[2..]);
    }

    private static string? FindOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.Combine(directory, executableName);

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}

public sealed class RealmLaunchResult
{
    public string Locale { get; init; } = string.Empty;
    public string RealmlistPath { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public Process? Process { get; init; }
}
