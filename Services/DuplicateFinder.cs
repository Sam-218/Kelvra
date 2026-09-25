using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Kelvra;

public sealed class DuplicateFile : ObservableObject
{
    private bool _selected;

    public DuplicateFile(DiskNode node, DateTime modified)
    {
        Node = node;
        Path = node.FullPath;
        Modified = modified;
    }

    public DiskNode Node { get; }
    public string Path { get; }
    public string Name => Node.Name;
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public DateTime Modified { get; }
    public string ModifiedText => Modified.ToString("d MMM yyyy HH:mm");
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class DuplicateGroup
{
    public DuplicateGroup(long size, List<DuplicateFile> files)
    {
        Size = size;
        Files = files;
    }

    public long Size { get; }
    public List<DuplicateFile> Files { get; }
    public long Wasted => Size * (Files.Count - 1);
    public string Title => $"{Files.Count} copies of {Files[0].Name}";
    public string Detail => $"{ByteFormat.Format(Size)} each · {ByteFormat.Format(Wasted)} could be freed";
}

/// <summary>Finds identical files: same size → same first/last 64 KB → same SHA-256 of the whole file.</summary>
public sealed class DuplicateFinder
{
    private const int Sample = 64 * 1024;

    private int _done;
    private int _total;
    private volatile string _stage = "";

    public int Done => _done;
    public int Total => _total;
    public string Stage => _stage;

    public List<DuplicateGroup> Find(DiskNode root, long minSize, bool skipWindows, CancellationToken ct)
    {
        _stage = "Collecting files…";
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // 1. same size
        var bySize = new Dictionary<long, List<DiskNode>>();
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
                    if (skipWindows && string.Equals(c.FullPath, windowsDir, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(c);
                }
                else if (c.Size >= minSize)
                {
                    if (!bySize.TryGetValue(c.Size, out var list)) bySize[c.Size] = list = new List<DiskNode>();
                    list.Add(c);
                }
            }
        }
        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();

        // 2. same first + last 64 KB (and not the same physical file via hard links)
        _stage = "Comparing file beginnings and ends…";
        var partial = Hash(candidates.SelectMany(l => l), full: false, ct);
        var stage2 = partial.GroupBy(p => (p.Node.Size, p.Hash)).Where(g => g.Count() > 1).ToList();

        // 3. full hash, only where the sample wasn't already the whole file
        _stage = "Checking whole files…";
        var needFull = stage2.Where(g => g.Key.Size > Sample * 2).SelectMany(g => g).Select(p => p.Node).ToList();
        var full = Hash(needFull, full: true, ct).ToDictionary(p => p.Node, p => p.Hash);

        var groups = new List<DuplicateGroup>();
        foreach (var g in stage2)
        {
            bool needsFull = g.Key.Size > Sample * 2;
            // Files we couldn't fully read are left out rather than guessed
            var members = needsFull ? g.Where(p => full.ContainsKey(p.Node)) : g;
            foreach (var set in members.GroupBy(p => needsFull ? full[p.Node] : ""))
            {
                // Hard links are one file with several names – they don't waste space
                var files = set.GroupBy(p => p.FileId).Select(x => x.First())
                               .Select(p => new DuplicateFile(p.Node, p.Modified)).ToList();
                if (files.Count > 1) groups.Add(new DuplicateGroup(g.Key.Size, files));
            }
        }
        _stage = "Done";
        return groups.OrderByDescending(x => x.Wasted).ToList();
    }

    private sealed record Hashed(DiskNode Node, string Hash, string FileId, DateTime Modified);

    private List<Hashed> Hash(IEnumerable<DiskNode> files, bool full, CancellationToken ct)
    {
        var list = files.ToList();
        _done = 0;
        _total = list.Count;
        var results = new ConcurrentBag<Hashed>();
        Parallel.ForEach(list, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, node =>
        {
            try
            {
                string path = node.FullPath;
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                string id = FileId(handle);
                var modified = File.GetLastWriteTime(path);
                using var stream = new FileStream(handle, FileAccess.Read, 1024 * 1024);
                results.Add(new Hashed(node, full ? FullHash(stream, ct) : SampleHash(stream, node.Size), id, modified));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // locked or no access: can't compare it
            }
            Interlocked.Increment(ref _done);
        });
        return results.ToList();
    }

    private static string SampleHash(FileStream s, long size)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Sample];
        int n = s.Read(buffer, 0, buffer.Length);
        sha.AppendData(buffer, 0, n);
        if (size > Sample)
        {
            s.Seek(Math.Max(Sample, size - Sample), SeekOrigin.Begin);
            n = s.Read(buffer, 0, buffer.Length);
            sha.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string FullHash(FileStream s, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string FileId(SafeFileHandle h) =>
        GetFileInformationByHandle(h, out var info)
            ? $"{info.VolumeSerialNumber:X}-{info.FileIndexHigh:X}-{info.FileIndexLow:X}"
            : Guid.NewGuid().ToString();

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
}
