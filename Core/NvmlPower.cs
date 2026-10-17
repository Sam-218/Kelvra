using System.Runtime.InteropServices;

namespace Kelvra;

/// <summary>
/// GPU power for NVIDIA cards whose driver answers "N/A" to the usual power call (nvidia-smi shows "Power Draw: N/A",
/// e.g. an RTX 4060 on driver 617), so the hardware library has no power sensor for them. The driver still keeps
/// power samples: this averages the ones taken since the last poll. Only the polling thread calls it.
/// </summary>
internal static class NvmlPower
{
    private const int Success = 0, NotFound = 6;
    private const int TotalPowerSamples = 0;
    private const int UnsignedInt = 1, UnsignedLong = 2, UnsignedLongLong = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Sample
    {
        public ulong TimeStamp;
        public ulong Value; // a union; power comes as an unsigned int in mW
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetSamples(IntPtr device, int type, ulong lastSeen, out int valueType, ref uint count, [Out] Sample[]? samples);

    private sealed class Gpu
    {
        public IntPtr Handle;
        public ulong LastSeen;
        public float? Watts, Min, Max;
    }

    private static bool? _ready;
    private static readonly Dictionary<string, Gpu?> Gpus = new();

    /// <summary>
    /// Watts (with min/max since the last reset) for the card <paramref name="name"/>, the <paramref name="index"/>-th
    /// NVIDIA GPU the hardware library found; null when the driver has no power readings for it either.
    /// </summary>
    public static (float Value, float Min, float Max)? Read(string hardwareId, int index, string name)
    {
        if (!Ready()) return null;
        float? now;
        try
        {
            if (!Gpus.TryGetValue(hardwareId, out var found)) Gpus[hardwareId] = found = Find(index, name);
            if (found == null) return null;
            now = Latest(found);
        }
        catch (EntryPointNotFoundException)
        {
            _ready = false; // an old driver without these calls: no power reading, as before
            return null;
        }
        var gpu = Gpus[hardwareId]!;
        if (now is float w)
        {
            gpu.Watts = w;
            gpu.Min = Math.Min(gpu.Min ?? w, w);
            gpu.Max = Math.Max(gpu.Max ?? w, w);
        }
        return gpu.Watts is float v ? (v, gpu.Min ?? v, gpu.Max ?? v) : null;
    }

    public static void ResetMinMax()
    {
        foreach (var gpu in Gpus.Values)
            if (gpu != null) gpu.Min = gpu.Max = gpu.Watts;
    }

    private static bool Ready()
    {
        if (_ready is bool r) return r;
        try { _ready = nvmlInit_v2() == Success; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { _ready = false; }
        return _ready.Value;
    }

    /// <summary>
    /// The NVML device for the card. NVML and the hardware library usually count cards in the same (PCI) order;
    /// the name check catches the cases where they don't.
    /// </summary>
    private static Gpu? Find(int index, string name)
    {
        if (nvmlDeviceGetCount_v2(out uint count) != Success) return null;
        var order = Enumerable.Range(0, (int)count).OrderBy(i => i == index ? 0 : 1);
        foreach (int i in order)
        {
            if (nvmlDeviceGetHandleByIndex_v2((uint)i, out var handle) != Success) continue;
            var buffer = new byte[96];
            if (nvmlDeviceGetName(handle, buffer, (uint)buffer.Length) != Success) continue;
            if (!string.Equals(System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0'), name, StringComparison.OrdinalIgnoreCase)) continue;
            return new Gpu { Handle = handle };
        }
        return null;
    }

    /// <summary>The average of the samples since the last call, the usual reading as a fallback, or null when there are neither.</summary>
    private static float? Latest(Gpu gpu)
    {
        uint count = 0;
        int rc = nvmlDeviceGetSamples(gpu.Handle, TotalPowerSamples, gpu.LastSeen, out _, ref count, null);
        if (rc == Success && count > 0)
        {
            var samples = new Sample[count];
            rc = nvmlDeviceGetSamples(gpu.Handle, TotalPowerSamples, gpu.LastSeen, out int type, ref count, samples);
            if (rc == Success && count > 0 && type is UnsignedInt or UnsignedLong or UnsignedLongLong)
            {
                var taken = samples.Take((int)count).ToList();
                gpu.LastSeen = taken.Max(s => s.TimeStamp);
                // unsigned int sits in the low 4 bytes of the union
                return (float)taken.Average(s => (double)(type == UnsignedInt ? (uint)s.Value : s.Value)) / 1000f;
            }
        }
        if (rc == NotFound) return null; // no new samples yet: keep the last value
        return nvmlDeviceGetPowerUsage(gpu.Handle, out uint mw) == Success ? mw / 1000f : null;
    }
}
