using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace FluxBrowser.Motion;

/// <summary>
/// Drives time-based animations from <see cref="CompositionTarget.Rendering"/>, so every step lands on a real composition frame
/// (60, 120 or 144 Hz alike) instead of drifting against a fixed timer. The event is only subscribed while something is animating,
/// so an idle window costs nothing. UI thread only.
/// </summary>
internal sealed class FrameLoop
{
    internal sealed class Animation
    {
        internal Animation(long start, double durationMs, Action<double> step, Action? completed)
        {
            Start = start; DurationMs = durationMs; Step = step; Completed = completed;
        }

        internal long Start { get; }
        internal double DurationMs { get; }
        internal Action<double> Step { get; }
        internal Action? Completed { get; }
        public bool IsRunning { get; internal set; } = true;

        /// <summary>Stops without running the completion callback.</summary>
        public void Stop() => IsRunning = false;
    }

    private readonly List<Animation> _running = [];
    private bool _subscribed;

    /// <summary>Runs <paramref name="step"/> with progress 0..1 over <paramref name="durationMs"/>; the first step runs immediately.</summary>
    public Animation Start(double durationMs, Action<double> step, Action? completed = null)
    {
        var animation = new Animation(Stopwatch.GetTimestamp(), Math.Max(1, durationMs), step, completed);
        step(0);
        _running.Add(animation);
        if (!_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
        return animation;
    }

    public void StopAll()
    {
        foreach (Animation animation in _running) animation.IsRunning = false;
        Release();
    }

    private void OnRendering(object? sender, object args)
    {
        // Steps and completions may start or stop other animations; iterate a snapshot.
        foreach (Animation animation in _running.ToArray())
        {
            if (!animation.IsRunning) continue;
            double t = Math.Clamp(Stopwatch.GetElapsedTime(animation.Start).TotalMilliseconds / animation.DurationMs, 0, 1);
            animation.Step(t);
            if (t < 1 || !animation.IsRunning) continue;
            animation.IsRunning = false;
            animation.Completed?.Invoke();
        }
        _running.RemoveAll(a => !a.IsRunning);
        if (_running.Count == 0) Release();
    }

    private void Release()
    {
        _running.RemoveAll(a => !a.IsRunning);
        if (_running.Count > 0 || !_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }
}

internal static class Easing
{
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double OutQuart(double t) => 1 - Math.Pow(1 - t, 4);
    public static double InQuad(double t) => t * t;
    public static double InOutCubic(double t) => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
