using System.Runtime.InteropServices;

namespace Kelvra;

/// <summary>
/// Counts what each process sends and receives over TCP and UDP. Windows reports every transfer as an event of its
/// Microsoft-Windows-Kernel-Network provider (the source Resource Monitor uses); Kelvra listens in a real-time ETW session
/// of its own and adds the bytes up per process ID. Needs admin rights. Traffic to this PC itself (loopback) isn't
/// counted. The ETW structures below follow the Windows SDK headers (evntrace.h, evntcons.h); 64-bit only, like Kelvra.
/// </summary>
public sealed class NetTrace : IDisposable
{
    private const string SessionName = "Kelvra Network";
    private const int NameBytes = 512; // room after EVENT_TRACE_PROPERTIES for the session name

    /// <summary>Microsoft-Windows-Kernel-Network.</summary>
    private static readonly Guid Provider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;

    private readonly object _gate = new();
    private Dictionary<int, (long Down, long Up)> _bytes = new();
    private readonly EventRecordCallback _onEvent; // a field, so the delegate lives as long as the session
    private ulong _session;
    private ulong _consumer = InvalidProcessTrace;
    private Thread? _pump;
    private volatile bool _closing;

    private NetTrace() => _onEvent = OnEvent;

    /// <summary>A running trace, or null without admin rights or when Windows refuses the session (logged).</summary>
    public static NetTrace? Start()
    {
        var trace = new NetTrace();
        try
        {
            if (trace.StartSession() && trace.StartConsumer()) return trace;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Starting the network trace", ex);
        }
        trace.Dispose();
        return null;
    }

    /// <summary>Bytes down and up per process ID since the last call; the counters start again from zero.</summary>
    public Dictionary<int, (long Down, long Up)> Take()
    {
        lock (_gate)
        {
            var taken = _bytes;
            _bytes = new();
            return taken;
        }
    }

    // ---------- session (the controller side) ----------

