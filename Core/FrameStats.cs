namespace Kelvra;

/// <summary>What limits the frame rate right now.</summary>
public enum Bottleneck { Unknown, GpuBound, CpuBound, Capped }

/// <summary>The numbers the gaming overlay shows, worked out from the frames of the last second / minute / session.</summary>
public sealed record FrameSnapshot(
    double? Fps,
    double? FrameTimeMs,
    double? Low1,
    double? Low01,
    double? AvgFps,
    double? MinFps,
    double? MaxFps,
    int Stutters,
    double? SecondsSinceStutter,
    double? GpuBusyPercent,
    Bottleneck Bound,
    double? LatencyMs,
    string PresentMode,
    TimeSpan SessionLength)
{
    public static readonly FrameSnapshot Empty = new(null, null, null, null, null, null, null, 0, null, null,
                                                    Bottleneck.Unknown, null, "", TimeSpan.Zero);
}

/// <summary>One point of the frametime graph.</summary>
public readonly record struct FramePoint(double TimeMs, double FrameTimeMs, bool Stutter);

/// <summary>
/// Frame statistics for one game: rolling 1 s values (FPS, frametime, GPU busy, latency), 60 s percentile lows,
/// session averages, stutters, and the recent frametimes for the graph. Frames arrive on PresentMon's reader thread,
/// snapshots are taken on the UI thread, so everything is behind one lock.
/// </summary>
public sealed class FrameStats
{
    internal const double WindowMs = 60_000;
    private const double SecondMs = 1000;
    /// <summary>No frame for this long: the game is paused/frozen/minimized, so "current" values are gone.</summary>
    internal const double StaleMs = 2000;
    /// <summary>A gap this long between two frames is a pause (loading, alt-tab), not a slow frame.</summary>
    internal const double PauseMs = 500;
    private const int MedianFrames = 60;
    private const int HistogramBuckets = 10_001; // 0.1 ms steps up to 1 s

    private readonly record struct Frame(double Time, double Ms, double? GpuBusy, double? Latency, bool Stutter);

    private readonly object _lock = new();

    // The last 60 s of frames, oldest first, as a ring (read newest-first for "the last second" without walking the
    // whole minute, which at 1000+ FPS in menus is 60k+ frames)
    private Frame[] _ring = new Frame[4096];
    private int _ringHead, _ringCount;
    private double[] _sortBuffer60 = new double[4096];

    // The 60-s lows are a sort of every frame: done at most every LowsEveryMs, not on every overlay refresh
    private const long LowsEveryMs = 500;
    private long _version, _lowsVersion = -1, _lowsAt;
    private double? _low1, _low01;
    private readonly double[] _recent = new double[MedianFrames];
    private readonly double[] _sortBuffer = new double[MedianFrames];
    private int _recentCount, _recentHead;

    // Games can present to several swap chains (a launcher, a video, the game itself): follow the busiest one
    private readonly Dictionary<ulong, int> _swapCounts = new();
    private ulong _swapChain;
    private bool _hasSwapChain;
    /// <summary>Which swap chain is busiest is re-checked every this many frames (all swap chains counted).</summary>
    private const int SwapCheckEvery = 300;

    // Kelvra's own timeline: the sum of the frame times of the followed swap chain. It doesn't depend on which clock or
    // unit PresentMon writes its time column in (that mix-up once made every frame look hours old).
    private readonly Func<long> _clock;
    private long _lastArrival;
    private int _swapSamples;
    private double _timelineMs;

