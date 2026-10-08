using System.Security.Cryptography;

namespace Kelvra.Tests;

public class UpdaterTests
{
    private const string Download = "https://github.com/Sam-218/kelvra/releases/download/v1.4.0/";

    private static string ReleaseJson(string tag = "v1.4.0", string exeUrl = Download + "Kelvra.exe",
                                      string shaUrl = Download + "Kelvra.exe.sha256", bool includeSha = true, bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "  Faster fans.\n",
          "assets": [
            { "name": "Kelvra.exe", "browser_download_url": "{{exeUrl}}" }
            {{(includeSha ? $$""", { "name": "Kelvra.exe.sha256", "browser_download_url": "{{shaUrl}}" }""" : "")}}
          ]
        }
        """;

    private static UpdateInfo Info(string version) =>
        new(Version.Parse(version), "v" + version, "", Download + "Kelvra.exe", Download + "Kelvra.exe.sha256");

    // ---------- version tags ----------

    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("1.4.0", "1.4.0")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData(" v1.10.3 ", "1.10.3")]
    public void Release_tags_parse_to_versions(string tag, string expected)
    {
        Assert.True(Updater.TryParseTag(tag, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    [Theory]
    [InlineData("v1.4.0-beta")]
    [InlineData("junk")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v1.4.0.1")]
    public void Prerelease_and_junk_tags_are_refused(string? tag) => Assert.False(Updater.TryParseTag(tag, out _));

    [Theory]
    [InlineData("1.4.0", "1.3.0", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.3.0", "1.3.0", false)]
    [InlineData("1.2.9", "1.3.0", false)]
    [InlineData("1.3.0", "1.3.0.0", false)] // the assembly version has four parts
    public void Only_a_higher_version_counts_as_newer(string latest, string current, bool newer) =>
        Assert.Equal(newer, Updater.IsNewer(Version.Parse(latest), Version.Parse(current)));

    // ---------- "Not now" asks again a day later ----------

    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Never_declined_prompts() => Assert.True(Updater.ShouldPrompt(Info("1.4.0"), new AppSettings(), Now));

    [Theory]
    [InlineData(1, false)]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(25, true)]
    [InlineData(-30, true)] // the clock was moved back a long way: don't stay quiet for days
    public void Declined_version_is_offered_again_after_24_hours(int hoursAgo, bool prompt)
    {
        var s = new AppSettings { UpdateDeclinedVersion = "1.4.0", UpdateDeclinedAtUtc = Now.AddHours(-hoursAgo) };
        Assert.Equal(prompt, Updater.ShouldPrompt(Info("1.4.0"), s, Now));
    }

    [Fact]
    public void A_newer_release_than_the_declined_one_prompts_at_once()
    {
        var s = new AppSettings { UpdateDeclinedVersion = "1.4.0", UpdateDeclinedAtUtc = Now.AddHours(-1) };
        Assert.True(Updater.ShouldPrompt(Info("1.5.0"), s, Now));
    }

    // ---------- GitHub release JSON ----------

    [Fact]
    public void Release_with_exe_and_checksum_is_read()
    {
        var info = Updater.ParseRelease(ReleaseJson());
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 4, 0), info.Version);
        Assert.Equal(("v1.4.0", "Faster fans."), (info.Tag, info.Notes));
        Assert.Equal(Download + "Kelvra.exe", info.ExeUrl);
        Assert.Equal(Download + "Kelvra.exe.sha256", info.ShaUrl);
    }

    [Fact]
    public void Release_without_checksum_is_ignored() => Assert.Null(Updater.ParseRelease(ReleaseJson(includeSha: false)));

    [Fact]
    public void Prerelease_is_ignored() => Assert.Null(Updater.ParseRelease(ReleaseJson(prerelease: true)));

    [Fact]
    public void Release_with_a_non_version_tag_is_ignored() => Assert.Null(Updater.ParseRelease(ReleaseJson(tag: "nightly")));

    [Theory]
    [InlineData("https://evil.example/Sam-218/kelvra/releases/download/v1.4.0/Kelvra.exe")]
    [InlineData("http://github.com/Sam-218/kelvra/releases/download/v1.4.0/Kelvra.exe")]
    [InlineData("https://github.com/someone-else/kelvra/releases/download/v1.4.0/Kelvra.exe")]
    [InlineData("https://github.com/Sam-218/kelvra/releases/download/../../../someone-else/x/Kelvra.exe")]
    [InlineData("https://github.com:8443/Sam-218/kelvra/releases/download/v1.4.0/Kelvra.exe")]
    public void Downloads_from_anywhere_else_are_refused(string exeUrl) =>
        Assert.Null(Updater.ParseRelease(ReleaseJson(exeUrl: exeUrl)));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"tag_name\": 5, \"assets\": {} }")]
    public void Broken_release_json_is_ignored(string json) => Assert.Null(Updater.ParseRelease(json));

    // ---------- checksum file ----------

    [Fact]
    public void Checksum_file_from_the_build_workflow_is_read()
    {
        byte[] hash = SHA256.HashData("Kelvra"u8);
        string file = Convert.ToHexString(hash) + "  Kelvra.exe\r\n"; // exactly what build.yml writes
        Assert.Equal(hash, Updater.ParseSha256File(file));
        Assert.Equal(hash, Updater.ParseSha256File(Convert.ToHexString(hash).ToLowerInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc  Kelvra.exe")]
    [InlineData("<html>Not Found</html>")]
    public void Bad_checksum_files_are_refused(string text) => Assert.Null(Updater.ParseSha256File(text));

    // ---------- replacing the exe ----------

    [Fact]
    public void New_exe_replaces_the_old_one_which_is_kept_until_cleanup()
    {
        using var dir = new TempDir();
        string exe = dir.File("Kelvra.exe", "old"u8.ToArray());

        Updater.ReplaceExe(exe, "new"u8.ToArray());
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));

        Updater.CleanupOldVersion(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.Equal("new", File.ReadAllText(exe));
    }

    [Fact]
    public void Leftovers_from_a_failed_attempt_dont_block_the_next_update()
    {
        using var dir = new TempDir();
        string exe = dir.File("Kelvra.exe", "old"u8.ToArray());
        File.WriteAllText(exe + ".new", "half");
        File.WriteAllText(exe + ".old", "older");

        Updater.ReplaceExe(exe, "new"u8.ToArray());
        Assert.Equal("new", File.ReadAllText(exe));
    }

    [Fact]
    public void A_download_of_the_wrong_version_is_refused_and_the_old_exe_stays()
    {
        // A real Windows binary with a known version: Kelvra's own dll (same version as Updater.CurrentVersion)
        byte[] real = File.ReadAllBytes(typeof(App).Assembly.Location);
        using var dir = new TempDir();
        string exe = dir.File("Kelvra.exe", "old"u8.ToArray());

        var wrong = new Version(Updater.CurrentVersion.Major + 1, 0, 0);
        var ex = Assert.Throws<InvalidOperationException>(() => Updater.ReplaceExe(exe, real, wrong));
        Assert.Contains(wrong.ToString(3), ex.Message);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(exe + ".old"));

        Assert.Throws<InvalidOperationException>(() => Updater.ReplaceExe(exe, "not a program"u8.ToArray(), wrong));
        Assert.Equal("old", File.ReadAllText(exe));

        Updater.ReplaceExe(exe, real, Updater.CurrentVersion); // the advertised version: installed
        Assert.Equal(real, File.ReadAllBytes(exe));
    }

    // ---------- settings ----------

    [Fact]
    public void Update_settings_survive_a_save()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "settings.json");
        new AppSettings { CheckForUpdates = false, UpdateDeclinedVersion = "1.4.0", UpdateDeclinedAtUtc = Now }.Save(path);

        var back = AppSettings.Load(path);
        Assert.Equal((false, "1.4.0", Now), (back.CheckForUpdates, back.UpdateDeclinedVersion, back.UpdateDeclinedAtUtc!.Value.ToUniversalTime()));
        Assert.True(new AppSettings().CheckForUpdates);
    }
}
