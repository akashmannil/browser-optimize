# 0006 — Big Picture mode

**Knowledge-graph nodes:** `d.bigpicture-is-budget-one`, `k.wpf-airspace`, `k.capture-needs-first-paint`, `cmp.snapshotconverter`

## What landed

A full-screen, keyboard-driven wall of every tab, built entirely from the screenshots hibernation
was already producing. `F11` toggles, arrows move, `Enter` opens, `Esc` goes back.

## Why this mode is worth having

It would be easy to dismiss as a skin. It isn't, because **the aesthetic and the memory
architecture want exactly the same thing.**

Steam Big Picture shows one game at a time because you are across the room with a controller.
Hearth shows one *page* at a time, and everything else as a picture — which is precisely the state
the memory model already prefers. So entering Big Picture pins `EffectiveBudget` to **1**. Browsing
from the wall is the cheapest state the browser has.

The snapshots that make tabs cheap are the same artifacts that make them findable. The overview
costs nothing extra because eviction already paid for it. That is also the answer to
`p.archive-blackhole`: a wall of page images is something people actually revisit, where a list of
titles is not.

## The bug that made the overlay invisible

The overlay was built correctly, given `Panel.ZIndex="50"`, and rendered **completely invisible**
behind the live page. The screenshot showed the "All tabs" heading and then go.dev painted over the
entire wall.

This is **WPF airspace**. The WPF `WebView2` is a windowed HWND control hosted through `HwndHost`,
and Windows composites it above the WPF render surface. Z-order, adorners and overlays have no
effect against it. There is no stacking fix.

The correct move is to **collapse the WebView2 host** when the wall is up — and that turns out not
to be a workaround at all. Big Picture shows pictures; no live page needs to be on screen. The
constraint and the design agree.

Worth noting as a cost of `d.dotnet-wpf` that the commit `0001` note did not anticipate.

## The bug that made thumbnails blank

Two cards came back empty. `CapturePreviewAsync` **succeeds** on a realised-but-unpainted
`WebView2` and hands back a blank frame — and `ActivateAsync` returns once the renderer exists, not
once the document has loaded.

A blank thumbnail is worse than no thumbnail: it reads as a broken page rather than an unvisited
one. Capture is now gated on `BrowserTab.HasRendered`, set from a successful `NavigationCompleted`
and cleared on teardown. A refused capture also correctly withholds permission to tear the tab
down, since there would be nothing to repaint from.

### It also invalidated the benchmark

With the guard in place, *every* thumbnail went blank — because `HEARTH_ACTIVATE_ALL` switched tabs
faster than any page could load. The guard was right; the harness was wrong. It now dwells until
each tab has painted before advancing.

**A harness that switches tabs instantly is measuring a workload nobody has.** This is the third
measurement error in this project (after `0002`'s live-vs-open confusion and `0003`'s unsettled
sampling), and all three shared a shape: the code was fine, the thing being measured wasn't what
was claimed.

## Details worth keeping

- **Snapshot paths carry a `?t=ticks` suffix.** The file path is stable, so a re-capture would
  never invalidate a binding and the wall would show stale images forever.
- **Thumbnails decode at 480 px wide.** Decoding full-resolution page captures for dozens of cards
  would spend more memory on the tab overview than on the tabs — an absurd way to lose this
  particular argument.
- **A `ListBox` with a `WrapPanel`**, not a hand-rolled grid: it brings keyboard navigation and
  selection for free, which is most of what a lean-back mode needs.
- **Cards fall back to the host name** when no snapshot exists, so an unvisited tab reads as
  unvisited rather than broken.

## Verified

Six tabs, all activated, `F11` sent programmatically and the screen captured. All six render real
thumbnails — Wikipedia's globe, Hacker News's story list, MDN, Python.org, go.dev selected with the
accent border. Memory readout showed 507 MB.

## Not yet true

Big Picture has **no search**. At six tabs a wall is enough; at two hundred it is a different
scrolling problem, and `Ctrl+K` over titles and page text is the real answer.

Gamepad input is not wired, despite the mode being named after a controller interface. Arrow keys
only.

There is also no re-capture of stale thumbnails: a tab captured days ago shows a days-old picture
with nothing indicating its age.

## Next

The obvious candidates are search over the wall, and the refindability scoring (`c.refindability`)
that would let eviction distinguish a homepage from a half-filled form.
