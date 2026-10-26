using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Portalkeeper.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        if (settingsPath is not null)
        {
            _settingsPath = settingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
            return;
        }
        var applicationData =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

        var portalkeeperDirectory =
            Path.Combine(applicationData, "Portalkeeper");

        Directory.CreateDirectory(portalkeeperDirectory);

        _settingsPath =
            Path.Combine(portalkeeperDirectory, "settings.json");
    }

    public PortalkeeperSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new PortalkeeperSettings();
            }

            var json = File.ReadAllText(_settingsPath);

            var settings = JsonSerializer.Deserialize<PortalkeeperSettings>(json) ?? new PortalkeeperSettings();
            settings.ShowTransmogrifiedAppearancesByRealm ??= new();
            settings.ClientCacheVersions ??= new();
            settings.PatchChoicesByRealm ??= new();
            settings.PendingCharacterOrders ??= new();
            return settings;
        }
        catch
        {
            return new PortalkeeperSettings();
        }
    }

    public void Save(PortalkeeperSettings settings)
    {
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(_settingsPath, json);
    }
}

public sealed class PortalkeeperSettings
{
    public Dictionary<string, bool> ShowTransmogrifiedAppearancesByRealm { get; set; } = new();
    public bool ShowTransmogFor(string realmKey) =>
        !ShowTransmogrifiedAppearancesByRealm.TryGetValue(realmKey, out var value) || value;
    public string? SelectedRealmPath { get; set; }
    public DateTimeOffset? LastPortalkeeperUpdateCheckUtc { get; set; }
    public string? LatestPortalkeeperReleaseTag { get; set; }
    public string ClientPath { get; set; } = string.Empty;
    public bool HidePortalkeeperWhileGameRuns { get; set; } = true;
    // Realm patches the player turned on or off on the SETTINGS page, by realm (RealmIdentity) and
    // patch key. Only Recommended patches (on until turned off) and Optional ones (off until turned on).
    public Dictionary<string, Dictionary<string, bool>> PatchChoicesByRealm { get; set; } = new();
    // Sharing downloads with other players (TorrentService).
    public bool ShareDownloads { get; set; } = true;
    public int ShareUploadLimitKiB { get; set; } = 2048;
    public int TorrentPort { get; set; }
    // Opt-in: combat logging in dungeons and raids, uploaded to the realm (CombatLogUploadService).
    public bool ShareCombatLogs { get; set; }
    // Set when the option is turned on, until a log from before that has been renamed out of the way.
    public bool CombatLogSetAsidePending { get; set; }
    // When the portal last confirmed a combat log, shown under the option.
    public DateTimeOffset? CombatLogLastSent { get; set; }
    // Send the game's crash reports to the realm without asking (CrashReportService).
    public bool SendCrashReports { get; set; }
    // The last ENTER REALM: the home screen's patch notes are the ones merged since.
    public DateTimeOffset? LastPlayedUtc { get; set; }
    // A client install that was paused or interrupted, resumed by INSTALL WOW.
    public string? PendingClientInstallPath { get; set; }
    // Installed Windows builds download and run new releases by themselves.
    public bool AutoUpdatePortalkeeper { get; set; } = true;
    // The realm's cache-version.txt each client folder last cleared its Cache for.
    public Dictionary<string, string> ClientCacheVersions { get; set; } = new();
    // The last self-update started: if Portalkeeper comes back still on the old version, the install
    // failed, so automatic retries of that version wait a while (UPDATE NOW always tries).
    public string? LastSelfUpdateVersion { get; set; }
    public DateTimeOffset? LastSelfUpdateUtc { get; set; }
    // Evermore fork: orders the character select screen left that the realm hasn't saved yet, by account
    // (upper case). The launcher's login handoff overwrites accountList, where the game leaves them.
    public Dictionary<string, PendingCharacterOrder> PendingCharacterOrders { get; set; } = new();
}

/// <summary>
/// An unsent character order (CharacterOrderSync): the accountList value the game left
/// (<see cref="SourceValue"/>), and once the realm's list was read, the guids it came to, top first.
/// </summary>
public sealed class PendingCharacterOrder
{
    public string? SourceValue { get; set; }
    public List<uint> Guids { get; set; } = new();
}