#if ANDOYA_SHADOW_PROFILE
using System.Diagnostics;
using KSA;

namespace AndoyaSpaceport;

/// <summary>
/// Test build only: measures what the site shadow pass costs. It turns on the GPU profiler of the game and, every
/// window of frames, logs the mean frame interval, the mean GPU time of the "Cascaded Shadows" region (all cascades,
/// planet, parts and site), the mean CPU time of the site pass and how many cascades it drew. When the file
/// shadow-ab.txt is next to the DLL, the windows switch the site pass on and off in turn, so both states are measured
/// at the same camera.
/// </summary>
internal static class ShadowProfile
{
    private const int Window = 120;
    private const int Skip = 12;

    private static bool _started;
    private static bool _ab;
    private static long _lastFrame;
    private static int _frame;
    private static double _sumFrameMs;
    private static double _sumGpuMs;
    private static int _gpuCount;
    private static double _sumCpuUs;
    private static int _cascadesDrawn;
    private static double _distance;
    private static int _gpuTag = -1;
    private static int _lastGpuFrame = -1;
    private static Action<string> _log = _ => { };

    internal static void Start(Action<string> log)
    {
        _log = log;
        string dir = Path.GetDirectoryName(typeof(ShadowProfile).Assembly.Location) ?? ".";
        _ab = File.Exists(Path.Combine(dir, "shadow-ab.txt"));
        if (File.Exists(Path.Combine(dir, "shadow-off.txt")))
            SiteShadows.Enabled = false;
        log("[AndoyaSpaceport profile] on, A/B " + _ab + ", site pass " + SiteShadows.Enabled);
    }

    private static bool _skyCleared;

    // The clouds and their shadows change from one game start to the next at the same game time, so the test build
    // turns them off to make pictures of two game starts comparable. Called from the update phase, before the frame is
    // recorded; the renderer rebuild takes effect in the next frame.
    internal static void ClearSky()
    {
        if (_skyCleared)
            return;
        _skyCleared = true;
        GameSettings.Current.Graphics.Clouds = false;
        GameSettings.Current.Graphics.CloudShadows = GameSettings.CloudShadows.Off;
        Program.ScheduleRendererRebuild();
        _log("[AndoyaSpaceport profile] clouds and cloud shadows off");
    }

    internal static void OnCascade(IViewport viewport, int cascade)
    {
        if (cascade != 0 || viewport != Program.MainViewport)
            return;
        if (!_started)
        {
            _started = true;
            if (Profiler.Gpu.IsSupported)
            {
                Profiler.Gpu.IsActive = true;
                Profiler.Gpu.Paused = false;
            }
            _gpuTag = Profiler.GpuTag.CascadedShadows.Index;
        }
        long now = Stopwatch.GetTimestamp();
        _distance = SiteShadows.SiteDistance(viewport);
        int inWindow = _frame % Window;
        if (inWindow >= Skip && _lastFrame != 0)
        {
            _sumFrameMs += (now - _lastFrame) * 1000.0 / Stopwatch.Frequency;
            ReadGpu();
        }
        _lastFrame = now;
        _frame++;
        if (_frame % Window == 0)
        {
            int n = Window - Skip;
            _log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[AndoyaSpaceport profile] site pass {0}: frame {1:F2} ms, CSM GPU {2:F3} ms ({3} samples), site pass CPU {4:F1} us, cascades drawn {5:F2} per frame, site distance {6:F0} m",
                SiteShadows.Enabled ? "ON " : "OFF", _sumFrameMs / n, _gpuCount > 0 ? _sumGpuMs / _gpuCount : -1.0, _gpuCount,
                _sumCpuUs / n, _cascadesDrawn / (double)n, _distance));
            _sumFrameMs = _sumGpuMs = _sumCpuUs = 0;
            _gpuCount = _cascadesDrawn = 0;
            if (_ab)
                SiteShadows.Enabled = !SiteShadows.Enabled;
        }
    }

    internal static void OnDrawn(long ticks)
    {
        if (_frame % Window < Skip)
            return;
        _sumCpuUs += ticks * 1e6 / Stopwatch.Frequency;
        _cascadesDrawn++;
    }

    private static void ReadGpu()
    {
        GpuProfiler gpu = Profiler.Gpu;
        int index = gpu.LastCompleteFrameIndex;
        if (index < 0 || index == _lastGpuFrame || _gpuTag < 0)
            return;
        _lastGpuFrame = index;
        ProfilerFrame frame = gpu.Frames[index];
        if (!frame.IsValid)
            return;
        foreach (Sample s in frame.AsSpan())
        {
            if (s.IdIndex == _gpuTag)
            {
                _sumGpuMs += s.Ticks * 1000.0 / Stopwatch.Frequency;
                _gpuCount++;
                return;
            }
        }
    }
}
#endif
