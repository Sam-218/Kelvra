using System.Runtime.InteropServices;

namespace Kelvra.Tests;

public class DiskToolTests
{
    private static readonly DateTime Old = new(2024, 1, 2, 3, 4, 5);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr sa);

    // ---------- scanner ----------

    [Fact]
    public void Scan_totals_sizes_counts_and_types()
    {
        using var dir = new TempDir();
        dir.File(@"a\one.TXT", new byte[100]);
        dir.File(@"a\two.txt", new byte[300]);
        dir.File(@"a\b\big.mp4", new byte[5000]);
        dir.File(@"noext", new byte[7]);
        Directory.CreateDirectory(Path.Combine(dir.Path, "empty"));

        var result = new DiskScanner().Scan(dir.Path, CancellationToken.None);
        var root = result.Root;

        Assert.Equal(5407, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(3, root.FolderCount); // a, a\b, empty
        Assert.Equal("a", root.Children![0].Name); // largest first
        Assert.Equal(".mp4", result.Types[0].Extension);
        var txt = result.Types.Single(t => t.Extension == ".txt"); // case folded
        Assert.Equal((400L, 2), (txt.Size, txt.Count));
        Assert.Equal(new[] { "big.mp4", "two.txt", "one.TXT", "noext" }, result.Largest.Select(n => n.Name));
        Assert.Equal(Path.Combine(dir.Path, "a", "b", "big.mp4"), result.Largest[0].FullPath);
    }

    [Fact]
    public void Scan_does_not_follow_junctions()
    {
        using var dir = new TempDir();
        dir.File(@"real\a.bin", new byte[1000]);
        dir.Junction("loop", dir.Path);
        var result = new DiskScanner().Scan(dir.Path, CancellationToken.None);
        Assert.Equal(1000, result.Root.Size);
    }

    // ---------- duplicates ----------

    [Fact]
    public void Duplicates_are_found_by_content_around_the_sample_edges()
    {
        using var dir = new TempDir();
        int[] sizes = { 3, 65535, 65536, 65537, 131072, 131073, 200000, 1048577 };
        foreach (int size in sizes)
        {
            var data = Data.Random(size, size);
            dir.File($@"s{size}\a\copy.bin", data, Old);
            dir.File($@"s{size}\b\copy.bin", data, Old.AddDays(1));
            dir.File($@"s{size}\first.bin", Data.Flip(data, 0), Old);
            dir.File($@"s{size}\middle.bin", Data.Flip(data, size / 2), Old);
            dir.File($@"s{size}\last.bin", Data.Flip(data, size - 1), Old);
        }
        var root = new DiskScanner().Scan(dir.Path, CancellationToken.None).Root;

        var groups = new DuplicateFinder().Find(root, 0, skipSystemFolders: true, CancellationToken.None);

        Assert.Equal(sizes.Length, groups.Count);
        foreach (var g in groups)
        {
            Assert.Equal(2, g.Files.Count);
            Assert.All(g.Files, f => Assert.Equal("copy.bin", f.Name));
        }
        Assert.Equal(1048577, groups[0].Size); // most wasted space first

        var big = new DuplicateFinder().Find(root, 100_000, true, CancellationToken.None);
        Assert.Equal(4, big.Count); // 131072, 131073, 200000, 1048577
    }

    [Fact]
    public void Hard_links_are_not_duplicates()
    {
        using var dir = new TempDir();
        var data = Data.Random(150_000, 7);
        string orig = dir.File("orig.bin", data);
        Assert.True(CreateHardLink(Path.Combine(dir.Path, "link.bin"), orig, IntPtr.Zero));
        dir.File(@"sub\realcopy.bin", data);
        var root = new DiskScanner().Scan(dir.Path, CancellationToken.None).Root;

        var group = Assert.Single(new DuplicateFinder().Find(root, 0, true, CancellationToken.None));
        Assert.Equal(2, group.Files.Count); // one of orig/link + the real copy
        Assert.Contains(group.Files, f => f.Name == "realcopy.bin");
    }

    // ---------- cleanup ----------

    private static TempDir CleanupFixture()
    {
        var dir = new TempDir();
        for (int i = 0; i < 20; i++)
        {
            string p = dir.File($@"c1\{(i % 4 == 0 ? @"n\m\" : "")}x{i}.tmp", new byte[100 + i], i % 5 == 0 ? DateTime.Now.AddHours(-1) : Old);
            if (i % 7 == 0) File.SetAttributes(p, FileAttributes.ReadOnly);
        }
        return dir;
    }

    [Fact]
    public void Cleanup_measures_only_files_older_than_min_age()
    {
        using var dir = CleanupFixture();
        var aged = new CleanupTarget("t", "t", "", "", new() { Path.Combine(dir.Path, "c1"), Path.Combine(dir.Path, "missing") }) { MinAge = TimeSpan.FromHours(24) };
        var all = new CleanupTarget("t", "t", "", "", new() { Path.Combine(dir.Path, "c1") });
        CleanupService.Measure(aged);
        CleanupService.Measure(all);

        // recent files are i = 0, 5, 10, 15
        long expectedAged = Enumerable.Range(0, 20).Where(i => i % 5 != 0).Sum(i => 100L + i);
        Assert.Equal((expectedAged, 16), (aged.Size, aged.Files));
        Assert.Equal((Enumerable.Range(0, 20).Sum(i => 100L + i), 20), (all.Size, all.Files));
        Assert.Equal("16 files", aged.Status);
    }

    [Fact]
    public void Cleanup_deletes_old_files_including_read_only_and_keeps_recent_ones()
    {
        using var dir = CleanupFixture();
        var target = new CleanupTarget("t", "t", "", "", new() { Path.Combine(dir.Path, "c1") }) { MinAge = TimeSpan.FromHours(24) };
        CleanupService.Measure(target);
        long measured = target.Size;

        var (freed, deleted, skipped) = CleanupService.Clean(target);

        Assert.Equal((measured, 16, 0), (freed, deleted, skipped));
        var left = Directory.GetFiles(Path.Combine(dir.Path, "c1"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x);
        Assert.Equal(new[] { "x0.tmp", "x10.tmp", "x15.tmp", "x5.tmp" }, left);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "c1"))); // the folder itself stays
    }

    [Fact]
    public void Cleanup_skips_files_in_use()
    {
        using var dir = new TempDir();
        string locked = dir.File(@"c\locked.tmp", new byte[10], Old);
        dir.File(@"c\free.tmp", new byte[20], Old);
        var target = new CleanupTarget("t", "t", "", "", new() { Path.Combine(dir.Path, "c") });
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (freed, deleted, skipped) = CleanupService.Clean(target);
            Assert.Equal((20L, 1, 1), (freed, deleted, skipped));
        }
    }
}
