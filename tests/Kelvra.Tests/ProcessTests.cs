using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kelvra.Tests;

/// <summary>The Apps page: reading processes like Task Manager does, and grouping them by app.</summary>
public class ProcessTests
{
    private static ProcessSample P(int pid, int parent, string name, long created = 100) =>
        new(pid, parent, created, name, null, 0, 0, 0, 0);

    private static Dictionary<int, int> Roots(IEnumerable<ProcessSample> processes, params int[] withWindow) =>
        ProcessTree.Roots(processes.ToList(), withWindow.ToHashSet());

    [Fact]
    public void An_apps_helper_processes_belong_to_it_but_what_the_shell_starts_doesnt()
    {
        var roots = Roots(new[]
        {
            P(9692, 1, "explorer.exe", created: 1),
            P(3396, 9692, "spotify.exe", created: 10),   // the app, opened from the Start menu
            P(16156, 3396, "spotify.exe", created: 11),  // Chromium renderer
            P(500, 3396, "powershell.exe", created: 12), // a shell the app started
            P(501, 500, "conhost.exe", created: 13),     // ...and its console
            P(7000, 9692, "Discord.exe", created: 20),  // another app from the shell
        }, withWindow: 3396);

        Assert.Equal(new[] { 3396, 3396, 3396, 3396 }, new[] { roots[3396], roots[16156], roots[500], roots[501] });
        Assert.Equal(7000, roots[7000]);
        Assert.Equal(9692, roots[9692]);
    }

    [Fact]
    public void A_program_with_its_own_window_is_its_own_app_even_when_a_launcher_started_it()
    {
        var roots = Roots(new[]
        {
            P(100, 4, "steam.exe", created: 1),
            P(101, 100, "steamwebhelper.exe", created: 2), // Steam's window: Task Manager lists it apart
            P(102, 101, "steamwebhelper.exe", created: 3),
            P(200, 100, "game.exe", created: 5),           // a game Steam started
            P(201, 200, "crashhandler.exe", created: 6),
        }, withWindow: new[] { 101, 200 });

        Assert.Equal(100, roots[100]);
        Assert.Equal(101, roots[102]);
        Assert.Equal(200, roots[200]);
        Assert.Equal(200, roots[201]);
    }

    [Fact]
    public void Services_and_reused_parent_ids_dont_join_other_apps()
    {
        var roots = Roots(new[]
        {
            P(700, 600, "services.exe", created: 1),
            P(800, 700, "svchost.exe", created: 2),
            P(801, 800, "WmiPrvSE.exe", created: 3),  // started by a service host: separate
            P(900, 700, "NVDisplay.Container.exe", created: 4),
            P(901, 900, "NVDisplay.Container.exe", created: 5), // a service's own helper: grouped
            P(50, 60, "old.exe", created: 50),
            P(60, 1, "newer.exe", created: 70),       // got the PID of old.exe's long-gone parent
            P(70, 12345, "orphan.exe", created: 80),  // parent exited
        });

        Assert.Equal(800, roots[800]);
        Assert.Equal(801, roots[801]);
        Assert.Equal(900, roots[901]);
        Assert.Equal(50, roots[50]);
        Assert.Equal(70, roots[70]);
    }

    [Fact]
    public void Every_process_is_read_with_its_parent_and_private_memory()
    {
        Assert.Equal(0x100, Marshal.SizeOf<ProcessMonitor.SYSTEM_PROCESS_INFORMATION>());
        Assert.Equal(0xE8, (int)Marshal.OffsetOf<ProcessMonitor.SYSTEM_PROCESS_INFORMATION>(nameof(ProcessMonitor.SYSTEM_PROCESS_INFORMATION.ReadTransferCount)));

        var all = ProcessMonitor.ReadProcesses();
        using var self = Process.GetCurrentProcess();
        var me = Assert.Single(all, p => p.Pid == self.Id);
        Assert.Equal(self.ProcessName + ".exe", me.ImageName, ignoreCase: true);
        Assert.Equal(self.StartTime.ToFileTimeUtc(), me.Created, (double)TimeSpan.TicksPerSecond);
        Assert.InRange(me.PrivateBytes, 1, me.WorkingSet); // private memory is part of the working set
        Assert.True(me.CpuTicks > 0);
        Assert.Contains(all, p => p.Pid == me.ParentPid);
        Assert.Contains(all, p => p.Pid == 4); // "System": readable without opening the process
        Assert.DoesNotContain(all, p => p.Pid == 0);
    }

    [Fact]
    public void List_sync_moves_rows_instead_of_replacing_them()
    {
        string a = "a", b = "b", c = "c", d = "d";
        var items = new ObservableCollection<string>(new[] { a, b, c });
        var changes = new List<string>();
        items.CollectionChanged += (_, e) => changes.Add(e.Action.ToString());

        AppsPage.Sync(items, new[] { c, a, d });
        Assert.Equal(new[] { c, a, d }, items);
        Assert.DoesNotContain("Reset", changes);
        Assert.Contains("Move", changes); // c and a keep their identity (and selection)
    }
}
