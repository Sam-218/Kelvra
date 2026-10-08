using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Kelvra;

/// <summary>
/// Runs the bundled Intel PresentMon in the background and turns its CSV output into <see cref="FrameSample"/>s.
/// The exe is extracted to an admin-only folder and kept locked while it runs, it's tied to a job object (so it can't
/// outlive Kelvra, even after a crash), and it's restarted with a growing delay if it stops.
/// </summary>
public sealed class PresentMonRunner : IDisposable
{
    internal const string SessionName = "KelvraFps";

    private readonly object _gate = new();
    private RunState? _run;
    private IntPtr _job;

    /// <summary>
    /// One Start→Stop generation: its own stop flag, process and extracted exe. A quick off/on in Settings starts a new
    /// generation while the old one winds down, so they can't stop or delete each other's things.
    /// </summary>
    private sealed class RunState
    {
        public volatile bool Stopping;
        public readonly object Gate = new();
        public Process? Process;
        public string? Dir, Exe;
        public FileStream? Lock;
        public Thread? Thread;
    }

    /// <summary>One event per frame, on a background thread.</summary>
    public event Action<FrameSample>? Frame;

    /// <summary>PresentMon (re)started and sent its header; on the reader thread, before its first frame.</summary>
    public event Action? Started;

    /// <summary>Why measuring isn't working right now (null = fine). Shown in Settings → Gaming.</summary>
    public string? Error { get; private set; }

    private static int _swept;

    /// <summary>
    /// Deletes extraction folders an earlier run couldn't remove (a crash, a power cut): %ProgramData%\Kelvra-&lt;32 hex&gt;,
    /// older than an hour. Anything still in use (a running installer or PresentMon) is locked and stays.
    /// </summary>
    internal static void SweepOldFolders(string root, DateTime olderThanUtc)
    {
        try
        {
            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories("Kelvra-*"))
                if (System.Text.RegularExpressions.Regex.IsMatch(dir.Name, "^Kelvra-[0-9a-f]{32}$") && dir.CreationTimeUtc < olderThanUtc)
                    SecureFiles.DeleteQuietly(dir.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 0)
            SweepOldFolders(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DateTime.UtcNow.AddHours(-1));
        lock (_gate)
        {
            if (_run != null) return;
            var run = new RunState();
            run.Thread = new Thread(() => Run(run)) { IsBackground = true, Name = "PresentMon reader" };
            _run = run;
            run.Thread.Start();
        }
    }

    /// <summary>Stops PresentMon and its trace session; the reader thread deletes the extracted exe when it ends.</summary>
    public void Stop()
    {
        RunState? run;
        lock (_gate)
        {
            run = _run;
            if (run == null) return;
            _run = null;
        }
        run.Stopping = true;
        // Ending the ETW session makes PresentMon exit by itself; killing it alone would leave the session running
        StopSession(run.Exe);
        lock (run.Gate) Kill(run.Process);
        run.Thread?.Join(TimeSpan.FromSeconds(3));
    }

