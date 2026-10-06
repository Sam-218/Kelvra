using LibreHardwareMonitor.Hardware;

// SensorFormat.UseFahrenheit is global state: run tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kelvra.Tests;

internal static class TestSetup
{
    /// <summary>Keeps test runs out of the real %APPDATA%\Kelvra\logs.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RedirectLog() => Log.Folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kelvra-tests-log");
}

/// <summary>A throw-away folder under %TEMP% that cleans up after itself (read-only files and junctions included).</summary>
public sealed class TempDir : IDisposable
{
    private readonly List<string> _junctions = new();

    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kelvra-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public string File(string relative, byte[] content, DateTime? modified = null)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        if (modified is DateTime m) System.IO.File.SetLastWriteTime(full, m);
        return full;
    }

    public string Junction(string relative, string target)
    {
        string full = System.IO.Path.Combine(Path, relative);
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{full}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        _junctions.Add(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (var j in _junctions) if (Directory.Exists(j)) Directory.Delete(j); // removes the link only
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort; %TEMP% gets cleaned eventually
        }
    }
}

internal static class Data
{
    public static byte[] Random(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    public static byte[] Flip(byte[] data, int at)
    {
        var c = (byte[])data.Clone();
        c[at] ^= 0xFF;
        return c;
    }

    public static SensorVm Sensor(string id, SensorType type, float? value, string name = "Test", HardwareType hw = HardwareType.Cpu) =>
        new(new SensorReading(id, "/hw/" + hw, hw.ToString(), hw, name, type, value, value, value), hw.ToString());
}
