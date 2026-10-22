using System;
using Portalkeeper.Services;

namespace Portalkeeper.Models;

public sealed class AddonInfo
{
    public AddonDefinition Definition { get; init; } = new();

    public string DirectoryPath { get; init; } = string.Empty;

    public bool IsInstalled { get; init; }

    public string InstalledVersion { get; init; } = string.Empty;

    public string InstalledSourceCommit { get; init; } = string.Empty;

    // This launcher installed it (and it hasn't been uninstalled since), as opposed to a folder of the
    // same name the player put there themselves.
    public bool InstalledByLauncher { get; init; }

    public string DiscoveryError { get; init; } = string.Empty;

    public bool IsSourceError =>
        !string.IsNullOrWhiteSpace(DiscoveryError);

    public bool HasKnownInstalledVersion =>
        IsInstalled &&
        !string.IsNullOrWhiteSpace(InstalledVersion);

    public bool HasAvailableVersion =>
        !string.IsNullOrWhiteSpace(Definition.Version);

    public bool HasTrackedSourceCommit =>
        !string.IsNullOrWhiteSpace(InstalledSourceCommit) &&
        !string.IsNullOrWhiteSpace(Definition.SourceCommit);

    public bool VersionUpdateAvailable =>
        HasKnownInstalledVersion &&
        HasAvailableVersion &&
        AddonVersionComparer.Compare(
            InstalledVersion,
            Definition.Version) < 0;

    public bool SourceUpdateAvailable =>
        IsInstalled &&
        HasTrackedSourceCommit &&
        !InstalledSourceCommit.Equals(
            Definition.SourceCommit,
            StringComparison.OrdinalIgnoreCase);

    public bool IsUpdateAvailable =>
        VersionUpdateAvailable || SourceUpdateAvailable;

    public bool IsNewerThanManifest =>
        HasKnownInstalledVersion &&
        HasAvailableVersion &&
        AddonVersionComparer.Compare(
            InstalledVersion,
            Definition.Version) > 0;

    public string RequirementText =>
        Definition.IsPersonal
            ? "Personal"
            : Definition.Required
                ? "Required"
                : Definition.Recommended
                    ? "Recommended"
                    : "Optional";

    public bool CanRemoveFromManagement =>
        Definition.IsPersonal;

    public string StatusText =>
        IsSourceError
            ? "Source Error"
            : !IsInstalled
            ? "Missing"
            : IsUpdateAvailable
                ? "Update Available"
                : IsNewerThanManifest
                    ? "Newer"
                    : Definition.SourceWarning.Length > 0 ? "Installed (cached source)" : "Current";

    public string StatusSymbol =>
        IsSourceError
            ? "!"
            : !IsInstalled
            ? "○"
            : IsUpdateAvailable
                ? "↑"
                : "✓";

    public bool CanInstallOrUpdate =>
        !IsSourceError &&
        (!IsInstalled || IsUpdateAvailable) &&
        (
            (Definition.IsGitHubSource &&
             !string.IsNullOrWhiteSpace(Definition.SourceCommit))
            ||
            !string.IsNullOrWhiteSpace(Definition.DownloadUrl)
        );

    public bool CanReinstall => IsInstalled && !IsSourceError && !IsUpdateAvailable;

    public string ActionText =>
        !IsInstalled ? "INSTALL" : "UPDATE";

    public string VersionText
    {
        get
        {
            if (IsSourceError)
                return DiscoveryError;
            if (Definition.SourceWarning.Length > 0) return Definition.SourceWarning + $" Installed: {InstalledVersion}; cached available: {Definition.Version}";

            if (!IsInstalled)
                return HasAvailableVersion
                    ? $"Available: {Definition.Version}"
                    : "Not installed";

            if (!HasKnownInstalledVersion)
                return HasAvailableVersion
                    ? $"Installed: unknown   Available: {Definition.Version}"
                    : "Installed version: unknown";

            // A GitHub addon can change without a version bump: name the commits, or the row reads
            // "Installed: 1.0   Available: 1.0" next to Update Available.
            if (SourceUpdateAvailable && !VersionUpdateAvailable && !IsNewerThanManifest)
                return $"Installed: {InstalledVersion} ({ShortCommit(InstalledSourceCommit)})   Available: {Definition.Version} ({ShortCommit(Definition.SourceCommit)})";

            if (IsUpdateAvailable || IsNewerThanManifest)
                return $"Installed: {InstalledVersion}   Available: {Definition.Version}";

            return $"Installed: {InstalledVersion}";
        }
    }

    private static string ShortCommit(string commit) =>
        commit.Length > 7 ? commit[..7] : commit;
}
