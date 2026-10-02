using System.Buffers;
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

    /// <summary>
    /// Searches an already scanned tree (sizes come from the scan, contents from disk). Runs for a while: call on a worker
    /// thread and poll <see cref="Stage"/>/<see cref="Done"/>/<see cref="Total"/> for progress. Biggest waste first.
    /// </summary>
    /// <param name="skipSystemFolders">Leave out Windows, Program Files and ProgramData, where identical files are normal and needed.</param>
    public List<DuplicateGroup> Find(DiskNode root, long minSize, bool skipSystemFolders, CancellationToken ct)
    {
        _stage = "Collecting files…";
        var skipped = SystemPaths.DuplicateSkipFolders;

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
                    if (skipSystemFolders && IsSkippedFolder(c, skipped)) continue;
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

    /// <summary>
    /// True only for one of <paramref name="folders"/> itself. Names are compared first so the full path, which
    /// allocates a string per folder level, is only built for folders called e.g. "Windows" or "Program Files".
    /// </summary>
    internal static bool IsSkippedFolder(DiskNode dir, IReadOnlyList<string> folders)
    {
        foreach (string folder in folders)
        {
            if (dir.Name.AsSpan().Equals(Path.GetFileName(folder.AsSpan()), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(dir.FullPath, folder, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
                results.Add(new Hashed(node, full ? FullHash(handle, ct) : SampleHash(handle, node.Size), id, modified));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // locked or no access: can't compare it
            }
            Interlocked.Increment(ref _done);
        });
        return results.ToList();
    }

    // Both hashes read straight from the handle (RandomAccess) into pooled buffers. A buffered FileStream
    // would read a whole 1 MB block to serve each 64 KB sample, and new 1 MB arrays per file churn the large-object heap.

    /// <summary>SHA-256 of the first and last 64 KB (the whole file when it's 128 KB or smaller).</summary>
    private static string SampleHash(SafeFileHandle handle, long size)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Sample);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int n = RandomAccess.Read(handle, buffer.AsSpan(0, Sample), 0);
            sha.AppendData(buffer, 0, n);
            if (size > Sample)
            {
                // Never re-read the first block: small files only contribute their remainder here
                n = RandomAccess.Read(handle, buffer.AsSpan(0, Sample), Math.Max(Sample, size - Sample));
                sha.AppendData(buffer, 0, n);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string FullHash(SafeFileHandle handle, CancellationToken ct)
    {
        const int chunk = 1024 * 1024;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunk);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            int n;
            while ((n = RandomAccess.Read(handle, buffer.AsSpan(0, chunk), offset)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, n);
                offset += n;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Volume + file index: the same for every hard link to one file. A random id if it can't be read (never merges).</summary>
    private static string FileId(SafeFileHandle h) =>
        GetFileInformationByHandle(h, out var info)
            ? $"{info.VolumeSerialNumber:X}-{info.FileIndexHigh:X}-{info.FileIndexLow:X}"
            : Guid.NewGuid().ToString();

    // Pack = 4 matches the native layout (52 bytes): FILETIMEs are 4-byte aligned there. Without it the longs are
    // 8-byte aligned and every field after FileAttributes is read 4 bytes off.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
}
