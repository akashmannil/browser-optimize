# 0015 — Transitions, and the one that was never on screen

**Knowledge-graph nodes:** `d.startup-is-a-transition`, `d.the-restart-is-a-transition`,
`d.one-marker-travels`, `d.reveal-when-the-page-is-ready`, `cmp.veil`, `cmp.hearth-mark`,
`k.the-placeholder-was-never-visible`, `k.navigation-completed-is-not-first-paint`,
`k.starving-the-layout-phase-blanks-webview2`, `k.layered-windows-are-software-drawn`,
`d.motion-honours-the-system-switch`

Commit `0013` built a motion vocabulary and applied it to the controls. It did not touch the
**transitions between states**, which is where the app still cut rather than moved: it appeared
fully formed at startup, vanished and reappeared across a mode restart, and swapped tabs with a
black frame in the middle. Those are the moments a browser is judged on, because they are the ones
that happen every time.

The largest finding here is not an animation. It is that the transition this codebase has claimed
since `0004` — dissolving a tab's snapshot into the live page — **had never once been visible.**

## The placeholder was never on screen

Hibernation captures a screenshot of every tab. On switching back to an evicted tab, the shell
paints that screenshot, rebuilds the renderer behind it, and fades it out when the page has painted.
That is what the code said and what three commits' notes described.

Filmed, at ~37 frames per second, a switch to an evicted tab looked like this:

| | pane luminance |
| --- | ---: |
| the page being left | 229 |
| **mid-switch** | **18** |
| the page arrived at | 236 |

A near-black frame. Not the snapshot — nothing at all.

Two mistakes, stacked. `ActivateAsync` left the **outgoing** page visible for the entire rebuild, so
the placeholder was underneath a live page; then it showed the **incoming** control the instant its
renderer existed, which is a second or more before that renderer has anything to draw, so the
placeholder was underneath an unpainted one. WebView2 is a child HWND and paints over all WPF
content whatever its Z-order or opacity (`k.wpf-airspace`), so at no point in the sequence was the
image the shell had carefully prepared actually on the screen. The `Motion.Slow` cross-fade at the
end of it was animating a picture nobody could see.

It survived three commits because every part of it is individually correct and the whole thing is
invisible by construction: the only way to catch it is to photograph the screen during the
transition, which nothing before this commit did.

The fix is an ordering, not an effect. Take the pages off screen **first**, rebuild behind the
snapshot, and hold the new control back until its page has painted — then cut. Both frames are the
same page at the same scroll offset, so the cut is imperceptible; a cross-fade was never needed and
was never possible. `TabManager` grew a `RevealGate`, awaited **outside** the budget semaphore
because it waits for a real page load and holding that lock for a page load is what made picking a
tab from the grid hang in `0010`.

Measured after, over the same transition:

| | minimum pane luminance | frames that are neither page |
| --- | ---: | ---: |
| before | 18, 78 | 2, 1 |
| after | 229, 185, 232 | 0, 1, 0 |

The same change removed a second stale frame: activating a tab that had **never** been visited left
the previous page up for the whole load, because the rebuild path was gated on a snapshot existing.
It is now gated on the tab having no renderer, so a never-visited tab gets the app's own ground and
the loading bar. Showing nothing is better than showing a page the user has just left.

## Waiting for the wrong signal

The startup curtain was held until `NavigationCompleted`, which is also what the snapshot logic
uses. That is the right signal for a screenshot and the wrong one for "there is something to look
at": it waits for every subresource on the page. Against `google.com` it routinely fired **after**
the curtain's 2.6 s ceiling had already given up — the page had been readable for a second by then.

`DOMContentLoaded` is the honest signal for the second question, and `BrowserTab` now carries both.
Curtain time went from hitting the ceiling to **1.0–1.6 s**, ending when the page is actually there
(`k.navigation-completed-is-not-first-paint`).

The reveal gate picks between them for the same reason: with a snapshot on screen it waits for the
strict signal, so the live page **matches the picture it replaces**; with no snapshot there is
nothing to match, so it reveals as soon as the document exists, plus a longer beat — revealing on
the instant `DOMContentLoaded` fires showed the renderer's blank white page for a frame or two,
which on a dark browser is the most visible failure available.

## The bug that turned the whole page black

While building the travelling tab-strip marker, the retry path looked harmless:

```csharp
if (chip is null || chip.ActualWidth <= 0)
{
    QueueTabIndicator();   // not laid out yet; try again after layout
    return;
}
```