    /// <param name="clock">Milliseconds, to notice when frames stop arriving (tests pass their own).</param>
    public FrameStats(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    private double _firstMs = double.NaN, _lastMs = double.NaN;
    private long _sessionFrames;
    private double _sessionFrameMs;
    private readonly int[] _histogram = new int[HistogramBuckets];
    private double? _minFps, _maxFps;
    private double _secondStart = double.NaN;
    private int _stutters;
    private long? _lastStutterAt;
    private string _mode = "";
    private int _sync;
    private int _gpuBoundSeconds, _cpuBoundSeconds, _cappedSeconds;

    public void Add(FrameSample s)
    {
        lock (_lock)
        {
            if (!FollowSwapChain(s)) return;

            double ms = s.FrameTimeMs;
            double time = _timelineMs += ms;
            _lastArrival = _clock();
            if (ms > PauseMs)
            {
                // A loading screen, alt-tab or a frozen game: time passes, but it isn't gameplay, so it doesn't
                // count as a stutter, a low or the session minimum
                if (double.IsNaN(_firstMs)) _firstMs = time - ms;
                _lastMs = time;
                _secondStart = time;
                return;
            }
            bool stutter = IsStutter(ms);
            PushRecent(ms);
            if (stutter)
            {
                _stutters++;
                _lastStutterAt = _lastArrival; // wall clock: "ago" stays right across alt-tab, when no frames are counted
            }

            Push(new Frame(time, ms, s.GpuBusyMs, s.LatencyMs, stutter));
            while (_ringCount > 0 && _ring[_ringHead].Time < time - WindowMs)
            {
                _ringHead = (_ringHead + 1) % _ring.Length;
                _ringCount--;
            }
            _version++;

            if (double.IsNaN(_firstMs)) _firstMs = time - ms;
            _lastMs = time;
            _sessionFrames++;
            _sessionFrameMs += ms;
            _histogram[Math.Min((int)(ms * 10), HistogramBuckets - 1)]++;

            _mode = s.PresentMode;
            _sync = s.SyncInterval;

            if (double.IsNaN(_secondStart)) _secondStart = time;
            else if (time - _secondStart >= SecondMs)
            {
                _secondStart = time;
                CloseSecond();
            }
        }
    }

    /// <summary>
    /// Frames were left out (the game was hidden, or a late backlog was skipped). Moves the timeline on by a second, so
    /// the "right now" values start fresh with the next frame instead of mixing in frames from before the gap.
    /// </summary>
    public void MarkGap()
    {
        lock (_lock)
        {
            if (double.IsNaN(_lastMs)) return;
            _timelineMs += SecondMs;
            _lastMs = _timelineMs;
            _secondStart = _timelineMs;
        }
    }

    /// <summary>Current values. With no frame for <see cref="StaleMs"/> (paused, minimized, frozen) the "right now" values are empty.</summary>
    public FrameSnapshot Snapshot()
    {
        lock (_lock)
        {
            if (_sessionFrames == 0) return FrameSnapshot.Empty;
            long now = _clock();
            bool stale = now - _lastArrival > StaleMs;
            var second = Second();

            if (_version != _lowsVersion && (_lowsVersion < 0 || now - _lowsAt >= LowsEveryMs)) UpdateLows(now);
            double? low1 = _low1, low01 = _low01;

            return new FrameSnapshot(
                stale ? null : second.Fps,
                stale ? null : second.FrameTime,
                low1, low01,
                _sessionFrameMs > 0 ? _sessionFrames * 1000 / _sessionFrameMs : null,
                _minFps, _maxFps,
                _stutters,
                _lastStutterAt is long at ? Math.Max(0, (now - at) / 1000.0) : null,
                stale ? null : second.GpuFraction * 100,
                stale ? Bottleneck.Unknown : Classify(second.GpuFraction, second.Variation, _sync > 0),
                stale ? null : second.Latency,
                _mode,
                TimeSpan.FromMilliseconds(Math.Max(0, _lastMs - _firstMs)));
        }
    }

    /// <summary>Frames of the last <paramref name="spanMs"/> (up to the newest one), oldest first, for the frametime graph.</summary>
    public FramePoint[] Recent(double spanMs)
    {
        lock (_lock)
        {
            double from = _lastMs - spanMs;
            int first = _ringCount;
            while (first > 0 && At(first - 1).Time >= from) first--;
            var points = new FramePoint[_ringCount - first];
            for (int i = first; i < _ringCount; i++)
            {
                var f = At(i);
                points[i - first] = new FramePoint(f.Time, f.Ms, f.Stutter);
            }
            return points;
        }
    }

    /// <summary>Whole-session totals for the session log: average and percentile lows over every frame, and where the time went.</summary>
    public (double? Avg, double? Low1, double? Low01, int Stutters, int GpuBoundSeconds, int CpuBoundSeconds, int CappedSeconds, TimeSpan Length) Session()
    {
        lock (_lock)
        {
            if (_sessionFrames == 0) return (null, null, null, 0, 0, 0, 0, TimeSpan.Zero);
            return (_sessionFrames * 1000 / _sessionFrameMs,
                    1000 / HistogramPercentile(0.99), 1000 / HistogramPercentile(0.999),
                    _stutters, _gpuBoundSeconds, _cpuBoundSeconds, _cappedSeconds,
                    TimeSpan.FromMilliseconds(Math.Max(0, _lastMs - _firstMs)));
        }
    }

    /// <summary>
    /// Intel's way of reading it: when the GPU works ≥ 85 % of every frame, the GPU is the limit. Otherwise the GPU
    /// waits – on purpose when VSync or a limiter holds the frame rate (then frame times are very even), or for the
    /// CPU. PresentMon's own "CPU busy" isn't used: it includes the time a game waits for the GPU, so a GPU-bound game
    /// reads ~100 % there too.
    /// </summary>
    /// <param name="variation">How uneven the last second's frame times are (standard deviation ÷ mean).</param>
    internal static Bottleneck Classify(double? gpuFraction, double variation, bool vsync)
    {
        if (gpuFraction is not double gpu) return Bottleneck.Unknown;
        if (gpu >= 0.85) return Bottleneck.GpuBound;
        if (gpu < 0.80 && (vsync || variation < 0.05)) return Bottleneck.Capped;
        return Bottleneck.CpuBound;
    }

    /// <summary>A frame that took at least twice the recent median, and at least 8 ms more (so tiny dips at 300 FPS don't count).</summary>
    private bool IsStutter(double ms)
    {
        if (_recentCount < 20) return false;
        Array.Copy(_recent, _sortBuffer, _recentCount);
        Array.Sort(_sortBuffer, 0, _recentCount);
        double median = _recentCount % 2 == 1
            ? _sortBuffer[_recentCount / 2]
            : (_sortBuffer[_recentCount / 2 - 1] + _sortBuffer[_recentCount / 2]) / 2;
        return ms >= 2 * median && ms >= median + 8;
    }

    private void PushRecent(double ms)
    {
        _recent[_recentHead] = ms;
        _recentHead = (_recentHead + 1) % MedianFrames;
        if (_recentCount < MedianFrames) _recentCount++;
    }

    private bool FollowSwapChain(FrameSample s)
    {
        if (!_hasSwapChain)
        {
            _swapChain = s.SwapChain;
            _hasSwapChain = true;
        }
        _swapCounts[s.SwapChain] = _swapCounts.GetValueOrDefault(s.SwapChain) + 1;
        if (++_swapSamples >= SwapCheckEvery)
        {
            var busiest = _swapCounts.MaxBy(kv => kv.Value);
            if (busiest.Key != _swapChain && busiest.Value > _swapCounts.GetValueOrDefault(_swapChain)) _swapChain = busiest.Key;
            _swapCounts.Clear();
            _swapSamples = 0;
        }
        return s.SwapChain == _swapChain;
    }

    /// <summary>Once per second of frames: session min/max FPS and the time spent GPU-/CPU-bound or capped.</summary>
    private void CloseSecond()
    {
        var second = Second();
        if (second.Fps is double fps)
        {
            _minFps = _minFps is double min ? Math.Min(min, fps) : fps;
            _maxFps = _maxFps is double max ? Math.Max(max, fps) : fps;
        }
        switch (Classify(second.GpuFraction, second.Variation, _sync > 0))
        {
            case Bottleneck.GpuBound: _gpuBoundSeconds++; break;
            case Bottleneck.CpuBound: _cpuBoundSeconds++; break;
            case Bottleneck.Capped: _cappedSeconds++; break;
        }
    }

    private (double? Fps, double? FrameTime, double? GpuFraction, double Variation, double? Latency) Second()
    {
        double from = _lastMs - SecondMs;
        int n = 0, gpuN = 0, latN = 0;
        double sum = 0, sumSq = 0, gpu = 0, gpuFt = 0, lat = 0;
        for (int i = _ringCount - 1; i >= 0; i--)
        {
            var f = At(i);
            if (f.Time <= from) break; // newest first: everything before is older
            n++;
            sum += f.Ms;
            sumSq += f.Ms * f.Ms;
            if (f.GpuBusy is double g) { gpu += g; gpuFt += f.Ms; gpuN++; }
            if (f.Latency is double l) { lat += l; latN++; }
        }
        if (n == 0 || sum <= 0) return (null, null, null, 1, null);
        double mean = sum / n;
        double variation = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)) / mean;
        return (n * 1000 / sum, mean,
                gpuN > 0 && gpuFt > 0 ? Math.Min(1, gpu / gpuFt) : null,
                variation, latN > 0 ? lat / latN : null);
    }

    /// <summary>The frametime at percentile <paramref name="p"/> (0.99 = only 1 % of frames were slower) of the first <paramref name="length"/> sorted values.</summary>
    internal static double Percentile(double[] sorted, int length, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * length) - 1, 0, length - 1)];

    /// <summary>P99 / P99.9 lows of the 60-s window (one sort, into a reused buffer).</summary>
    private void UpdateLows(long now)
    {
        _lowsVersion = _version;
        _lowsAt = now;
        if (_ringCount < 2)
        {
            _low1 = _low01 = null;
            return;
        }
        if (_sortBuffer60.Length < _ringCount) _sortBuffer60 = new double[_ring.Length];
        for (int i = 0; i < _ringCount; i++) _sortBuffer60[i] = At(i).Ms;
        Array.Sort(_sortBuffer60, 0, _ringCount);
        _low1 = 1000 / Percentile(_sortBuffer60, _ringCount, 0.99);
        _low01 = 1000 / Percentile(_sortBuffer60, _ringCount, 0.999);
    }

    private Frame At(int i) => _ring[(_ringHead + i) % _ring.Length];

    private void Push(Frame f)
    {
        if (_ringCount == _ring.Length)
        {
            var bigger = new Frame[_ring.Length * 2];
            for (int i = 0; i < _ringCount; i++) bigger[i] = At(i);
            _ring = bigger;
            _ringHead = 0;
        }
        _ring[(_ringHead + _ringCount) % _ring.Length] = f;
        _ringCount++;
    }

    private double HistogramPercentile(double p)
    {
        long target = (long)Math.Ceiling(p * _sessionFrames);
        long seen = 0;
        for (int i = 0; i < HistogramBuckets; i++)
        {
            seen += _histogram[i];
            if (seen >= target) return Math.Max(0.1, (i + 0.5) / 10);
        }
        return (HistogramBuckets - 0.5) / 10;
    }
}
