namespace Kelvra;

/// <summary>What limits the frame rate right now.</summary>
public enum Bottleneck { Unknown, GpuBound, CpuBound, Capped }

/// <summary>The numbers the gaming overlay shows, worked out from the frames of the last second / minute / session.</summary>
/// <param name="Fps">Frames per second on screen right now, generated frames included (like the GPU makers' own overlays).</param>
/// <param name="FrameGeneration">Who generates frames ("AMD AFMF", "Intel XeSS-FG") when the driver reports it; else null.</param>
/// <param name="RenderedFps">With <paramref name="FrameGeneration"/>: the frames the game renders itself per second.</param>
/// <param name="PacedByDisplay">Frame times follow the screen's updates, not the game's presents (see <see cref="FrameStats"/>).</param>
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
    TimeSpan SessionLength,
    string? FrameGeneration = null,
    double? RenderedFps = null,
    bool PacedByDisplay = false)
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
/// <para>
/// Frame times normally run from one present to the next, like other overlays measure them. Two cases follow the
/// screen's updates instead (PresentMon's MsBetweenDisplayChange), because presents don't show what you see there:
/// frame generation in the driver (its frames have no present of their own), and frames the GPU paces itself (DLSS 4
/// flip metering, FSR frame generation), which a game hands over in bursts – present-to-present times jump between
/// ~0 and twice the real interval although the picture is smooth, which made lows and stutters look far worse.
/// </para>
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

    /// <summary>Generated frames seen this recently (wall clock, ms) mean frame generation is on.</summary>
    internal const long FrameGenHoldMs = 2000;
    private const int PaceWindow = 120, PaceEvery = 30;

    private readonly record struct Frame(double Time, double Ms, double? GpuBusy, double? Latency, bool ReflexLatency, bool Stutter, bool Generated);

    private readonly object _lock = new();

    // Present-to-present vs. screen-to-screen times of the game's own recent frames: which of them to go by (see TrackPacing)
    private readonly double[] _pacePresent = new double[PaceWindow];
    private readonly double[] _paceDisplay = new double[PaceWindow]; // NaN = never shown
    private int _paceCount, _paceHead, _paceSince;
    private bool _byDisplay, _wasByDisplay, _paceDecided;
    private long? _generatedAt;
    private string _generator = "";

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
    private readonly double[] _recentSorted = new double[MedianFrames]; // the same frame times, in order (see PushRecent)
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
            long now = _clock();
            if (s.Generated)
            {
                _generatedAt = now;
                _generator = s.FrameType;
            }
            else if (s.FrameTimeMs <= PauseMs) TrackPacing(s.FrameTimeMs, s.DisplayedMs);

            double ms;
            _lastArrival = now;
            bool byDisplay = ByDisplay(now);
            if (byDisplay != _wasByDisplay)
            {
                // The other kind of frame time from here on: don't mix the two in the lows and the stutter median
                _wasByDisplay = byDisplay;
                _ringHead = _ringCount = 0;
                _recentHead = _recentCount = 0;
                _version++;
            }
            if (byDisplay)
            {
                // A frame that never reached the screen isn't one you saw; the next shown frame's interval covers its time
                if (s.DisplayedMs is not double shown) return;
                ms = shown;
            }
            else
            {
                if (s.Generated) return; // (generated frames switch to the screen's timeline above)
                ms = s.FrameTimeMs;
            }
            double time = _timelineMs += ms;
            if (ms > PauseMs)
            {
                // A loading screen, alt-tab or a frozen game: time passes, but it isn't gameplay, so it doesn't
                // count as a stutter, a low or the session minimum
                if (double.IsNaN(_firstMs)) _firstMs = time - ms;
                _lastMs = time;
                _secondStart = time;
                return;
            }
            // Not before it's clear which frame times to go by: bunched presents (frame generation) would read as stutters
            bool stutter = _paceDecided && IsStutter(ms);
            PushRecent(ms);
            if (stutter)
            {
                _stutters++;
                _lastStutterAt = _lastArrival; // wall clock: "ago" stays right across alt-tab, when no frames are counted
            }

            // A generated frame adds no GPU time of the game's own (PresentMon says 0) but does add time: GPU busy stays
            // "share of the time the GPU worked for the game"
            Push(new Frame(time, ms, s.GpuBusyMs, s.LatencyMs, s.PcLatency != null, stutter, s.Generated));
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
            bool frameGen = !stale && FrameGenOn(now);

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
                TimeSpan.FromMilliseconds(Math.Max(0, _lastMs - _firstMs)),
                frameGen ? _generator : null,
                frameGen ? second.RenderedFps : null,
                !stale && ByDisplay(now));
        }
    }

    private bool FrameGenOn(long now) => _generatedAt is long at && now - at <= FrameGenHoldMs;

    /// <summary>Frame times follow the screen: frame generation is on, or the GPU paces the frames (see <see cref="TrackPacing"/>).</summary>
    private bool ByDisplay(long now) => _byDisplay || FrameGenOn(now);

    /// <summary>
    /// Every 30 of the game's frames, looks at its last 120: when (nearly) all of them reach the screen and the screen's
    /// intervals are much steadier than the present-to-present ones, the GPU paces the frames (DLSS 4 flip metering, FSR
    /// frame generation) and the screen's intervals are what you see. A margin keeps it from flipping back and forth.
    /// </summary>
    private void TrackPacing(double presentMs, double? displayedMs)
    {
        _pacePresent[_paceHead] = presentMs;
        _paceDisplay[_paceHead] = displayedMs ?? double.NaN;
        _paceHead = (_paceHead + 1) % PaceWindow;
        if (_paceCount < PaceWindow) _paceCount++;
        if (++_paceSince < PaceEvery || _paceCount < PaceWindow / 2) return;
        _paceSince = 0;
        _byDisplay = PacedByDisplay(_pacePresent.AsSpan(0, _paceCount), _paceDisplay.AsSpan(0, _paceCount), _byDisplay);
        _paceDecided = true;
    }

    /// <param name="displayed">Screen-to-screen times; NaN for frames that weren't shown.</param>
    /// <param name="now">Whether frame times follow the screen right now (switching back needs a clearer case).</param>
    internal static bool PacedByDisplay(ReadOnlySpan<double> presents, ReadOnlySpan<double> displayed, bool now)
    {
        if (presents.Length == 0) return false;
        int shown = 0;
        foreach (double d in displayed)
            if (!double.IsNaN(d)) shown++;
        double fraction = (double)shown / presents.Length;
        double present = Variation(presents), screen = Variation(displayed);
        return now
            ? fraction >= 0.8 && present >= 0.12 && screen <= present * 0.8
            : fraction >= 0.9 && present >= 0.18 && screen <= present * 0.5;
    }

    /// <summary>Standard deviation ÷ mean of the values (NaN ones skipped); 0 for fewer than two.</summary>
    internal static double Variation(ReadOnlySpan<double> values)
    {
        int n = 0;
        double sum = 0, sumSq = 0;
        foreach (double v in values)
        {
            if (double.IsNaN(v)) continue;
            n++;
            sum += v;
            sumSq += v * v;
        }
        if (n < 2 || sum <= 0) return 0;
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)) / mean;
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
        double median = _recentCount % 2 == 1
            ? _recentSorted[_recentCount / 2]
            : (_recentSorted[_recentCount / 2 - 1] + _recentSorted[_recentCount / 2]) / 2;
        return ms >= 2 * median && ms >= median + 8;
    }

    /// <summary>
    /// Adds a frame time to the last <see cref="MedianFrames"/>. <see cref="_recentSorted"/> holds the same values kept in
    /// order (the oldest taken out, the new one put in its place), so the median needs no sort on every frame:
    /// Add went from 0.94 to 0.12 µs per frame (measured), which matters at hundreds of frames a second.
    /// </summary>
    private void PushRecent(double ms)
    {
        if (_recentCount == MedianFrames)
        {
            int old = Array.BinarySearch(_recentSorted, 0, _recentCount, _recent[_recentHead]);
            Array.Copy(_recentSorted, old + 1, _recentSorted, old, _recentCount - old - 1);
            _recentCount--;
        }
        int at = Array.BinarySearch(_recentSorted, 0, _recentCount, ms);
        if (at < 0) at = ~at;
        Array.Copy(_recentSorted, at, _recentSorted, at + 1, _recentCount - at);
        _recentSorted[at] = ms;
        _recentCount++;

        _recent[_recentHead] = ms;
        _recentHead = (_recentHead + 1) % MedianFrames;
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

    private (double? Fps, double? FrameTime, double? GpuFraction, double Variation, double? Latency, double? RenderedFps) Second()
    {
        double from = _lastMs - SecondMs;
        int n = 0, gpuN = 0, latN = 0, reflexN = 0, generated = 0;
        double sum = 0, sumSq = 0, gpu = 0, gpuFt = 0, lat = 0, reflex = 0;
        for (int i = _ringCount - 1; i >= 0; i--)
        {
            var f = At(i);
            if (f.Time <= from) break; // newest first: everything before is older
            n++;
            sum += f.Ms;
            sumSq += f.Ms * f.Ms;
            if (f.Generated) generated++;
            if (f.GpuBusy is double g) { gpu += g; gpuFt += f.Ms; gpuN++; }
            if (f.Latency is double l)
            {
                // Reflex's input-to-screen latency, where the game reports it, is the better number: don't mix in estimates
                if (f.ReflexLatency) { reflex += l; reflexN++; }
                else { lat += l; latN++; }
            }
        }
        if (n == 0 || sum <= 0) return (null, null, null, 1, null, null);
        double mean = sum / n;
        double variation = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)) / mean;
        return (n * 1000 / sum, mean,
                gpuN > 0 && gpuFt > 0 ? Math.Min(1, gpu / gpuFt) : null,
                variation,
                reflexN > 0 ? reflex / reflexN : latN > 0 ? lat / latN : null,
                generated > 0 ? (n - generated) * 1000 / sum : null);
    }

    /// <summary>The frametime at percentile <paramref name="p"/> (0.99 = only 1 % of frames were slower) of the first <paramref name="length"/> sorted values.</summary>
    internal static double Percentile(double[] sorted, int length, double p) => sorted[PercentileIndex(length, p)];

    /// <summary>
    /// P99 / P99.9 lows of the 60-s window, into a reused buffer. Only two places of the sorted order are needed, so they're
    /// selected instead of sorting everything: the same values, but O(n) instead of O(n log n). It runs under the lock the
    /// frame reader needs, every 500 ms: at 1000 FPS (60k frames) 4.7 ms → about 1 ms (measured).
    /// </summary>
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
        var values = _sortBuffer60.AsSpan(0, _ringCount);
        int k01 = PercentileIndex(_ringCount, 0.999), k1 = PercentileIndex(_ringCount, 0.99);
        Select(values, k01);
        if (k1 < k01) Select(values[..k01], k1); // the k01 smallest are now in front, so the k1-th is among them
        _low1 = 1000 / values[k1];
        _low01 = 1000 / values[k01];
    }

    /// <summary>Where percentile <paramref name="p"/> sits in <paramref name="length"/> sorted values (see <see cref="Percentile"/>).</summary>
    private static int PercentileIndex(int length, double p) => Math.Clamp((int)Math.Ceiling(p * length) - 1, 0, length - 1);

    /// <summary>
    /// Quickselect: reorders <paramref name="values"/> so values[k] is what a full sort would put there, with only smaller
    /// or equal values before it. Average O(n). Median-of-three pivots, because steady frame times arrive almost sorted.
    /// </summary>
    internal static void Select(Span<double> values, int k)
    {
        int lo = 0, hi = values.Length - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (values[mid] < values[lo]) (values[mid], values[lo]) = (values[lo], values[mid]);
            if (values[hi] < values[lo]) (values[hi], values[lo]) = (values[lo], values[hi]);
            if (values[hi] < values[mid]) (values[hi], values[mid]) = (values[mid], values[hi]);
            double pivot = values[mid];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (values[i] < pivot) i++;
                while (values[j] > pivot) j--;
                if (i <= j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    i++;
                    j--;
                }
            }
            // Now [lo..j] ≤ pivot ≤ [i..hi], and anything between j and i equals the pivot
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }
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
