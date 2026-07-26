using System.Diagnostics;
using System.Windows.Media;
using Hearth.Core;

namespace Hearth.Views;

/// <summary>
/// Counts composition frames across an animation and logs the rate.
///
/// "Smoother" is the one quality claim in this project that was never measured.
/// Memory has had a bench since 0003 and the house rule is that claims are
/// measured rather than estimated (CLAUDE.md), but motion was only ever judged
/// by eye -- and by eye, a 60 fps animation and a 34 fps one both look "fine" in
/// a screenshot and quite different in the hand.
///
/// CompositionTarget.Rendering fires once per frame the renderer actually
/// composites, so counting it across a known duration gives the real delivered
/// rate rather than the requested one. A long animation that drops to 30 fps is
/// worse than a short one that holds 60, which is exactly the trade this commit
/// is making and therefore exactly the thing worth watching.
///
/// Costs a delegate call per frame and only exists when HEARTH_TRACE=1.
/// </summary>
internal sealed class FrameMeter : IDisposable
{
    private readonly string _label;
    private readonly Stopwatch _clock;
    private readonly TimeSpan _cpuAtStart;
    private int _frames;

    private FrameMeter(string label)
    {
        _label = label;
        _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
        _clock = Stopwatch.StartNew();
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Returns null when tracing is off, so callers pay nothing.</summary>
    public static FrameMeter? Start(string label) =>
        Diag.Enabled ? new FrameMeter(label) : null;

    private double _lastTickMs;
    private double _worstGapMs;
    private int _longFrames;

    private void OnRendering(object? sender, EventArgs e)
    {
        _frames++;

        var now = _clock.Elapsed.TotalMilliseconds;
        if (_lastTickMs > 0)
        {
            var gap = now - _lastTickMs;
            if (gap > _worstGapMs) _worstGapMs = gap;

            // A frame that took longer than ~25 ms is one the eye can see as a
            // hitch. Counting these is what "smooth" actually means; average
            // frame rate hides them completely, because a single 90 ms stall
            // barely moves the mean.
            if (gap > 25) _longFrames++;
        }

        _lastTickMs = now;
    }

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();

        var cpu = Process.GetCurrentProcess().TotalProcessorTime - _cpuAtStart;
        var seconds = _clock.Elapsed.TotalSeconds;
        var fps = seconds > 0 ? _frames / seconds : 0;

        // Long frames are the headline number, not the average.
        //
        // Mean frame rate is a poor measure of smoothness: it saturates at the
        // compositor's cap, so anything that is not catastrophic reads as
        // "fine", and one 90 ms stall barely moves it. Process CPU time turned
        // out to be worse still -- it counts every thread in a process that also
        // hosts a browser, so identical operations measured 203 to 375 ms.
        // A count of frames the eye can see as a hitch is the thing that
        // actually corresponds to the complaint.
        Diag.Log($"anim {_label}: {_frames} frames in {_clock.ElapsedMilliseconds} ms "
               + $"= {fps:0} fps, {_longFrames} long, worst gap {_worstGapMs:0} ms, "
               + $"cpu {cpu.TotalMilliseconds:0} ms");
    }
}
