# 0013 — A motion system, and three assumptions it disproved

**Knowledge-graph nodes:** `d.one-motion-vocabulary`, `k.subtree-opacity-is-not-free`,
`k.effects-are-per-frame`, `k.cached-bitmap-loses-to-visualbrush`, `k.begintime-null-never-runs`,
`cmp.motion`, `cmp.framemeter`, `m.animation-smoothness`

Every interaction now moves, on a shared vocabulary, and the whole thing is measured for the first
time. Three of the changes I was most confident about turned out to be wrong, and the measurements
are why.

## One vocabulary

There were two durations and four easings, used more or less at random, and half the controls had
no motion at all. The result read as jerky not because any single animation was wrong but because
nothing agreed with anything else.

Four durations, named for what they are for — `MotionQuick` (pointer feedback), `MotionBase` (state
changes), `MotionSlow` (surfaces arriving), `MotionGrand` (a card becoming a page) — and four
easings: `EaseEnter`, `EaseExit`, `EaseTravel`, `EaseEmphasis`. `Motion.cs` mirrors them for code,
deliberately duplicated rather than resolved from the resource dictionary: a mistyped resource key
fails silently and leaves an animation that never plays, whereas a mistyped field does not compile.

Newly animated, having previously been instant: caption buttons, the close button, the mode pill,
the shield, and the address bar's focus ring. Icon buttons and pills also gained press feedback —
a control that visibly gives under the pointer is the difference between "I clicked" and "I think I
clicked".

## Measuring "smooth"

Memory has had a bench since `0003`; motion had only ever been judged by eye. `FrameMeter` counts
composition frames across an animation and reports the rate, the CPU cost, and — the number that
actually matters — **how many frames took longer than 25 ms**, which is the threshold at which the
eye sees a hitch.

Mean frame rate turned out to be a poor measure on its own: it saturates at the compositor's cap, so
anything not catastrophic reads as fine, and a single 90 ms stall barely moves it. Process CPU time
was worse still — it counts every thread in a process that also hosts a browser, so identical
operations measured anywhere from 203 ms to 375 ms.

## Three things I was wrong about

**Drop shadows were halving the frame rate.** Each card carried a `DropShadowEffect`, and a blur is
recomputed every frame, per card, while a dozen of them scale and fade at once. Same build, only the
effect varying, first (cold) sample of each run discarded:

| Cards | Frame rate across the grid entrance |
| --- | --- |
| with `DropShadowEffect` | 56, 60, 62, 54, 59 fps — mean **58** |
| without | 103, 115, 94, 112, 115 fps — mean **108** |

The ranges do not overlap. Elevation now comes from a hairline plus background contrast, which costs
nothing per frame (`k.effects-are-per-frame`).

**Opacity is not free on a subtree.** I wrote a rule saying only Opacity and Transform get animated
because both are "composited and cost no layout". Half of that is wrong: opacity below 1 on a
container with overlapping children forces WPF to render that subtree into an offscreen surface
every frame. Fading the whole grid cost about a third of the frame rate against fading its scrim,
header and footer as individual leaves — long frames per entrance fell from **1.0 to 0.7**, rate
from ~69 to ~79 fps (`k.subtree-opacity-is-not-free`). Transforms genuinely are cheap; opacity is
cheap *on leaves*.

**Caching the zoomed card as a bitmap made it worse.** Rasterising the card once into a frozen
`RenderTargetBitmap` instead of re-rendering a `VisualBrush` per frame is the textbook optimisation.
Measured, it cost three times the CPU — **47 ms against 16 ms** across the animation, same build,
only the implementation varying.

The rasterisation is not the expense; it completes before the measurement window opens. The expense
is resampling: the card is 308 px wide and grows to fill the viewport, so the bitmap is upscaled
about four times on every frame. The `VisualBrush` re-renders the card's vectors and text *at* the
size being drawn, which is both cheaper and sharper — an upscaled bitmap goes soft exactly when the
card is largest and most looked at. Reverted (`k.cached-bitmap-loses-to-visualbrush`).

## A bug that made the grid open empty

`Motion.To` passed a `TimeSpan?` straight into `Timeline.BeginTime`. That property defaults to
`TimeSpan.Zero`, and setting it to `null` does not mean "no delay" — it means **the timeline never
begins**. Every animation that did not request a stagger was silently disabled, with no error.

It shipped as far as the next screenshot: the tab grid opened completely blank, because its
`Opacity` is declared `0` in XAML and the animation meant to raise it never ran. Only the staggered
card animations worked, because those pass an explicit delay (`k.begintime-null-never-runs`).

Worth recording because the failure mode is invisible in code review — the call site looks correct,
and a disabled animation leaves the property at a plausible value rather than throwing.

## Where it landed

Grid entrance, six runs after the fixes: **0.7 long frames** per 450 ms entrance, worst gap 17–78 ms,
typically ~70–120 fps. The animations are longer than before and drop fewer frames than before,
which was the whole point.

## Also

The theme switch cross-fades the chrome rather than snapping. Only the chrome — the page cannot fade
with it, because WebView2 ignores WPF opacity entirely (`k.wpf-airspace`), so dipping the window
would dissolve the frame and leave the content sitting there. Fading only what actually changes
colour is also the honest description of what a theme switch does.

## Not yet true

No reduced-motion setting; the animations are unconditional. Tab strip chips still appear and
disappear instantly — the entrance/exit of a `ListBoxItem` in a `StackPanel` has no clean hook.
`FrameMeter` only instruments the grid entrance and the zoom, not the many small state transitions,
which are individually too short to measure this way.