    private static void Kill(Process? process)
    {
        try { process?.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
    }

    private void Run(RunState run)
    {
        int failures = 0;
        try
        {
            while (!run.Stopping)
            {
                long started = Environment.TickCount64;
                try
                {
                    RunOnce(run);
                    if (!run.Stopping) Error = "PresentMon stopped unexpectedly; restarting.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Error = ex.Message;
                    Log.Error("Running PresentMon", ex);
                }
                if (run.Stopping) return;

                // Ran for a while: a one-off hiccup. Otherwise wait longer each time (2 s … 60 s).
                failures = Environment.TickCount64 - started > 60_000 ? 1 : failures + 1;
                int delay = (int)Math.Min(2000 * Math.Pow(2, failures - 1), 60_000);
                for (int waited = 0; waited < delay && !run.Stopping; waited += 200) Thread.Sleep(200);
            }
        }
        finally
        {
            Cleanup(run); // here, not in Stop(): only once nothing of this generation runs any more
            if (run.Stopping) Error = null;
        }
    }

    private void RunOnce(RunState run)
    {
        string exe = Extract(run);
        string self = Path.GetFileName(Environment.ProcessPath ?? "Kelvra.exe");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[]
                 {
                     "--output_stdout", "--no_console_stats", "--stop_existing_session",
                     "--session_name", SessionName, "--exclude", "dwm.exe", "--exclude", self,
                     // PresentMon writes frames in order; one frame of a background game that never reaches the screen
                     // holds back all newer ones until this buffer overflows. 2048 (the default) meant up to a minute
                     // of old frames after alt-tab; 512 clears it within seconds and is still plenty while playing.
                     "--set_circular_buffer_size", "512",
                 })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("PresentMon couldn't be started.");
        lock (run.Gate)
        {
            run.Process = process;
            if (run.Stopping) Kill(process); // Stop() came while it was starting
        }
        AddToJob(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn("PresentMon: " + e.Data.Trim());
        };
        process.BeginErrorReadLine();
        Log.Info($"PresentMon started (pid {process.Id})");

        PresentMonCsv? parser = null;
        long frames = 0, skipped = 0, failedFrames = 0;
        string? line;
        while ((line = process.StandardOutput.ReadLine()) != null)
        {
            if (parser == null)
            {
                parser = PresentMonCsv.FromHeader(line);
                if (parser != null)
                {
                    Error = null;
                    Started?.Invoke(); // a new run: its time column starts over
                    Log.Info("PresentMon columns: " + line.Trim());
                }
                else if (line.Trim().Length > 0) Log.Warn("PresentMon said: " + line.Trim());
                continue;
            }
            if (parser.Parse(line) is FrameSample sample)
            {
                frames++;
                try
                {
                    Frame?.Invoke(sample);
                }
                catch (Exception ex)
                {
                    // An error with one frame mustn't end measuring (or, on this background thread, all of Kelvra)
                    if (++failedFrames <= 3) Log.Error("Handling a PresentMon frame", ex);
                }
            }
            else if (!parser.IsComplete(line) && ++skipped <= 3) Log.Warn("PresentMon line not understood: " + line.Trim());
        }
        process.WaitForExit(2000);
        Log.Info($"PresentMon exited (code {(process.HasExited ? process.ExitCode.ToString() : "?")}) after {frames:N0} frames");
        lock (run.Gate) run.Process = null;
    }

    /// <summary>The bundled exe in an admin-only folder, locked against changes until <see cref="Cleanup"/>.</summary>
    private static string Extract(RunState run)
    {
        if (run.Exe != null && File.Exists(run.Exe)) return run.Exe;
        Cleanup(run);
        byte[] bytes = ReadBundledExe();
        run.Dir = SecureFiles.CreatePrivateFolder();
        string path = Path.Combine(run.Dir, "PresentMon.exe");
        run.Lock = SecureFiles.WriteAndLock(path, bytes);
        return run.Exe = path;
    }
    /// <summary>The embedded PresentMon, checked against the SHA-256 the build verified (see Kelvra.csproj).</summary>
    internal static byte[] ReadBundledExe()
    {
        var assembly = typeof(PresentMonRunner).Assembly;
        using var resource = assembly.GetManifestResourceStream("PresentMon.exe")
                             ?? throw new InvalidOperationException("PresentMon is missing from this build.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        string? expected = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "PresentMonSha256")?.Value;
        if (string.IsNullOrEmpty(expected) || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bundled PresentMon failed its integrity check.");
        return bytes;
    }

    /// <summary>Runs a second PresentMon that ends our trace session; the capturing one then exits on its own.</summary>
    private static void StopSession(string? exe)
    {
        if (exe == null || !File.Exists(exe)) return;
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--terminate_existing_session", "--session_name", SessionName }) info.ArgumentList.Add(arg);
            using var stopper = Process.Start(info);
            stopper?.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("Stopping the PresentMon session: " + ex.Message);
        }
    }

    private static void Cleanup(RunState run)
    {
        run.Lock?.Dispose();
        run.Lock = null;
        if (run.Dir != null) SecureFiles.DeleteQuietly(run.Dir);
        run.Dir = null;
        run.Exe = null;
    }

    // ---------- job object: PresentMon dies with Kelvra ----------

    private void AddToJob(Process process)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, (uint)size))
                    Log.Warn($"PresentMon job object: SetInformationJobObject failed ({Marshal.GetLastWin32Error()})");
            }
            if (!AssignProcessToJobObject(_job, process.Handle))
                Log.Warn($"PresentMon job object: AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("PresentMon job object: " + ex.Message);
        }
    }

    public void Dispose()
    {
        Stop();
        if (_job != IntPtr.Zero) CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
