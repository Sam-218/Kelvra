using System.Runtime.InteropServices;

namespace Kelvra;

/// <summary>A known place that fills up with disposable files.</summary>
public sealed class CleanupTarget : ObservableObject
{
    private bool _selected = true;
    private long _size = -1;
    private int _files;
    private string _status = "Measuring…";

    public CleanupTarget(string id, string name, string description, string icon, List<string> folders, bool recycleBin = false)
    {
        Id = id;
        Name = name;
        Description = description;
        Icon = icon;
        Folders = folders;
        IsRecycleBin = recycleBin;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Icon { get; }
    public List<string> Folders { get; }
    public bool IsRecycleBin { get; }
    /// <summary>Files newer than this are left alone (e.g. temp files an installer is still using).</summary>
    public TimeSpan MinAge { get; init; }

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public long Size
    {
        get => _size;
        set
        {
            if (Set(ref _size, value)) OnPropertyChanged(nameof(SizeText));
        }
    }
    public int Files { get => _files; set => Set(ref _files, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string SizeText => Size < 0 ? "…" : ByteFormat.Format(Size);
    public string? FirstFolder => Folders.FirstOrDefault(Directory.Exists);
}

/// <summary>Finds and empties temp folders, caches and similar junk. Files that are in use are skipped.</summary>
public static class CleanupService
{
    public static List<CleanupTarget> Targets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var list = new List<CleanupTarget>
        {
            new("temp-user", "Your temporary files", "Leftovers from installers and apps (%TEMP%). Files from the last 24 h are kept.", "\uE8B7",
                new() { Path.GetTempPath() }) { MinAge = TimeSpan.FromHours(24) },
            new("temp-windows", "Windows temporary files", "C:\\Windows\\Temp. Files from the last 24 h are kept.", "\uE8B7",
                new() { Path.Combine(windows, "Temp") }) { MinAge = TimeSpan.FromHours(24) },
            new("nvidia", "NVIDIA shader cache", "Rebuilt automatically the next time each game starts.", "\uE7F4",
                new()
                {
                    Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"),
                    Path.Combine(local, "NVIDIA Corporation", "NV_Cache"), Path.Combine(programData, "NVIDIA Corporation", "NV_Cache"),
                }),
            new("amd", "AMD shader cache", "Rebuilt automatically the next time each game starts.", "\uE7F4",
                new()
                {
                    Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"),
                    Path.Combine(local, "AMD", "VkCache"), Path.Combine(local, "AMD", "GLCache"),
                }),
            new("d3d", "DirectX shader cache", "Windows' own shader cache (D3DSCache).", "\uE7F4",
                new() { Path.Combine(local, "D3DSCache") }),
            new("wu", "Windows Update downloads", "Update files that were already installed.", "\uE895",
                new() { Path.Combine(windows, "SoftwareDistribution", "Download") }),
            new("do", "Delivery Optimization cache", "Update pieces shared with other PCs.", "\uE895",
                new() { Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache") }),
            new("dumps", "Crash dumps & error reports", "Memory dumps and Windows Error Reporting files.", "\uE7BA",
                new()
                {
                    Path.Combine(local, "CrashDumps"), Path.Combine(windows, "Minidump"), Path.Combine(windows, "LiveKernelReports"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                }),
            new("browsers", "Browser caches", "Cached web pages and images (Chrome, Edge, Brave, Firefox). Close your browser first.", "\uE774",
                BrowserCaches(local)),
            new("recycle", "Recycle Bin", "Everything currently in the Recycle Bin – this can't be undone.", "\uE74D",
                new(), recycleBin: true),
        };
        return list;
    }

    private static List<string> BrowserCaches(string local)
    {
        var result = new List<string>();
        string[] chromium =
        {
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
        };
        foreach (var userData in chromium.Where(Directory.Exists))
        {
            result.Add(Path.Combine(userData, "ShaderCache"));
            result.Add(Path.Combine(userData, "GrShaderCache"));
            foreach (var profile in SafeDirs(userData))
            {
                result.Add(Path.Combine(profile, "Cache"));
                result.Add(Path.Combine(profile, "Code Cache"));
                result.Add(Path.Combine(profile, "GPUCache"));
                result.Add(Path.Combine(profile, "Service Worker", "CacheStorage"));
            }
        }
        foreach (var profile in SafeDirs(Path.Combine(local, "Mozilla", "Firefox", "Profiles")))
            result.Add(Path.Combine(profile, "cache2"));
        return result.Where(Directory.Exists).ToList();
    }

    // ---------- measuring / cleaning ----------

    public static void Measure(CleanupTarget target)
    {
        long size = 0;
        int files = 0;
        if (target.IsRecycleBin)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0)
            {
                size = info.i64Size;
                files = (int)info.i64NumItems;
            }
        }
        else
        {
            foreach (var folder in target.Folders.Where(Directory.Exists))
            {
                var cutoff = DateTime.Now - target.MinAge;
                foreach (var file in SafeFiles(folder))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (target.MinAge > TimeSpan.Zero && fi.LastWriteTime > cutoff) continue;
                        size += fi.Length;
                        files++;
                    }
                    catch
                    {
                        // vanished or no access
                    }
                }
            }
        }
        target.Size = size;
        target.Files = files;
        target.Status = files == 0 ? "Already clean" : $"{files:N0} files";
    }

    /// <summary>Deletes the contents of the target's folders (the folders themselves stay). Returns bytes freed.</summary>
    public static (long Freed, int Deleted, int Skipped) Clean(CleanupTarget target)
    {
        if (target.IsRecycleBin)
        {
            long before = Math.Max(0, target.Size);
            int items = target.Files;
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0 || hr == unchecked((int)0x8000FFFF) ? (before, items, 0) : (0, 0, items);
        }

        long freed = 0;
        int deleted = 0, skipped = 0;
        var cutoff = DateTime.Now - target.MinAge;
        foreach (var folder in target.Folders.Where(Directory.Exists))
        {
            foreach (var file in SafeFiles(folder))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (target.MinAge > TimeSpan.Zero && fi.LastWriteTime > cutoff) continue;
                    long len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    freed += len;
                    deleted++;
                }
                catch
                {
                    skipped++; // in use or protected
                }
            }
            // Remove now-empty subfolders, deepest first
            foreach (var dir in SafeDirsRecursive(folder).OrderByDescending(d => d.Length))
            {
                try { Directory.Delete(dir, recursive: false); } catch { /* not empty or in use */ }
            }
        }
        return (freed, deleted, skipped);
    }

    // ---------- safe enumeration (skips folders we can't read and reparse points) ----------

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*", Recursive).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirsRecursive(string folder)
    {
        try { return Directory.EnumerateDirectories(folder, "*", Recursive).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirs(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    // ---------- Recycle Bin (shell32) ----------

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
