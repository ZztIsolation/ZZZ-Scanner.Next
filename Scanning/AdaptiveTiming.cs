namespace ZZZScannerNext.Scanning;

internal sealed class AdaptiveTimingState
{
    public const int DefaultWarmupItems = 12;

    private readonly int _warmupItems;
    private readonly List<PanelTimingSample> _panelSamples = new();
    private readonly List<PanelTimingSample> _postScrollPanelSamples = new();

    public AdaptiveTimingState(int warmupItems = DefaultWarmupItems)
    {
        _warmupItems = Math.Max(1, warmupItems);
    }

    public AdaptivePanelTimingDecision ResolvePanelTiming(ScanProfile profile, string captureMode)
    {
        return ResolvePanelTiming(
            profile,
            captureMode,
            PanelAcceptMode.Safe,
            postScrollFirstCell: false,
            PostScrollPanelAcceptMode.Safe,
            panelMinAcceptFloorMs: 120,
            PanelFloorMode.Static,
            sceneAdaptiveFloorEligible: false,
            sameRowPanelMinAcceptFloorMs: 105,
            postScrollPanelMinAcceptFloorMs: 110);
    }

    public AdaptivePanelTimingDecision ResolvePanelTiming(
        ScanProfile profile,
        string captureMode,
        PanelAcceptMode panelAcceptMode,
        bool postScrollFirstCell,
        PostScrollPanelAcceptMode postScrollPanelAcceptMode,
        int panelMinAcceptFloorMs,
        PanelFloorMode panelFloorMode,
        bool sceneAdaptiveFloorEligible,
        int sameRowPanelMinAcceptFloorMs,
        int postScrollPanelMinAcceptFloorMs)
    {
        var isDxgi = string.Equals(captureMode, "dxgi", StringComparison.OrdinalIgnoreCase);
        var warmupMinimum = isDxgi ? 120 : 60;
        var staticMinimumFloor = isDxgi ? Math.Clamp(panelMinAcceptFloorMs, 90, 120) : 60;
        var sameRowMinimumFloor = isDxgi ? Math.Clamp(sameRowPanelMinAcceptFloorMs, 100, 120) : 60;
        var postScrollMinimumFloor = isDxgi ? Math.Clamp(postScrollPanelMinAcceptFloorMs, 100, 120) : 60;
        var configuredMinimum = Math.Clamp(
            Math.Max(profile.PanelChangedMinimumAcceptMs, warmupMinimum),
            60,
            profile.LoadTimeoutMs);

        if (_panelSamples.Count < _warmupItems)
        {
            var warmupFloor = panelFloorMode == PanelFloorMode.SceneAdaptive && isDxgi ? 120 : staticMinimumFloor;
            var warmupMinimumAccept = isDxgi
                ? Math.Clamp(Math.Min(configuredMinimum, warmupFloor), 60, profile.LoadTimeoutMs)
                : configuredMinimum;
            return new AdaptivePanelTimingDecision(
                warmupMinimumAccept,
                isDxgi ? 2 : 1,
                _panelSamples.Count,
                WarmupComplete: false,
                AppliedAdaptiveMinimum: false,
                Reason: "warmup",
                PanelAcceptMode.Safe,
                PostScrollPanelAcceptMode.Safe,
                panelFloorMode,
                warmupFloor,
                sameRowMinimumFloor,
                postScrollMinimumFloor,
                "warmup");
        }

        var usePostScrollEarly = postScrollFirstCell && postScrollPanelAcceptMode == PostScrollPanelAcceptMode.AdaptiveAfterScroll;
        if (usePostScrollEarly && _postScrollPanelSamples.Count < 2)
        {
            var postScrollWarmupFloor = panelFloorMode == PanelFloorMode.SceneAdaptive && isDxgi ? 120 : staticMinimumFloor;
            var postScrollWarmupMinimum = isDxgi
                ? Math.Clamp(Math.Min(configuredMinimum, postScrollWarmupFloor), 60, profile.LoadTimeoutMs)
                : configuredMinimum;
            return new AdaptivePanelTimingDecision(
                postScrollWarmupMinimum,
                isDxgi ? 2 : 1,
                _panelSamples.Count,
                WarmupComplete: true,
                AppliedAdaptiveMinimum: false,
                Reason: "post_scroll_warmup",
                PanelAcceptMode.Safe,
                PostScrollPanelAcceptMode.Safe,
                panelFloorMode,
                postScrollWarmupFloor,
                sameRowMinimumFloor,
                postScrollMinimumFloor,
                "post_scroll_warmup");
        }

        var adaptiveMinimumFloor = ResolvePanelMinimumFloor(
            isDxgi,
            panelFloorMode,
            postScrollFirstCell,
            sceneAdaptiveFloorEligible,
            staticMinimumFloor,
            sameRowMinimumFloor,
            postScrollMinimumFloor,
            out var panelFloorReason);
        var selectedConfiguredMinimum = isDxgi
            ? Math.Clamp(Math.Min(configuredMinimum, adaptiveMinimumFloor), 60, profile.LoadTimeoutMs)
            : configuredMinimum;

        var useEarlyFullRoi = panelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi && (!postScrollFirstCell || usePostScrollEarly);
        var sampleSource = usePostScrollEarly ? _postScrollPanelSamples : _panelSamples;
        var readyMs = sampleSource
            .Select(sample => useEarlyFullRoi ? sample.FullRoiMilliseconds : sample.ReadyMilliseconds)
            .Where(value => value > 0)
            .ToArray();
        if (readyMs.Length == 0)
        {
            return new AdaptivePanelTimingDecision(
                selectedConfiguredMinimum,
                isDxgi ? 2 : 1,
                _panelSamples.Count,
                WarmupComplete: true,
                AppliedAdaptiveMinimum: false,
                Reason: "no_ready_samples",
                PanelAcceptMode.Safe,
                PostScrollPanelAcceptMode.Safe,
                panelFloorMode,
                adaptiveMinimumFloor,
                sameRowMinimumFloor,
                postScrollMinimumFloor,
                panelFloorReason);
        }

        var frameLoopPercentile = useEarlyFullRoi ? 0.50 : 0.95;
        var readyPercentile = useEarlyFullRoi ? 0.90 : 0.95;
        var frameLoopP = Percentile(sampleSource.Select(sample => sample.FrameLoopMilliseconds).Where(value => value > 0), frameLoopPercentile);
        var safetyPadding = useEarlyFullRoi ? 0 : 10;
        var adaptiveMinimum = (int)Math.Ceiling(Percentile(readyMs, readyPercentile) + (useEarlyFullRoi ? 0 : frameLoopP) + safetyPadding);
        var selectedMinimum = Math.Clamp(
            Math.Max(adaptiveMinimumFloor, adaptiveMinimum),
            adaptiveMinimumFloor,
            configuredMinimum);
        var requiredStableFrames = 1;

        return new AdaptivePanelTimingDecision(
            selectedMinimum,
            requiredStableFrames,
            _panelSamples.Count,
            WarmupComplete: true,
            AppliedAdaptiveMinimum: useEarlyFullRoi || selectedMinimum < configuredMinimum,
            Reason: selectedMinimum < configuredMinimum
                ? usePostScrollEarly ? "adaptive_after_scroll" : useEarlyFullRoi ? "adaptive_early_full_roi" : "adaptive_p95"
                : usePostScrollEarly ? "adaptive_after_scroll_roi_stability" : useEarlyFullRoi ? "adaptive_early_full_roi_roi_stability" : "configured_safety",
            useEarlyFullRoi ? PanelAcceptMode.AdaptiveEarlyFullRoi : PanelAcceptMode.Safe,
            usePostScrollEarly ? PostScrollPanelAcceptMode.AdaptiveAfterScroll : PostScrollPanelAcceptMode.Safe,
            panelFloorMode,
            adaptiveMinimumFloor,
            sameRowMinimumFloor,
            postScrollMinimumFloor,
            panelFloorReason);
    }