In browse it does what it says. In immersion the tab strip is not in the window at all — `0012`
lifts it into an `EdgeBar` window that stays hidden until the pointer reaches the top of the screen
— so its chips have **no size and never will**, and this retries forever. The queue runs at
`DispatcherPriority.Loaded`, which is inside the layout phase, so the loop kept that phase
permanently busy.

The symptom had nothing to do with the tab strip. **The web page went blank.** `HwndHost` shows the
child window it hosts during a settled layout pass, never got one, and the content area stayed empty
for the life of the process. Immersion failed 5 runs out of 5; browse always worked, because its
status bar rewrites itself every two seconds and that was enough to let a pass through
(`k.starving-the-layout-phase-blanks-webview2`).

Getting there took an hour of bisecting the wrong things — the browser switches, the fullscreen
call, the curtain, `Visibility.Hidden` versus `Collapsed`, collapsing the host instead of the
control — all of which were innocent, and one of which (a mode carve-out disabling the new reveal in
immersion) was very nearly shipped as a documented limitation. The lesson worth keeping is narrower
than "bound your retries": **a self-scheduling dispatcher callback that can never succeed does not
fail loudly, it starves whatever shares its priority**, and what shared it here was the mechanism
that puts the browser on screen.

Bounded to one retry: **10 immersion starts out of 10**, and both modes on the shipping build 3/3.

## The curtain

Startup used to be a grey rectangle for as long as it took to create a browser process and load a
page. There is now an opaque window carrying the Hearth mark over the whole app, from before the
environment exists until the first page has parsed, with the browser frame dropping in behind it as
it lifts (`d.startup-is-a-transition`).

It is a **separate top-level window**, for the third time in this codebase and the same reason: an
overlay inside the main window would be punched through by the first page that painted, in the
middle of the screen, which is exactly where the mark is (`d.airspace-needs-a-second-window`). The
grid's trick of collapsing the content host is not available, because a WebView2 cannot finish
initialising inside a collapsed panel and initialising is precisely what the curtain covers.

**A mode restart raises the same curtain on both sides of the process boundary**, which is the only
continuity available: nothing else survives, by construction. The successor is now launched
**before** the predecessor exits rather than in the statement after it, so its startup overlaps the
curtain animation instead of following it. Measured on one build, alternating the two orderings, the
window where neither process is on screen went from **1.15 s and 1.25 s** to **0.66 s and 0.75 s**.
The handover wait it depends on already existed, so overlapping costs nothing.

`AwaitHandover` also became asynchronous. It blocked the UI thread for as long as the old process
took to die, so the incoming window could not paint — a mode switch showed the unpainted white
rectangle Windows draws for an application that is not responding. Same wait, spent animating.

### Opaque, not dissolved — measured

The better-looking curtain fades the **whole window** out, so the chrome and the live page dissolve
into view together. It needs `AllowsTransparency`, and a per-pixel-alpha window in WPF is composited
in **software**: the entire window, every frame, on the UI thread.

Three cold starts each, one build, only the exit path varying:

| | ms/frame | fps | long frames | worst gap | CPU |
| --- | ---: | ---: | ---: | ---: | ---: |
| opaque | **8.0** | 124–125 | **0, 0, 0** | 9–16 ms | ~292 ms |
| dissolve | 16.2 | 56–67 | 0, 0, 1 | up to 114 ms | ~771 ms |

**Half the frame rate and 2.6× the CPU** for the same 1320×860 window, and software compositing
scales with pixel count, so a 4K display pays more (`k.layered-windows-are-software-drawn`). The
brief was that performance and smoothness come first, so the opaque curtain ships. The transparent
one stays reachable as `HEARTH_VEIL=dissolve`, because it is genuinely the nicer of the two and
someone with headroom may prefer it.

## One marker, travelling

The tab strip used to light up a background on the destination chip and fade one out on the source.
Two chips fading in opposite directions read as two unrelated events; browsers that move a single
indicator read as continuous because the eye tracks one object. There is now one border that slides
and resizes between chips (`d.one-marker-travels`).

Its **width** is animated, which is a layout property and a deliberate exception to
`d.animate-transforms-only`. The alternative — scaling a fixed-width border on X — turns its rounded
corners into ellipses over the length of the travel. The exception is safe only because the marker
lives in a `Canvas`, which reports no size of its own and gives children infinite space, so
re-measuring it cannot change anything above it.

