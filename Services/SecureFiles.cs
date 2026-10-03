using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Kelvra;

/// <summary>
/// Files Kelvra hands to elevated programs (the PawnIO installer, the logon-task XML). Anything a normal-rights
/// program can change before an admin runs it is a way to get admin rights.
/// </summary>
public static class SecureFiles
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

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
}
