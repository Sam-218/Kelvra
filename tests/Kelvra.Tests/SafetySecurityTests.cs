using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Kelvra.Tests;

public class SafetySecurityTests
{
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Drive = Path.GetPathRoot(Win)!;

    // ---------- SAF-01: protected folders ----------

    public static TheoryData<string, bool> ProtectedCases => new()
    {
        { Win, true },
        { Win + "\\", true },
        { Win.ToLowerInvariant(), true },
        { Path.Combine(Win, "System32"), true },
        { Path.Combine(Win, "Temp", "some.log"), true },
        { ProgramFiles, true },
        { Path.Combine(ProgramFiles, "SomeOldApp"), false },
        { ProgramData, true },
        { Path.GetDirectoryName(Profile)!, true },           // C:\Users
        { Profile, true },                                    // C:\Users\<you>
        { Path.Combine(Profile, "Documents"), false },
        { Drive, true },
        { Path.Combine(Drive, "System Volume Information"), true },
        { Path.Combine(Drive, "pagefile.sys"), true },
        { Path.Combine(Drive, "Data", "pagefile.sys"), false },
        { Win + ".old", false },
        { Path.GetTempPath(), false },
    };

    [Theory]
    [MemberData(nameof(ProtectedCases))]
    public void Knows_which_folders_must_not_be_recycled(string path, bool expected) =>
        Assert.Equal(expected, SystemPaths.IsProtected(path));

    // ---------- SAF-03: duplicate finder skips system folders ----------

    [Fact]
    public void Duplicate_finder_skips_windows_program_files_and_programdata_only()
    {
        var root = new DiskNode(Drive, null, true);
        DiskNode Child(string name) => new(name, root, true);
        var skip = SystemPaths.DuplicateSkipFolders;

        Assert.True(DuplicateFinder.IsSkippedFolder(Child(Path.GetFileName(Win)), skip));
        Assert.True(DuplicateFinder.IsSkippedFolder(Child(Path.GetFileName(ProgramFiles)), skip));
        Assert.True(DuplicateFinder.IsSkippedFolder(Child(Path.GetFileName(ProgramData)), skip));
        Assert.False(DuplicateFinder.IsSkippedFolder(Child("Users"), skip));
        Assert.False(DuplicateFinder.IsSkippedFolder(new DiskNode("Windows", Child("Data"), true), skip)); // C:\Data\Windows
        Assert.False(DuplicateFinder.IsSkippedFolder(new DiskNode("Windows", new DiskNode("Z:\\", null, true), true), skip));
    }

    // ---------- SAF-02: critical processes ----------

    [Fact]
    public void Critical_windows_processes_are_recognised()
    {
        Assert.True(ProcessGuard.IsCritical(4, "System", null));
        Assert.True(ProcessGuard.IsCritical(0, "Idle", null));
        var csrss = Process.GetProcessesByName("csrss").First();
        Assert.True(ProcessGuard.IsCritical(csrss.Id, "csrss", null));
        using var me = Process.GetCurrentProcess();
        Assert.False(ProcessGuard.IsCritical(me.Id, me.ProcessName, Environment.ProcessPath));
    }

    [Fact]
    public void A_fake_csrss_outside_windows_is_not_protected() =>
        Assert.False(ProcessGuard.IsCritical(int.MaxValue - 1, "csrss", @"C:\Users\Public\csrss.exe"));

    // ---------- SEC-02/03: files handed to elevated programs ----------

    [Fact]
    public void Locked_file_has_the_written_bytes_and_cannot_be_changed_or_deleted()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "setup.exe");
        var content = Data.Random(200_000, 1);
        using (var locked = SecureFiles.WriteAndLock(path, content))
        {
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(locked));
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(path, new byte[1]));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
            Assert.Equal(content, File.ReadAllBytes(path)); // readers (Windows starting the exe) are still allowed
        }
        File.Delete(path); // free again afterwards
    }

    [Fact]
    public void Private_folder_is_created_empty_and_removed()
    {
        string folder = SecureFiles.CreatePrivateFolder();
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        SecureFiles.DeleteQuietly(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Private_folder_acl_allows_only_administrators_and_system()
    {
        var security = SecureFiles.AdminOnlySecurity();
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                           .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).OrderBy(x => x);
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, sids);
    }

    [Fact]
    public void Bundled_pawnio_installer_matches_the_build_hash()
    {
        byte[] setup = PawnIoInstaller.ReadBundledInstaller(); // throws if the hash doesn't match
        Assert.True(setup.Length > 100_000);
        Assert.Equal((byte)'M', setup[0]); // an exe
    }
}
