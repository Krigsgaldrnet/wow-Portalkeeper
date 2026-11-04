using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MonoTorrent;

namespace Portalkeeper.Services;

/// <summary>What a client folder has that the realm didn't ship, and what it's missing.</summary>
/// <param name="Different">Client torrent files that are missing or have the wrong size.</param>
/// <param name="TooLong">The ones of those that are longer than the torrent's file.</param>
public sealed record ClientConformance(IReadOnlyList<string> Different, IReadOnlyList<string> TooLong, IReadOnlyList<string> Extra, int Expected)
{
    public bool Matches => Different.Count == 0 && Extra.Count == 0;
    /// <summary>Every file of the client torrent is there with its size: this is the realm's client.</summary>
    public bool HasAllClientFiles => Different.Count == 0;
}

/// <summary>What a client needs, see <see cref="ClientConformanceService.Decide"/>.</summary>
public enum ClientAction
{
    /// <summary>It has the realm's files and the realm's current torrent.</summary>
    Ok,
    /// <summary>The launcher's own install is behind: it updates itself in place.</summary>
    Update,
    /// <summary>A client the launcher didn't install is behind: only INSTALL WOW may write to it.</summary>
    NotRealmClient,
}

/// <summary>
/// Keeps the game folder exactly what the realm ships: the files of its client torrent plus the realm
/// patches realm.conf lists. Anything else there (another MPQ, a dll, an exe) is deleted. Only what make-client-torrent.py leaves out of the torrent is
/// the player's: the top-level Interface (addons), WTF, Cache, Logs, Screenshots and Errors folders,
/// realmlist.wtf, Config.wtf and LuxVelum.ini, and logs and temp files. The rules below must match that script.
/// </summary>
public static class ClientConformanceService
{
    public const string InstallMarkerRelativePath = ".portalkeeper/client-install.json";
    public const string CachedTorrentRelativePath = ".portalkeeper/client.torrent";

