using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Kelvra;

/// <summary>
/// Files Kelvra hands to elevated programs (the PawnIO installer, the logon-task XML) and the check that Kelvra.exe
/// itself can't be swapped. Anything a normal-rights program can change before an admin runs it is a way to get admin rights.
/// </summary>
public static class SecureFiles
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    // Rights that let someone replace or change a file, plant a DLL next to it, or take the folder over
    private const FileSystemRights FileDanger = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericWrite | GenericAll;
    private const FileSystemRights FolderDanger = FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericWrite | GenericAll;
    // Higher up only renaming/replacing the whole branch matters (users may create folders in C:\, that's fine)
    private const FileSystemRights AncestorDanger = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll;

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// A new, empty folder only Administrators and SYSTEM can change (in %ProgramData%, whose ACL doesn't let users
    /// delete other people's folders). Without admin rights that's impossible, so it falls back to a folder in %TEMP%.
    /// Delete it with <see cref="DeleteQuietly"/>.
    /// </summary>
    public static string CreatePrivateFolder()
    {
        string name = "Kelvra-" + Guid.NewGuid().ToString("N");
        if (IsElevated)
        {
            try
            {
                var dir = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name));
                dir.Create(AdminOnlySecurity());
                return dir.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // fall through to %TEMP%; the lock + check in WriteAndLock still protect the file
            }
        }
        string fallback = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>Full control for Administrators and SYSTEM only, nothing inherited from the parent.</summary>
    internal static DirectorySecurity AdminOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a new file, then reopens it so nobody else can change or delete it while the
    /// returned handle is open, and checks it still holds exactly those bytes. Run or register the file while holding it.
    /// The handle is read-only and allows other readers, so Windows can still start the exe.
    /// </summary>
    public static FileStream WriteAndLock(string path, byte[] content)
    {
        using (var write = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            write.Write(content);
            write.Flush(flushToDisk: true);
        }
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!SHA256.HashData(locked).AsSpan().SequenceEqual(SHA256.HashData(content)))
                throw new InvalidOperationException($"{Path.GetFileName(path)} was changed by another program before Kelvra could use it.");
            locked.Position = 0;
            return locked;
        }
        catch
        {
            locked.Dispose();
            throw;
        }
    }

    public static void DeleteQuietly(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* still in use: it's in a temp/private location and harmless */ }
    }

    /// <summary>
    /// True if no one but Administrators, SYSTEM and TrustedInstaller can replace <paramref name="file"/>: not the file,
    /// not its folder (also no DLL planting next to it), not any folder above it. False when the ACLs can't be read.
    /// </summary>
    public static bool IsAdminOnlyWritable(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || !IsSafe(info.GetAccessControl(), FileDanger)) return false;
            var dir = info.Directory;
            if (dir is null || !IsSafe(dir.GetAccessControl(), FolderDanger)) return false;
            for (var up = dir.Parent; up != null; up = up.Parent)
                if (!IsSafe(up.GetAccessControl(), AncestorDanger)) return false;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or PlatformNotSupportedException or ArgumentException or IdentityNotMappedException)
        {
            return false;
        }
    }

    private static bool IsSafe(FileSystemSecurity security, FileSystemRights danger)
    {
        // The owner can always rewrite the permissions
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue; // applies to children only
            if (rule.IdentityReference is SecurityIdentifier sid && IsTrusted(sid)) continue;
            if ((rule.FileSystemRights & danger) != 0) return false;
        }
        return true;
    }

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.Equals(Administrators) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller);
}
