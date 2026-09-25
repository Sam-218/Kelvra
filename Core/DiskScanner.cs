using System.Globalization;
using System.IO.Enumeration;
using System.Windows.Media;

namespace Kelvra;

/// <summary>A file or folder in a disk scan. Folder sizes/counts are totals of everything beneath them.</summary>
public sealed class DiskNode
{
    public DiskNode(string name, DiskNode? parent, bool isDirectory)
    {
        Name = name;
        Parent = parent;
        IsDirectory = isDirectory;
    }

    public string Name { get; }
    public DiskNode? Parent { get; }
    public bool IsDirectory { get; }
    public long Size { get; internal set; }
    public int FileCount { get; internal set; }
    public int FolderCount { get; internal set; }
    /// <summary>Sorted largest first once the scan has finished.</summary>
    public List<DiskNode>? Children { get; internal set; }

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();

    public string FullPath => Parent == null ? Name : Path.Join(Parent.FullPath, Name);

    public IEnumerable<DiskNode> Ancestors()
    {
        for (var p = Parent; p != null; p = p.Parent) yield return p;
    }
}

public sealed record FileTypeStat(string Extension, long Size, int Count, FileKinds.Kind Kind);

public sealed record ScanResult(
    DiskNode Root, TimeSpan Elapsed, int Errors, long? DriveUsedBytes,
    List<FileTypeStat> Types, List<DiskNode> Largest);