`Sync` runs on every state change a tab reports and a busy page reports dozens, since each refused
subresource bumps the blocked count. Requests are coalesced onto one deferred pass and dropped
entirely when the geometry has not moved; without that the marker permanently eased toward a
position it was already at, which looks like a stutter and is one.

Chips also arrive and leave now — a short travel plus a fade, not Chrome's width expansion, which
re-measures the whole strip every frame and the strip is inside the window's caption. The exit lives
in the shell rather than in `TabManager`: the manager removes a tab synchronously and should keep
doing that, rather than having a presentation concern pushed into the class that owns renderer
lifetime.

## The immersion chip

It carried a permanent "Immersion" label, spending tab-strip width on a button pressed perhaps twice
a session. It is now a square glyph that expands leftward on hover to reveal the label, and on
keyboard focus too — a control whose meaning is only available to a pointer is one a keyboard user
has to guess at.

The slot is fixed at the **expanded** width with the button right-aligned inside it, so expansion
consumes space that was already reserved and no neighbouring control moves. The label sits in a
`Canvas` inside a clipped border, because given a zero-width constraint directly it wraps to one
character per line and changes height, which is the usual reason this effect looks broken. Both
widths come from the real text metrics at load rather than from a number that a font change would
falsify.

Square, because the rounded pill read as a status badge — a thing being reported rather than a thing
to press — and it was the only rounded control in a caption bar of square ones.

## The mark, as vectors

The curtain needs the mark at 118 px and growing, so it is drawn as geometry rather than scaled up
from a 256 px bitmap. The coordinates are the icon generator's, converted from fractions of the tile
to a 100-unit box, so `tools/make-icon.py` remains the source of the design. Vectors are also
animatable: the ember breathes while the browser starts, which is one `Opacity` on one small leaf —
deliberately the cheapest possible animation, because it runs during the busiest moment in the
process and its whole job is to say the application is alive rather than hung.

## The system animation switch

Windows has a global "show animations" setting, and a browser is exactly the kind of application
people turn it off for: weak hardware, remote desktop, or a vestibular disorder that makes a
full-screen zoom genuinely unpleasant. Every other browser honours it; Hearth now does, read once in
`Motion` so a transition added later cannot forget to (`d.motion-honours-the-system-switch`).

It covers everything that **moves** something across the screen and deliberately not pointer
feedback: a hover fill and a press dip are 70–130 ms, carry no travel, and removing them makes the
controls feel broken rather than calm. `HEARTH_MOTION=0/1` pins it either way for benchmarking.

## Also fixed, found on the way

`CapturePreviewAsync` against a **collapsed** controller does not fail — it never returns. It is
called while holding the budget semaphore, so the tab being switched to never activates and the
browser sits on an empty pane for good. Found by adding a blanking step to the grid handoff, which
collapsed the outgoing view a few lines before the capture and hung the switch every single time.
`k.capture-requires-live` was already documented; it is now enforced at the call site.

## What it costs

| Transition | delivered |
| --- | --- |
| curtain, fade out | 124–125 fps, 0 long frames, worst gap 9–16 ms |
| chrome reveal | 94–119 fps, 0–2 long frames over ~500 ms |
| grid entrance, 6 tabs | 57–86 fps, 1.0 long frames per open across six opens |
| card zoom to page | 69–79 fps, 0–1 long frames over 580 ms |

Startup is **1.0–1.6 s of curtain** on a Debug build against a cold browser process — time that was
always being spent and is now spent with something on screen.

## Not yet true

The page cannot participate in any of this. WebView2 ignores WPF opacity entirely, so the chrome
fades in around a page that simply appears; every transition here works by holding a still image or
an empty ground until the real thing is ready, never by blending the two.

Grid entrance is the weakest number in the table and the first open of a session is the outlier, as
snapshots decode from disk. Decoding them ahead of time would fix it and would spend memory on
pictures, which wants measuring before it is done.

The tab-strip marker does not animate when the strip scrolls, only when the selection or a chip's
width changes. Chip entrance is a translate rather than the width expansion browsers use, which is
the more convincing effect and the one that re-measures the caption bar every frame.

The restart still has a **~0.7 s window with neither process on screen**, floored by how long a
fresh WPF process takes to put a window up. Nothing short of not restarting removes it, and the
restart is required (`k.browser-args-fixed-at-creation`).
