using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kelvra;

/// <summary>A newer Kelvra release on GitHub, with the two assets the Build workflow uploads.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ExeUrl, string ShaUrl);

/// <summary>
/// Checks GitHub Releases for a newer Kelvra and installs it: the new exe is downloaded, checked against the release's
/// SHA-256 file and swapped in place of the running one (Windows allows renaming a running exe, not overwriting it).
/// </summary>
public static partial class Updater
{
    /// <summary>Passed to the new exe, so it waits for the old Kelvra to close instead of saying "already running".</summary>
    public const string AfterUpdateArg = "--after-update";

    private const string LatestReleaseApi = "https://api.github.com/repos/Sam-218/kelvra/releases/latest";
    private const string DownloadPath = "/Sam-218/kelvra/releases/download/";
    private const string ExeAsset = "Kelvra.exe";
    private const string ShaAsset = "Kelvra.exe.sha256";
    private const long MaxDownloadBytes = 300L * 1024 * 1024;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

    /// <summary>"Not now" pauses the prompt for this long.</summary>
    internal static readonly TimeSpan RemindAfter = TimeSpan.FromHours(24);

    public static Version CurrentVersion { get; } = Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    // The timeout is long because it also covers the exe download; the API calls use ApiTimeout
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Kelvra/{CurrentVersion.ToString(3)}"); // GitHub rejects requests without one
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// The newest release if it's newer than this Kelvra, otherwise null. Throws (see <see cref="IsCheckError"/>) when
    /// GitHub can't be reached or its latest release can't be used, so that isn't mistaken for "up to date".
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        string json = await GetStringAsync(LatestReleaseApi, ct).ConfigureAwait(false);
        var latest = ParseRelease(json)
                     ?? throw new InvalidDataException("The latest release on GitHub has no usable version number, Kelvra.exe or checksum.");
        return IsNewer(latest.Version, CurrentVersion) ? latest : null;
    }

    /// <summary>
    /// Downloads the release's exe, checks it against its SHA-256 file and puts it in place of the running exe.
    /// The running Kelvra keeps working; the new version starts on the next launch. Returns the exe path.
    /// </summary>
    public static async Task<string> DownloadAndInstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Kelvra couldn't find its own exe file.");
        byte[] expected = ParseSha256File(await GetStringAsync(update.ShaUrl, ct).ConfigureAwait(false))
                          ?? throw new InvalidOperationException("The release's checksum file couldn't be read.");
        byte[] bytes = await DownloadAsync(update.ExeUrl, progress, ct).ConfigureAwait(false);
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("The download doesn't match the release's SHA-256 checksum, so it wasn't installed. Try again later.");
        ReplaceExe(exe, bytes, update.Version);
        Log.Info($"Installed Kelvra {update.Version.ToString(3)} (was {CurrentVersion.ToString(3)})");
        return exe;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> next to <paramref name="exe"/>, renames the (running) exe to *.old and the new
    /// file to the exe's name. Any failure puts the old exe back.
    /// </summary>
    /// <param name="expected">The version the release says it is: a mis-tagged exe would reinstall forever or downgrade.</param>
    internal static void ReplaceExe(string exe, byte[] bytes, Version? expected = null)
    {
        string fresh = exe + ".new";
        string old = exe + ".old";
        File.Delete(fresh); // left over from a failed attempt
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { old = $"{exe}.{Guid.NewGuid():N}.old"; } // still running
        }

        using (var write = new FileStream(fresh, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            write.Write(bytes);
            write.Flush(flushToDisk: true);
        }

        if (expected != null && FileVersionOf(fresh) is var actual && actual != Normalize(expected))
        {
            try { File.Delete(fresh); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException(
                $"The download is version {actual?.ToString(3) ?? "unknown"}, not {expected.ToString(3)} as the release says, so it wasn't installed.");
        }

        File.Move(exe, old);
        try
        {
            File.Move(fresh, exe);
            // Nothing may have swapped the file between writing and moving it
            if (!SHA256.HashData(File.ReadAllBytes(exe)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new InvalidOperationException("The new Kelvra.exe was changed by another program before it could be installed.");
        }
        catch
        {
            try { File.Delete(exe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            File.Move(old, exe);
            try { File.Delete(fresh); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Deletes the previous exe (and any half-finished download) an update left next to this one.</summary>
    public static void CleanupOldVersion() => CleanupOldVersion(Environment.ProcessPath);

    internal static void CleanupOldVersion(string? exe)
    {
        if (exe == null || Path.GetDirectoryName(exe) is not string dir) return;
        string name = Path.GetFileName(exe);
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, name + ".*")
                         .Where(f => f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || f.Equals(exe + ".new", StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use: next time */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The exe's file version (3 parts), or null when it has none (not a Windows program).</summary>
    private static Version? FileVersionOf(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0
            ? null
            : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    /// <summary>The failures a check or download can hit when GitHub or the network is unavailable.</summary>
    public static bool IsNetworkError(Exception ex) => ex is HttpRequestException or OperationCanceledException;

    /// <summary>A check that couldn't tell whether an update exists: no connection, or an unusable latest release.</summary>
    public static bool IsCheckError(Exception ex) => IsNetworkError(ex) || ex is InvalidDataException;

    /// <summary>A short, user-facing reason for a failed check or install.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "GitHub didn't answer in time. Check your internet connection and try again.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "GitHub refused the request (too many checks). Try again in an hour.",
        HttpRequestException => "GitHub couldn't be reached: " + ex.Message,
        _ => ex.Message,
    };

    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        return await Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<byte[]> DownloadAsync(string url, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        if (total > MaxDownloadBytes) throw new InvalidOperationException("The update file is unexpectedly large, so it wasn't downloaded.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(total is long t ? (int)t : 0);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxDownloadBytes) throw new InvalidOperationException("The update file is unexpectedly large, so it wasn't downloaded.");
            if (total > 0) progress?.Report((double)buffer.Length / total.Value);
        }
        return buffer.ToArray();
    }

    // ---------- pure helpers (unit-tested) ----------

    [GeneratedRegex(@"^[vV]?(\d{1,9})\.(\d{1,9})(?:\.(\d{1,9}))?$")]
    private static partial Regex TagPattern();

    /// <summary>"v1.4.0", "1.4.0" or "v1.4" → 1.4.0. Pre-release tags like "v1.4.0-beta" are refused.</summary>
    internal static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var m = TagPattern().Match(tag?.Trim() ?? "");
        if (!m.Success) return false;
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                              m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return true;
    }

    internal static bool IsNewer(Version latest, Version current) => Normalize(latest) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// Reads GitHub's "latest release" JSON. Null for drafts, pre-releases, tags that aren't a version, or when
    /// Kelvra.exe or its checksum is missing or isn't hosted on this repository's release downloads.
    /// </summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;
            string? tag = Str(root, "tag_name");
            if (!TryParseTag(tag, out var version)) return null;

            string? exeUrl = null, shaUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? url = Str(asset, "browser_download_url");
                    if (url == null || !IsTrustedDownload(url)) continue;
                    switch (Str(asset, "name"))
                    {
                        case ExeAsset: exeUrl = url; break;
                        case ShaAsset: shaUrl = url; break;
                    }
                }
            }
            if (exeUrl == null || shaUrl == null) return null;
            return new UpdateInfo(version, tag!, (Str(root, "body") ?? "").Trim(), exeUrl, shaUrl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Only files from this repository's GitHub release downloads are installed.</summary>
    internal static bool IsTrustedDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.AbsolutePath.StartsWith(DownloadPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The hash from a "&lt;64 hex digits&gt;  Kelvra.exe" checksum file, or null if it doesn't hold one.</summary>
    internal static byte[]? ParseSha256File(string text)
    {
        string first = text.TrimStart('﻿').Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (first.Length != 64 || !first.All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(first);
    }

    /// <summary>
    /// Whether to ask about <paramref name="update"/> now: always, unless the user said "Not now" to this same
    /// version less than 24 h ago. A clock that jumped back more than a day doesn't pause the prompt forever.
    /// </summary>
    internal static bool ShouldPrompt(UpdateInfo update, AppSettings settings, DateTime nowUtc)
    {
        if (settings.UpdateDeclinedAtUtc is not DateTime declinedAt) return true;
        if (!TryParseTag(settings.UpdateDeclinedVersion, out var declined) || declined != update.Version) return true;
        TimeSpan since = nowUtc - declinedAt;
        return since >= RemindAfter || since <= -RemindAfter;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