    private static int ResolvePanelMinimumFloor(
        bool isDxgi,
        PanelFloorMode panelFloorMode,
        bool postScrollFirstCell,
        bool sceneAdaptiveFloorEligible,
        int staticMinimumFloor,
        int sameRowMinimumFloor,
        int postScrollMinimumFloor,
        out string reason)
    {
        if (!isDxgi || panelFloorMode != PanelFloorMode.SceneAdaptive)
        {
            reason = "static";
            return staticMinimumFloor;
        }

        if (postScrollFirstCell)
        {
            reason = "scene_post_scroll";
            return postScrollMinimumFloor;
        }

        if (sceneAdaptiveFloorEligible)
        {
            reason = "scene_same_row";
            return sameRowMinimumFloor;
        }

        reason = "scene_safe";
        return 120;
    }

    public void ObservePanel(
        double? changeMilliseconds,
        double? fullRoiMilliseconds,
        double? stableMilliseconds,
        double waitMilliseconds,
        double captureMilliseconds,
        double frameLoopMilliseconds,
        bool postScrollFirstCell)
    {
        var readyMilliseconds = new[]
            {
                changeMilliseconds,
                fullRoiMilliseconds
            }
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .DefaultIfEmpty(waitMilliseconds)
            .Max();

        var sample = new PanelTimingSample(
            readyMilliseconds,
            fullRoiMilliseconds is > 0 ? fullRoiMilliseconds.Value : readyMilliseconds,
            waitMilliseconds,
            captureMilliseconds,
            frameLoopMilliseconds);

        _panelSamples.Add(sample);

        if (_panelSamples.Count > 96)
        {
            _panelSamples.RemoveAt(0);
        }

        if (postScrollFirstCell)
        {
            _postScrollPanelSamples.Add(sample);
            if (_postScrollPanelSamples.Count > 32)
            {
                _postScrollPanelSamples.RemoveAt(0);
            }
        }
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .OrderBy(value => value)
            .ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private readonly record struct PanelTimingSample(
        double ReadyMilliseconds,
        double FullRoiMilliseconds,
        double WaitMilliseconds,
        double CaptureMilliseconds,
        double FrameLoopMilliseconds);
}

internal sealed record AdaptivePanelTimingDecision(
    int MinimumAcceptMilliseconds,
    int RequiredStableFrames,
    int SampleCount,
    bool WarmupComplete,
    bool AppliedAdaptiveMinimum,
    string Reason,
    PanelAcceptMode EffectivePanelAcceptMode,
    PostScrollPanelAcceptMode EffectivePostScrollPanelAcceptMode,
    PanelFloorMode PanelFloorMode,
    int PanelMinAcceptFloorMs,
    int SameRowPanelFloorMs,
    int PostScrollPanelFloorMs,
    string PanelFloorReason);

internal sealed class AdaptiveOcrThrottle
{
    private readonly int _highBacklogThreshold;
    private readonly int _lowBacklogThreshold;
    private int _highBacklogStreak;
    private int _lowBacklogStreak;
    private int _currentDelayMilliseconds;

