namespace Kelvra;

/// <summary>
/// Windows' own folders. The Disk page refuses to recycle protected ones (Kelvra runs as administrator, so
/// Windows itself wouldn't stop it), and the duplicate finder skips the system folders by default.
/// </summary>
public static class SystemPaths
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private static readonly string WindowsDir = Folder(Environment.SpecialFolder.Windows);
    private static readonly string UsersDir = Path.GetDirectoryName(Folder(Environment.SpecialFolder.UserProfile)) ?? "";

    /// <summary>Folders that are protected themselves; what's inside them (e.g. an old app in Program Files) is not.</summary>
    private static readonly string[] ProtectedFolders = new[]
    {
        Folder(Environment.SpecialFolder.ProgramFiles),
        Folder(Environment.SpecialFolder.ProgramFilesX86),
        Folder(Environment.SpecialFolder.CommonApplicationData),
        UsersDir,
    }.Where(p => p.Length > 0).Distinct(Cmp).ToArray();

    /// <summary>Things at the top of any drive that Windows needs to boot, recover or page.</summary>
    private static readonly HashSet<string> DriveRootItems = new(Cmp)
    {
        "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "Config.Msi", "Documents and Settings",
        "bootmgr", "BOOTNXT", "pagefile.sys", "hiberfil.sys", "swapfile.sys",
    };

    /// <summary>Where identical files are normal (shared DLLs, runtimes): skipped by the duplicate finder unless the user opts in.</summary>
    public static IReadOnlyList<string> DuplicateSkipFolders { get; } = new[]
    {
        WindowsDir,
        Folder(Environment.SpecialFolder.ProgramFiles),
        Folder(Environment.SpecialFolder.ProgramFilesX86),
        Folder(Environment.SpecialFolder.CommonApplicationData),
    }.Where(p => p.Length > 0).Distinct(Cmp).ToArray();

    /// <summary>
    /// True if moving <paramref name="path"/> to the Recycle Bin could break Windows: anything inside the Windows folder,
    /// Program Files / ProgramData / Users themselves, a user profile folder, a drive root or a drive's system files.
    /// </summary>
    public static bool IsProtected(string path)
    {
        string p;
        try
        {
            p = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true; // can't tell what it is: be safe
        }

        string? root = Path.GetPathRoot(p);
        if (root is null || Cmp.Equals(Normalize(root), p)) return true; // a whole drive

        if (WindowsDir.Length > 0 && IsSameOrInside(p, WindowsDir)) return true;
        if (ProtectedFolders.Contains(p, Cmp)) return true;

        string? parent = Path.GetDirectoryName(p);
        if (parent is null) return true;
        if (UsersDir.Length > 0 && Cmp.Equals(parent, UsersDir)) return true; // C:\Users\<someone>
        return Cmp.Equals(Normalize(parent), Normalize(root)) && DriveRootItems.Contains(Path.GetFileName(p));
    }

    /// <summary>Why <see cref="IsProtected"/> said no, for the message shown to the user.</summary>
    public const string ProtectedReason =
        "This is part of Windows or holds every user's files. Kelvra runs as administrator, so Windows wouldn't stop it " +
        "from being moved, and doing so can stop the PC from working or starting. Delete what's inside it instead, if you're sure.";

    internal static bool IsSameOrInside(string path, string folder) =>
        Cmp.Equals(path, folder) ||
        (path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] is '\\' or '/');

    /// <summary>Full path without a trailing separator (except for "C:\").</summary>
    internal static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string trimmed = full.TrimEnd('\\', '/');
        return trimmed.Length <= 2 ? full : trimmed; // keep "C:\" intact
    }

    private static string Folder(Environment.SpecialFolder f)
    {
        string p = Environment.GetFolderPath(f);
        return p.Length == 0 ? "" : Normalize(p);
    }
}
