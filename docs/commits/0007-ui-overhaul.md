# 0007 — UI overhaul

**Knowledge-graph nodes:** `d.tokenised-theming`, `k.custom-chrome-maximise`, `k.powershell-utf8-corruption`, `d.bigpicture-exit-hibernates`, `cmp.thememanager`, `cmp.maximisefix`

The chrome up to `0006` was developer telemetry with a native title bar bolted on top. This
replaces it.

## Why one commit and not two

Chrome, theming and the Big Picture rework all rewrite the same two files. Splitting them would
produce an intermediate commit whose code-behind referenced XAML that did not exist yet — a commit
that does not build is worse than a large one.

## The tab strip is now the title bar

`WindowStyle="None"` plus `WindowChrome` removes the native caption entirely. The tab strip
occupies that space, with minimise/maximise/close at its right edge, the way every modern browser
does it. `CaptionHeight="42"` makes the strip draggable; anything interactive inside it carries
`WindowChrome.IsHitTestVisibleInChrome`, which the shared styles set.

**This is not free**, and the first build proved it. A `WindowStyle="None"` window maximises to
full *monitor* bounds rather than the *work area*, so it covered the taskbar — and the bottom row
of the layout went with it. The row it hid was the memory readout: the one element that exists
specifically to make cost visible.

Windows asks about this exactly once, through `WM_GETMINMAXINFO`, so the right size has to be
supplied in the answer rather than corrected afterwards. `MaximiseFix` hooks it and resolves the
monitor per-window rather than assuming the primary, so a taskbar on a secondary display still
behaves.

## Theming is tokenised, not themed

`Themes/Light.xaml` and `Themes/Dark.xaml` expose an identical key set and are swapped in place at
slot 0 of the merged dictionaries. Nothing in the app names a colour; everything resolves through
`DynamicResource`, so a swap repaints the whole window without rebuilding a single visual tree.
`ThemeManager` follows Windows via `AppsUseLightTheme` and watches `UserPreferenceChanged` — but
only while the preference is *System*, because an explicit choice should not be overridden by the
OS.

Two things that are easy to get wrong and are now invariants:

- **Both dictionaries must expose the same keys.** A key in one and missing from the other resolves
  to nothing after a swap and paints transparent.
- **Converters must resolve brushes per call.** `StateToBrushConverter` used to cache frozen
  brushes; cached brushes survive a theme swap and leave the UI half-applied.

The neutrals are Apple's system greys, as requested. The accent does **not** simply carry over:
ember `#E8833A` on white fails contrast and reads washed out, so light uses `#C25E14`. A light
theme is not an inverted dark theme.

## Motion

Hover discs scale from 0.7 rather than appearing. Tab selection cross-fades. The low-power pill
animates its fill, because engaging a mode that silently changes how pages load should be
unmistakable. Big Picture fades in while scaling from 0.965, which reads as a surface arriving
where a plain fade reads as a dialog appearing.

WPF has no letter-spacing, so the Apple feel comes from weight and scale instead of tracking:
`Segoe UI Variable Display` at Light weight for the large heading, a wide jump down to secondary
text, and considerably more air than before.

## Big Picture actually goes fullscreen

Previously it was an overlay inside a normal window. Now the chrome rows collapse and the window
maximises, so the wall owns the screen. Cards gained rounded corners, drop shadows, and a lift on
hover — the Android-recents gesture rather than a colour change.

### Leaving it now hibernates everything else

Exiting calls `HibernateAllButActiveAsync`, which evicts every tab except the one just chosen,
regardless of budget headroom. Leaving the wall is the clearest signal available that the user
surveyed everything and picked one thing. Waiting for a later activation to push tabs past the
ceiling would be *reactive* eviction — exactly the behaviour `c.hibernate-by-default` exists to
reject.

## A bug I caused, shipped, and then made worse

Fixing the maximise glyph, I edited `MainWindow.xaml.cs` with a PowerShell read-modify-write.
Windows PowerShell 5.1's `Get-Content` decodes BOM-less UTF-8 as ANSI, so the round-trip
re-encoded every non-ASCII character in the file. It reached the screen: the Big Picture subtitle
rendered as **"6 open Â· one stays awake while you're here"**.

Then I made it worse. A blanket Latin-1 → UTF-8 repair assumes the *whole* file is doubly encoded.
This one wasn't — it held a mix, and the box-drawing characters in the comment separators degraded
into replacement characters. The fix was to rewrite the file with the editor tool.

Recorded as `k.powershell-utf8-corruption` with a rule attached: edit source with the file-editing
tools, never a PowerShell round-trip; where a C# file must carry punctuation, define it as a named
constant so there is one place to check; keep comment separators ASCII.

## Verified

Captured in both themes via `PrintWindow` against the window handle. `SetForegroundWindow` from a
background process is refused by Windows — the first attempt screenshotted VS Code — so the
capture now targets the HWND directly with `PW_RENDERFULLCONTENT`, which also picks up
DirectComposition-backed WebView2 content.

Light and dark both render correctly in browse and Big Picture, and the status bar survives
maximise: `767 MB · 3 awake · 3 resting`.

## Not yet true

Big Picture still has no search, and no gamepad input despite the name. There is no settings UI —
theme cycles through a toolbar button and everything else is environment variables. Card entry is
not staggered; the grid fades as one block rather than cascading.