    public AdaptiveOcrThrottle(int queueCapacity)
    {
        var capacity = Math.Max(1, queueCapacity);
        _highBacklogThreshold = Math.Max(1, (int)Math.Ceiling(capacity * 0.60));
        _lowBacklogThreshold = Math.Max(0, (int)Math.Floor(capacity * 0.25));
    }

    public AdaptiveThrottleDecision Observe(int backlog)
    {
        var previousDelay = _currentDelayMilliseconds;
        if (backlog >= _highBacklogThreshold)
        {
            _highBacklogStreak++;
            _lowBacklogStreak = 0;
            if (_highBacklogStreak >= 2)
            {
                _currentDelayMilliseconds = Math.Min(300, _currentDelayMilliseconds + 25);
            }
        }
        else if (backlog <= _lowBacklogThreshold)
        {
            _lowBacklogStreak++;
            _highBacklogStreak = 0;
            if (_lowBacklogStreak >= 3)
            {
                _currentDelayMilliseconds = Math.Max(0, _currentDelayMilliseconds - 25);
            }
        }
        else
        {
            _highBacklogStreak = 0;
            _lowBacklogStreak = 0;
        }

        return new AdaptiveThrottleDecision(
            _currentDelayMilliseconds,
            previousDelay != _currentDelayMilliseconds,
            _highBacklogThreshold,
            _lowBacklogThreshold);
    }
}

internal sealed record AdaptiveThrottleDecision(
    int DelayMilliseconds,
    bool Changed,
    int HighBacklogThreshold,
    int LowBacklogThreshold);