    private bool StartSession()
    {
        Control(0, EventTraceControlStop); // a session a crashed Kelvra left running would block the name
        using (var props = new Properties())
        {
            int status = StartTraceW(out _session, SessionName, props.Pointer);
            if (status != 0)
            {
                Log.Warn(status == ErrorAccessDenied ? "No admin rights: app network use can't be read" : $"Couldn't start the network trace (error {status})");
                _session = 0;
                return false;
            }
        }
        var provider = Provider;
        int enabled = EnableTraceEx2(_session, ref provider, EventControlCodeEnableProvider, TraceLevelInformation,
                                     KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
        if (enabled == 0) return true;
        Log.Warn($"Couldn't subscribe to Windows' network events (error {enabled})");
        return false;
    }

    /// <summary>Sends a control code (stop) to the session, by handle or, with 0, by name.</summary>
    private void Control(ulong session, uint code)
    {
        using var props = new Properties();
        ControlTraceW(session, session == 0 ? SessionName : null, props.Pointer, code);
    }

    /// <summary>EVENT_TRACE_PROPERTIES for a real-time session, with the session name's space behind it, in unmanaged memory.</summary>
    private sealed class Properties : IDisposable
    {
        public IntPtr Pointer { get; }

        public Properties()
        {
            int size = Marshal.SizeOf<EventTraceProperties>();
            Pointer = Marshal.AllocHGlobal(size + NameBytes);
            var zero = new byte[size + NameBytes];
            Marshal.Copy(zero, 0, Pointer, zero.Length);
            var p = new EventTraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)(size + NameBytes), ClientContext = 1 /* QPC timestamps */, Flags = WnodeFlagTracedGuid },
                BufferSize = 64,           // KB per buffer
                MinimumBuffers = 2,
                MaximumBuffers = 16,
                LogFileMode = EventTraceRealTimeMode,
                FlushTimer = 1,            // deliver at least once a second
                LoggerNameOffset = (uint)size,
            };
            Marshal.StructureToPtr(p, Pointer, false);
        }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    // ---------- consumer (reads the events) ----------

    private bool StartConsumer()
    {
        IntPtr name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            var logFile = new EventTraceLogfile
            {
                LoggerName = name,
                ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_onEvent),
            };
            _consumer = OpenTraceW(ref logFile);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
        if (_consumer == InvalidProcessTrace)
        {
            Log.Warn($"Couldn't read the network trace (error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        ulong consumer = _consumer;
        // ProcessTrace delivers events on this thread and only returns when the session ends
        _pump = new Thread(() =>
        {
            int status = ProcessTrace(new[] { consumer }, 1, IntPtr.Zero, IntPtr.Zero);
            if (!_closing) Log.Warn($"The network trace ended (status {status})");
        }) { IsBackground = true, Name = "Network trace" };
        _pump.Start();
        return true;
    }

    /// <summary>One event, on the trace thread: a TCP or UDP transfer of some process, IPv4 or IPv6.</summary>
    private void OnEvent(ref EventRecord e)
    {
        var kind = e.EventHeader.EventDescriptor.Id switch
        {
            TcpSendV4 or UdpSendV4 => (Send: true, V6: false),
            TcpReceiveV4 or UdpReceiveV4 => (Send: false, V6: false),
            TcpSendV6 or UdpSendV6 => (Send: true, V6: true),
            TcpReceiveV6 or UdpReceiveV6 => (Send: false, V6: true),
            _ => ((bool Send, bool V6)?)null,
        };
        if (kind is not (bool send, bool v6) || e.UserData == IntPtr.Zero) return;

        int pid;
        long size;
        if (v6)
        {
            if (e.UserDataLength < Marshal.SizeOf<Ipv6Transfer>()) return;
            var t = Marshal.PtrToStructure<Ipv6Transfer>(e.UserData);
            if (t.DestinationLow == 0 && t.DestinationHigh == Ipv6LoopbackHigh) return; // ::1
            (pid, size) = ((int)t.ProcessId, t.Size);
        }
        else
        {
            if (e.UserDataLength < Marshal.SizeOf<Ipv4Transfer>()) return;
            var t = Marshal.PtrToStructure<Ipv4Transfer>(e.UserData);
            if ((t.Destination & 0xFF) == 127) return; // 127.x.x.x (network byte order: the first octet is the low byte)
            (pid, size) = ((int)t.ProcessId, t.Size);
        }
        if (size <= 0) return;

        lock (_gate)
        {
            var b = _bytes.GetValueOrDefault(pid);
            _bytes[pid] = send ? (b.Down, b.Up + size) : (b.Down + size, b.Up);
        }
    }

    public void Dispose()
    {
        _closing = true;
        if (_session != 0)
        {
            Control(_session, EventTraceControlStop); // also makes ProcessTrace return
            _session = 0;
        }
        if (_consumer != InvalidProcessTrace)
        {
            CloseTrace(_consumer);
            _consumer = InvalidProcessTrace;
        }
        _pump?.Join(2000);
    }

    // ---------- Windows: constants, structures and functions ----------

    // Event IDs of Microsoft-Windows-Kernel-Network (from its manifest)
    private const ushort TcpSendV4 = 10, TcpReceiveV4 = 11, TcpSendV6 = 26, TcpReceiveV6 = 27;
    private const ushort UdpSendV4 = 42, UdpReceiveV4 = 43, UdpSendV6 = 58, UdpReceiveV6 = 59;

    /// <summary>::1 as the second half of a 16-byte address read as a little-endian ulong: the last byte is 1.</summary>
    private const ulong Ipv6LoopbackHigh = 1UL << 56;

    private const int ErrorAccessDenied = 5;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceControlStop = 1;
    private const uint EventControlCodeEnableProvider = 1;
    private const byte TraceLevelInformation = 4;
    private const uint ProcessTraceModeRealTime = 0x00000100, ProcessTraceModeEventRecord = 0x10000000;
    private const ulong InvalidProcessTrace = ulong.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceHeader
    {
        public ushort Size, FieldTypeFlags;
        public uint Version, ThreadId, ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId, ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength, ClientContext;
    }

    /// <summary>TIME_ZONE_INFORMATION: never read here, only its size (172 bytes) matters.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 172)]
    private struct TimeZoneInformation
    {
        public int Bias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName, LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime, PerfFreq, StartTime;
        public uint ReservedFlags, BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceLogfile
    {
        public IntPtr LogFileName, LoggerName;
        public long CurrentTime;
        public uint BuffersRead, ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize, Filled, EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventDescriptor
    {
        public ushort Id;
        public byte Version, Channel, Level, Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHeader
    {
        public ushort Size, HeaderType, Flags, EventProperty;
        public uint ThreadId, ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EventDescriptor EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventRecord
    {
        public EventHeader EventHeader;
        public uint BufferContext; // ETW_BUFFER_CONTEXT: processor number, alignment, logger id
        public ushort ExtendedDataCount, UserDataLength;
        public IntPtr ExtendedData, UserData, UserContext;
    }

    /// <summary>The start of a TcpIp/UdpIp IPv4 send or receive event: process, bytes, destination and source address.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Ipv4Transfer
    {
        public uint ProcessId, Size, Destination, Source;
    }

    /// <summary>The start of an IPv6 send or receive event: process, bytes and the 16-byte destination address.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Ipv6Transfer
    {
        public uint ProcessId, Size;
        public ulong DestinationLow, DestinationHigh;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(ref EventRecord record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong session, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong session, string? name, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong session, ref Guid provider, uint controlCode, byte level,
                                             ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfile logFile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong handle);

    /// <summary>Sizes of the Windows structures, for the self-check test (they must match the SDK's x64 layout).</summary>
    internal static (int Properties, int LogFile, int Record, int Header) StructSizes() =>
        (Marshal.SizeOf<EventTraceProperties>(), Marshal.SizeOf<EventTraceLogfile>(), Marshal.SizeOf<EventRecord>(), Marshal.SizeOf<EventHeader>());

    /// <summary>Where the fields Kelvra uses sit, for the self-check test.</summary>
    internal static (int ProcessMode, int Callback, int UserDataLength, int UserData, int EventId) Offsets() =>
        ((int)Marshal.OffsetOf<EventTraceLogfile>(nameof(EventTraceLogfile.ProcessTraceMode)),
         (int)Marshal.OffsetOf<EventTraceLogfile>(nameof(EventTraceLogfile.EventRecordCallback)),
         (int)Marshal.OffsetOf<EventRecord>(nameof(EventRecord.UserDataLength)),
         (int)Marshal.OffsetOf<EventRecord>(nameof(EventRecord.UserData)),
         (int)Marshal.OffsetOf<EventHeader>(nameof(EventHeader.EventDescriptor)));
}