/// <summary>Walks a folder tree in parallel and builds a <see cref="DiskNode"/> tree.</summary>
public sealed class DiskScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0, // include hidden and system files, like WizTree
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private long _files;
    private long _folders;
    private long _bytes;
    private int _errors;
    private volatile string _current = "";

    public long FilesScanned => Interlocked.Read(ref _files);
    public long FoldersScanned => Interlocked.Read(ref _folders);
    public long BytesScanned => Interlocked.Read(ref _bytes);
    public string CurrentFolder => _current;

    public ScanResult Scan(string rootPath, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var root = new DiskNode(rootPath, null, isDirectory: true);
        ScanFolder(root, rootPath, ct);
        Summarize(root);

        long? used = null;
        try
        {
            var drive = new DriveInfo(rootPath);
            if (drive.IsReady && string.Equals(drive.RootDirectory.FullName, Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
                used = drive.TotalSize - drive.TotalFreeSpace;
        }
        catch
        {
            // not a drive root
        }

        var (types, largest) = Analyze(root);
        return new ScanResult(root, DateTime.UtcNow - started, _errors, used, types, largest);
    }

    private void ScanFolder(DiskNode node, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _current = path;

        var children = new List<DiskNode>();
        var subfolders = new List<(DiskNode Node, string Path)>();
        long files = 0, bytes = 0;

        try
        {
            var entries = new FileSystemEnumerable<(string Name, bool IsDir, long Length, FileAttributes Attr)>(
                path,
                (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.Attributes),
                Options);

            foreach (var (name, isDir, length, attr) in entries)
            {
                if (isDir)
                {
                    var dir = new DiskNode(name, node, isDirectory: true);
                    children.Add(dir);
                    // Don't follow junctions/symlinks: avoids double counting and loops
                    if ((attr & FileAttributes.ReparsePoint) == 0) subfolders.Add((dir, Path.Join(path, name)));
                }
                else
                {
                    children.Add(new DiskNode(name, node, isDirectory: false) { Size = length });
                    files++;
                    bytes += length;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Interlocked.Increment(ref _errors);
        }

        node.Children = children;
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _folders, subfolders.Count);

        if (subfolders.Count > 1)
        {
            Parallel.ForEach(subfolders, new ParallelOptions { CancellationToken = ct }, s => ScanFolder(s.Node, s.Path, ct));
        }
        else if (subfolders.Count == 1)
        {
            ScanFolder(subfolders[0].Node, subfolders[0].Path, ct);
        }
    }

    /// <summary>Rolls sizes and counts up to folders and sorts every folder largest-first.</summary>
    private static void Summarize(DiskNode node)
    {
        if (node.Children == null) return;
        long size = 0;
        int files = 0, folders = 0;
        foreach (var c in node.Children)
        {
            if (c.IsDirectory)
            {
                Summarize(c);
                folders += 1 + c.FolderCount;
                files += c.FileCount;
            }
            else
            {
                files++;
            }
            size += c.Size;
        }
        node.Size = size;
        node.FileCount = files;
        node.FolderCount = folders;
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>File-type totals and the largest files.</summary>
    public static (List<FileTypeStat> Types, List<DiskNode> Largest) Analyze(DiskNode root, int largestCount = 500)
    {
        var types = new Dictionary<string, (long Size, int Count)>();
        var largest = new PriorityQueue<DiskNode, long>();
        var stack = new Stack<DiskNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Children == null) continue;
            foreach (var c in node.Children)
            {
                if (c.IsDirectory)
                {
                    stack.Push(c);
                    continue;
                }
                string ext = c.Extension;
                types.TryGetValue(ext, out var t);
                types[ext] = (t.Size + c.Size, t.Count + 1);

                largest.Enqueue(c, c.Size);
                if (largest.Count > largestCount) largest.Dequeue();
            }
        }

        var typeList = types
            .Select(kv => new FileTypeStat(kv.Key, kv.Value.Size, kv.Value.Count, FileKinds.Of(kv.Key)))
            .OrderByDescending(t => t.Size)
            .ToList();
        var largestList = new List<DiskNode>(largest.Count);
        while (largest.Count > 0) largestList.Add(largest.Dequeue());
        largestList.Reverse();
        return (typeList, largestList);
    }
}

/// <summary>Colour groups for file extensions (treemap and legend).</summary>
public static class FileKinds
{
    public sealed record Kind(string Name, Color Color);

    public static readonly Kind Video = new("Video", Color.FromRgb(0xE0, 0x55, 0x6B));
    public static readonly Kind Audio = new("Audio", Color.FromRgb(0xE8, 0xA1, 0x3A));
    public static readonly Kind Images = new("Images", Color.FromRgb(0x4C, 0xC3, 0x8A));
    public static readonly Kind Archives = new("Archives", Color.FromRgb(0xB0, 0x7C, 0xE8));
    public static readonly Kind Programs = new("Programs", Color.FromRgb(0x4F, 0x8C, 0xFF));
    public static readonly Kind Documents = new("Documents", Color.FromRgb(0xE9, 0xC9, 0x46));
    public static readonly Kind Code = new("Code & data", Color.FromRgb(0x3F, 0xC5, 0xD6));
    public static readonly Kind Games = new("Game data", Color.FromRgb(0xFF, 0x7A, 0x45));
    public static readonly Kind DiskImages = new("Disk images", Color.FromRgb(0x8C, 0x9E, 0xFF));
    public static readonly Kind System = new("System", Color.FromRgb(0x9A, 0x86, 0x70));
    public static readonly Kind Other = new("Other", Color.FromRgb(0x7D, 0x85, 0x96));

    public static readonly Kind[] All = { Video, Audio, Images, Archives, Programs, Documents, Code, Games, DiskImages, System, Other };

    private static readonly Dictionary<string, Kind> ByExtension = Build();

    public static Kind Of(string extension) =>
        ByExtension.TryGetValue(extension, out var k) ? k : Other;

    private static Dictionary<string, Kind> Build()
    {
        var map = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
        void Add(Kind kind, string list)
        {
            foreach (var ext in list.Split(' ')) map["." + ext] = kind;
        }

        Add(Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts vob 3gp");
        Add(Audio, "mp3 wav flac aac ogg m4a wma opus aiff mid");
        Add(Images, "jpg jpeg png gif bmp tif tiff webp heic raw cr2 nef psd svg ico dds tga exr");
        Add(Archives, "zip rar 7z tar gz bz2 xz zst cab lz4");
        Add(Programs, "exe dll msi sys ocx drv cpl scr msix appx node so");
        Add(Documents, "pdf doc docx xls xlsx ppt pptx odt ods txt rtf md epub csv log");
        Add(Code, "cs js ts py java cpp c h json xml html css yml yaml ini cfg db sqlite pdb lib obj");
        Add(Games, "pak vpk bsa ba2 big forge upk uasset ucas utoc bundle assets resource wad pck rpf gcf");
        Add(DiskImages, "iso img vhd vhdx vmdk vdi qcow2 wim esd");
        Add(System, "etl evtx dmp tmp cache bin dat mui cat manifest");
        return map;
    }
}

public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        string format = i == 0 ? "0" : v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00";
        return $"{v.ToString(format, CultureInfo.InvariantCulture)} {Units[i]}";
    }
}