    private static readonly HashSet<string> PlayerDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "wtf", "cache", "logs", "interface", "screenshots", "errors", ".portalkeeper" };
    private static readonly HashSet<string> PlayerFiles = new(StringComparer.OrdinalIgnoreCase)
        { "realmlist.wtf", "config.wtf", "luxvelum.ini", "thumbs.db", "desktop.ini", ".ds_store" };
    private static readonly string[] PlayerSuffixes = { ".log", ".tmp", ".bak" };

    /// <summary>
    /// Compares <paramref name="root"/> with the client torrent. <paramref name="realmFiles"/> are the
    /// full paths of the realm's patches, which belong there too (installed or not).
    /// </summary>
    public static ClientConformance Check(string root, byte[] clientTorrent, IEnumerable<string> realmFiles)
    {
        var torrent = Torrent.Load(clientTorrent);
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in torrent.Files)
            expected[RetiredPatchService.Normalize(file.Path.ToString())] = file.Length;
        var allowed = new HashSet<string>(realmFiles.Select(p => RetiredPatchService.Normalize(Path.GetRelativePath(root, p))),
            StringComparer.OrdinalIgnoreCase);

        var different = new List<string>();
        var tooLong = new List<string>();
        foreach (var (path, length) in expected)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            var actual = File.Exists(full) ? new FileInfo(full).Length : -1;
            if (actual == length) continue;
            different.Add(path);
            if (actual > length) tooLong.Add(path);
        }

        var extra = new List<string>();
        if (Directory.Exists(root))
        {
            foreach (var full in ShippedArea(root))
            {
                var relative = RetiredPatchService.Normalize(Path.GetRelativePath(root, full));
                if (!expected.ContainsKey(relative) && !allowed.Contains(relative)) extra.Add(relative);
            }
        }
        return new ClientConformance(different, tooLong, extra, expected.Count);
    }

    /// <summary>Every file in the part of the folder the realm decides, as make-client-torrent.py walks it.</summary>
    private static IEnumerable<string> ShippedArea(string root)
    {
        var pending = new Stack<(string Directory, bool Top)>();
        pending.Push((root, true));
        while (pending.Count > 0)
        {
            var (directory, top) = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || (top && PlayerDirectories.Contains(name))) continue;
                if (new DirectoryInfo(sub).LinkTarget is not null) continue;
                pending.Push((sub, false));
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || PlayerFiles.Contains(name)
                    || PlayerSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                    continue;
                yield return file;
            }
        }
    }

    /// <summary>
    /// Deletes files the realm didn't ship, and client files longer than the torrent's (a torrent
    /// rewrites a file in place and never shortens it, so those are downloaded again from scratch).
    /// Shorter files stay: they may be a paused download, and the torrent's hash check keeps every piece
    /// of them that's right. Returns how many were deleted.
    /// </summary>
    public static int DeleteNonConforming(string root, ClientConformance conformance)
    {
        var deleted = 0;
        foreach (var relative in conformance.Extra.Concat(conformance.TooLong))
        {
            var full = ManagedPath.Resolve(root, relative);
            if (!File.Exists(full)) continue;
            ManagedRuntimeWriteGuard.Check(root, full);
            File.Delete(full);
            deleted++;
        }
        return deleted;
    }

    /// <summary>The realm's client was installed here, or a client was made into it (INSTALL WOW leaves a marker).</summary>
    public static bool IsRealmInstall(string root) => File.Exists(Path.Combine(root, InstallMarkerRelativePath));

    /// <summary>
    /// The realm's client torrent is a different one from the one this folder was last brought up to.
    /// Sizes can't show this: a rebuilt file can keep its size. The cached torrent is that record, saved
    /// only once a download has finished. The install marker names the torrent the last download started
    /// for, and catches what the cached torrent misses: launchers before 0.5.62 saved every torrent whose
    /// sizes matched, without downloading anything. A folder with no record of another torrent hasn't
    /// changed: its sizes match, so the current torrent becomes its baseline without a hash check of the
    /// whole client. The exception is <paramref name="downloadUnfinished"/>, a download into this folder
    /// that was started and hasn't finished: without a cached torrent the marker alone would call it
    /// done, since it already names the torrent being downloaded. Only ask this with a torrent the portal
    /// just gave: an old one says nothing about what the realm wants now.
    /// </summary>
    public static bool ClientTorrentChanged(string root, byte[] torrent, bool downloadUnfinished = false)
    {
        var current = TorrentService.InfoHashOf(torrent);
        var cached = LoadCachedTorrent(root);
        if (cached is not null && TorrentService.InfoHashOf(cached) != current) return true;
        if (cached is null && downloadUnfinished) return true;
        return InstallMarkerInfoHash(root) is { } started && !string.Equals(started, current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What to do with a client, from its files, whose it is and whether the realm's torrent changed.</summary>
    public static ClientAction Decide(ClientConformance conformance, bool isRealmInstall, bool torrentChanged)
    {
        if (conformance.HasAllClientFiles && !torrentChanged) return ClientAction.Ok;
        return isRealmInstall ? ClientAction.Update : ClientAction.NotRealmClient;
    }

    private static string? InstallMarkerInfoHash(string root)
    {
        var path = Path.Combine(root, InstallMarkerRelativePath);
        try
        {
            if (!File.Exists(path)) return null;
            using var marker = JsonDocument.Parse(File.ReadAllBytes(path));
            return marker.RootElement.ValueKind == JsonValueKind.Object
                && marker.RootElement.TryGetProperty("infoHash", out var hash) && hash.ValueKind == JsonValueKind.String
                && hash.GetString() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// The client torrent this folder was last brought up to, or the first one seen when there was no
    /// record yet. Also what the client is checked against while the portal is unreachable. Null when
    /// there's none or the file is damaged.
    /// </summary>
    public static byte[]? LoadCachedTorrent(string root)
    {
        var path = Path.Combine(root, CachedTorrentRelativePath);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return TorrentService.TryInfoHashOf(bytes) is null ? null : bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>False when the record couldn't be written.</summary>
    public static bool SaveCachedTorrent(string root, byte[] torrent)
    {
        var path = Path.Combine(root, CachedTorrentRelativePath);
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(torrent)) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, torrent);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
