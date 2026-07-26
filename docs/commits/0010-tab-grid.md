# 0010 — The tab grid, actually working

**Knowledge-graph nodes:** `k.collapsed-host-blocks-initialisation`, `d.grid-inherits-the-mode`,
`d.single-click-opens`, `d.zoom-connects-card-to-page`

The grid has existed since `0006` and has never really worked. This commit finds out why.

## Picking a tab hung the browser

The headline bug, and it was not a UI problem at all.

`EnsureCoreWebView2Async` **never returns while its control sits inside a collapsed panel**. It waits
for a realised HWND, and a collapsed WPF element does not have one
(`k.collapsed-host-blocks-initialisation`).

The grid has to collapse `ContentHost` — WebView2 paints over all WPF content regardless of Z-order,
so the host must leave the screen rather than sit behind the overlay (`k.wpf-airspace`). Opening a
tab then called `ActivateAsync` **while the host was still collapsed**. For a cold tab that means
building a renderer, so the call hung — holding the budget semaphore the whole time, which wedged
every later activation too.

Why it looked intermittent rather than broken: Big Picture pins the live budget to **1**, so nearly
every card in it is a cold tab. Picking one of the rare tabs that still held a controller took the
early-return path in `RealiseAsync` and worked perfectly. The failure rate was therefore a function
of which card you clicked, which is the worst possible shape for a bug.

The trace made it obvious in one run:

```
20:07:49.317  grid: zoom start
20:07:49.710  grid: zoom done, awaiting activation
                                                    <- nothing, ever
```

The fix is an ordering constraint, now named and documented in `RestoreShell`: **the shell is
restored before any tab is activated.** `LeaveGridAsync` takes the tab to open and activates it
itself, after uncollapsing the host and forcing a layout pass, so there is one place where the
ordering lives and a comment saying why it cannot move.

This also explains the symptom from the other side. Before the fix, leaving the grid left the
address bar and status bar missing and the tab strip highlighting the wrong tab — because
`LeaveGridAsync` never reached them, and `Sync()` never ran.

## The grid takes the shape the app is in

It no longer maximises the window. That was the other half of "not working properly": opening the
grid resized the browser, and leaving it did not reliably put the window back.

The markup now spans rows 1–3 rather than the whole window and never touches `WindowState`. That one
detail makes it mode-aware for free (`d.grid-inherits-the-mode`):

- in **browse**, the tab strip stays visible above it — it doubles as the title bar, so collapsing it
  would take the window controls with it — and the grid fills the rest of the window at whatever size
  the window happens to be;
- in **immersion**, the strip is already collapsed, so the same markup fills the screen.

One layout, two correct results, and no mode check in the XAML.

The subtitle does differ, because the guarantee differs: *"one stays awake while you're here"* in
browse, *"the last few stay awake"* in immersion.

## One click opens a tab

It used to need a double-click. That is the single biggest reason the grid read as broken — a click
selected a card and appeared to do nothing. Nothing else in this app, and no tab switcher anyone has
used, needs two clicks to pick a thing.

Keyboard picked up the same directness: **1–9 jump straight to a card**. In a wall of pictures the
position *is* the identity, so a number is the most direct thing a keyboard can offer. Arrows still
move, Enter and Space still open, Escape still leaves.

## Opening zooms the card into the page

Picking a card flies it up to fill the viewport, so the page you land on is visibly the card you
chose rather than an unrelated cut.

The card itself cannot be animated in place: it lives inside the `ListBox`'s own `ScrollViewer`,
which clips to its bounds, so it would be sliced off exactly as the movement got interesting. A
`VisualBrush` copy is painted onto a `Canvas` spanning every row, and that ghost is what moves
(`d.zoom-connects-card-to-page`). It scales to **cover** rather than fit — a letterboxed intermediate
frame would give away that this is a thumbnail and not the page — and the rest of the wall fades
underneath it.

If the container has been virtualised away or laid out at zero size, it falls back to a plain fade.
A decorative animation must never be able to block the actual open.

Cards also now enter staggered rather than as one block, capped at a dozen: past that a per-item
delay stops reading as choreography and starts reading as the application being slow. This was
listed as "not yet true" in `0007`.

## A quieter fix

The `ListBox` used to sit inside an outer `ScrollViewer`, which handed it infinite height. It
therefore never virtualised, and `ScrollIntoView` had nothing to scroll — so keyboard selection
walked silently off the bottom of the screen on any window that could not show every tab at once.
The `ListBox` now scrolls itself.

## Verified

Browse: window keeps its 1320×860, tab strip stays visible, five cards with real screenshots, and
clicking one lands on that page with the address bar reading `https://example.com/`, the strip
highlighting the right tab, and the status bar back at `520 MB · 1 awake · 2 resting`.

Immersion: the same grid fills 1920×1080 with no chrome and no taskbar, subtitle
`4 open · the last few stay awake`.

## Not yet true

No search in the grid, and no gamepad input. Cards cannot be closed from the grid or reordered by
dragging. The zoom animation reads well for a card whose snapshot resembles the page it is about to
show; for a tab that has never rendered, it flies a host-name placeholder up instead, which is
honest but not beautiful.
