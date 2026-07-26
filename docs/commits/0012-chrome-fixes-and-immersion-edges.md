# 0012 — A missing button, a smoother handoff, and chrome that comes to you

**Knowledge-graph nodes:** `k.non-ascii-literals-do-not-survive-tooling`,
`d.airspace-needs-a-second-window`, `d.snapshot-handoff-covers-the-load`, `cmp.edgebar`

## The maximise button was an empty string

Two controls had been invisible since `0008`: the maximise/restore caption button, and the immersion
button next to it.

```csharp
MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
ImmersionButton.Content = Immersive ? "" : "";
```

Those are not placeholders. They are what is actually in the file — literally empty strings.

`0007` recorded `k.powershell-utf8-corruption` after a PowerShell round-trip re-encoded this source,
and attached a rule: edit with the editing tools, never a shell round-trip. That rule was followed
here, and the characters were lost anyway, by a completely different mechanism. `0008` rewrote
`MainWindow.xaml.cs` wholesale, reproducing it from the file as displayed — and tools that display
source render private-use-area glyphs as nothing at all. They came back as `""`, the file compiled,
and the window shipped without a restore control.

So the rule was too narrow. It named one bad tool; the actual hazard is that **a non-ASCII literal
has to survive every tool that ever touches the file**, including reading it
(`k.non-ascii-literals-do-not-survive-tooling`). The fix is to stop relying on that:

```csharp
private const string GlyphMaximise = "";   // ChromeMaximize
private const string GlyphRestore  = "";   // ChromeRestore
private const string Dot           = "·";
```

An escape is pure ASCII on disk, so nothing in the chain can drop it, and it states which code point
is meant instead of depending on a glyph rendering somewhere.

Writing that rule down immediately caught the next instance. A sweep of the repository found 49 more
non-ASCII characters in C# — 46 em dashes and 2 arrows in comments, harmless, plus **one in a
user-visible string literal**: the ellipsis that truncates a tab title in the strip. That one is
exactly the case the rule exists for, and it is now `"…"`. The comment characters became `--`
and `->`.

**Every hand-written C# file in the repository now contains zero bytes above 0x7F**, which is
checkable in one line rather than trusted:

```python
sum(1 for b in open(path, "rb").read() if b > 127)   # 0, for every file under src/Hearth
```

That is the invariant recorded on the node. An invariant that cannot be checked is a hope, and this
one had already been violated twice without anybody noticing.

## The immersion button says what it does

It was an icon. Entering immersion **restarts the browser**
(`k.browser-args-fixed-at-creation`), and no glyph can warn anyone about that.

It is now a labelled pill — "Immersion", or "Exit immersion" when you are in it — with a tooltip
that names the restart outright: *"Restart Hearth into fullscreen immersion · F11"*. A mode change
that relaunches the process should never be a surprise.

## New tabs land on Google

`HomeUrl` was `example.com`, which is a developer's placeholder rather than a home page. The address
bar's search fallback is still DuckDuckGo — that is a separate choice and I have left it alone
rather than change something nobody asked about.

## The grid transition, made continuous

Three things were wrong, and only one of them was the animation curve.

**The curve.** 280 ms of `QuarticEase` `EaseIn` crawls out of the gate and then snaps. It is now
360 ms of `CubicEase` `EaseInOut`, which starts and lands softly.

**The gap.** The real problem was structural. `k.collapsed-host-blocks-initialisation` forces the
order *animate, then activate* — so the zoom finished, and then the page load began, with nothing on
screen in between. No easing curve fixes a hole.

The card now hands off to the placeholder (`d.snapshot-handoff-covers-the-load`). The zoom ends
**holding** the card at full size instead of fading it; the same tab's snapshot is put into the
placeholder underneath; the grid cross-fades away to reveal it. The picture that flew up to fill the
screen is still the picture on screen while the page loads behind it. An indeterminate accent bar
runs along the top while that happens — visible precisely because the placeholder is up, since once
the page paints, WebView2 covers all WPF content anyway.

The `Placeholder` also stopped letterboxing: it was `Uniform`/`DownOnly`/centred, so a snapshot
smaller than the pane sat in the middle with bars around it. It is now `UniformToFill`, top-aligned,
occupying the pane the way the page will.

**The guess.** The placeholder used to disappear on a flat 220 ms timer, which `0004` recorded as a
guess rather than a measurement. A guess is wrong in both directions: too short for a cold tab doing
a real network load, leaving a blank pane, and needlessly long for a warm one. It now waits for
`HasRendered` — the signal `NavigationCompleted` already sets — capped at five seconds so a page that
never loads cannot pin a screenshot over itself forever, then dissolves over 200 ms.

## Immersion chrome comes to the edge you reach for

Move the pointer to the top of the screen and the tab strip and address bar slide in; move it to the
bottom and the memory readout slides up. Move away and they go.

**This needed a second window**, and the reason is `k.wpf-airspace` again
(`d.airspace-needs-a-second-window`). WebView2 paints above every piece of WPF content regardless of
Z-order, so a toolbar drawn inside `MainWindow` is invisible the moment a page paints. `0006` hit the
same wall with the tab grid and solved it by collapsing the content host — an option available to a
full-screen surface and useless here, since the entire point is to show controls *without* hiding the
page.

The other option was to let the rows reflow and grow the layout. That resizes the WebView2 on every
reveal, which reflows the page underneath — unacceptable in the mode built around watching video.

`EdgeBar` is a borderless, non-activating, owned top-level window. It is a *sibling* HWND rather than
a child, so it composites above the WebView2, and the page below never changes size. It slides the
panel rather than the window, because moving the window would force Windows to redraw the live page
underneath every frame.

The controls are **not duplicated**. The real tab strip, navigation bar and status bar are lifted out
of the main window's layout and re-hosted in the bars, so there is one address bar, one shield, one
memory readout, and no second copy to drift.

Edge proximity is detected in **page script**, for the same reason the swipe gesture is
(`k.mouse-input-never-reaches-wpf`): the pointer is over a window the shell does not own, so WPF sees
no mouse position at all. The listener posts only on transitions, so dragging across a page costs one
comparison per event.

Two details that stop it being annoying: the bar is `ShowActivated = false`, because an activating
window would pull focus out of the page *and* drop the owner out of the shell's fullscreen treatment,
bringing the taskbar back (`k.maximised-is-not-fullscreen`); and hovering the bar keeps it open,
because once the pointer is over it the page stops receiving mouse events entirely and can no longer
report anything.

## Verified

Browse: Google loads, all three caption buttons render, and the labelled Immersion pill is present.
Immersion: top edge reveals the strip and address bar **over** the page with no reflow, bottom edge
reveals `635 MB · 1 awake`, and the trace shows the full cycle `edge-top → edge-none → edge-bottom`.
Grid: clicking a card still lands correctly, with the address bar, tab highlight and status bar all
in agreement.

## Not yet true

The edge bars have no keyboard route — they appear on pointer proximity only. There is no way to pin
them open. `EdgeBar` follows the owner's bounds only at reveal time, which is fine in immersion where
the window cannot move, and would need a `LocationChanged` hook to be reused anywhere else.
