# 0009 — Immersion mode, and the restart that makes it real

**Knowledge-graph nodes:** `k.browser-args-fixed-at-creation`, `k.maximised-is-not-fullscreen`,
`k.mouse-input-never-reaches-wpf`, `d.mode-switch-restarts`, `d.session-carries-tab-ids`,
`d.immersion-evicts-by-recency`, `cmp.modeprofile`, `cmp.sessionstore`, `cmp.gesturescript`,
`m.immersion-cost`

Browse is lean by construction. Immersion is the opposite pole and is entered deliberately: the
screen belongs to the page, the last few tabs stay warm, filtering is off, and Chromium is started
with switches that trade memory for smoothness.

## Why it restarts the process

This is the interesting part, and it is not a shortcut.

`AdditionalBrowserArguments` is read once, when `CoreWebView2Environment.CreateAsync` starts the
browser process, and never again. Worse, WebView2 refuses to create a second environment over the
same user-data folder with *different* options while the first browser process is alive. So the
flags that make immersion actually faster — GPU rasterisation, zero-copy, and above all the
`CalculateNativeWinOcclusion` disable that stops Chromium throttling a window it wrongly believes
is covered — **cannot be applied to a running process at all** (`k.browser-args-fixed-at-creation`).

The alternative was a mode that claims to boost performance while changing nothing that matters.
Restarting is the honest shape here, and the user's own phrasing for the feature — *reboot into
immersion* — is what the platform actually requires.

The restart cost is paid down by `SessionStore`, which had to exist anyway. A browser that loses
your tabs when you change a setting is one nobody changes the setting on.

Verified end to end — PID changes, and the new environment reports what it was given:

```
19:43:24.597  restarting into Immersion
19:43:24.620  key page    None+F11 -> ToggleImmersion handled=True
19:43:25.810  environment: mode=Immersion budget=5 filter=False args=[--disable-features=CalculateNativeWinOcclusion
              --disable-backgrounding-occluded-windows --disable-renderer-backgrounding
              --disable-background-timer-throttling --autoplay-policy=no-user-gesture-required
              --enable-gpu-rasterization --enable-zero-copy --ignore-gpu-blocklist]
```

and the flags are present in the browser process's own command line, not merely in ours.

The outgoing process must actually exit before the new one can create its environment — the same
constraint, from the other side. The new instance is passed `--handover=<pid>` and waits for it.

## Session persistence, and one detail that matters

`store/session.json` carries **tab ids**, not just URLs. Snapshots live at `store/shots/{id}.png`,
so carrying the id across a restart means a restored tab finds its own last frame already on disk
and the grid comes back populated instead of blank. Restoring with fresh ids would orphan every
screenshot the previous run captured — still on disk, permanently unreachable
(`d.session-carries-tab-ids`).

Restored tabs come back **cold**. Reopening 40 tabs has to cost 40 URLs and not 40 renderers, or
restore would violate the one thesis this project has.

What comes back is a URL and a title. Not history, not form state, and *not scroll* — the offset
was captured in a process that has exited and was never persisted. `SnapshotStore.Adopt` therefore
takes the image and deliberately leaves the offset at zero. Saying so plainly matters more than the
feature does: `p.lossy-restore` is a problem this project exists to take seriously, and quietly
over-promising restore is exactly how the tools it criticises lost people's trust.

Session save also runs on ordinary quit, so a normal restart restores like a mode switch does. The
file is consumed and deleted immediately on load — if restoring is what crashes the browser, a
session left on disk would reopen the same tabs forever.

## Maximised is not fullscreen

Immersion cost three attempts at the same bug, and only measurement ended it.

The window was set to `WindowState.Maximized` with `WM_GETMINMAXINFO` answered using the full
monitor bounds instead of the work area. `GetWindowRect` confirmed **1920x1080 at 0,0**. Walking the
child windows confirmed every layer agreed:

```
TOP-LEVEL: 1920x1080 at 0,0
  Static                        1920x1080 at 0,0
  Chrome_WidgetWin_0            1920x1080 at 0,0
  Chrome_WidgetWin_1            1920x1080 at 0,0
  Chrome_RenderWidgetHostHWND   1920x1080 at 0,0
  Intermediate D3D Window       1920x1080 at 0,0
```

WPF agreed too: `window=1920x1080 content=1920x1080 view=1920x1080`. And the taskbar was still drawn
on top of it, with a 48-pixel strip of desktop showing through.

The app was right the whole time and the shell was declining to yield. To Windows, a *maximised*
window is by definition one that respects the work area, so it is never a candidate for the
fullscreen treatment that hides the taskbar — no matter what size it actually is
(`k.maximised-is-not-fullscreen`). A real fullscreen window is `WindowState.Normal`, topmost, sized
explicitly to the monitor. `MaximiseFix.Fullscreen` does that, converting monitor pixels to DIPs,
because skipping that conversion looks correct at 100% scaling and leaves a strip of desktop on
every other machine.

Two wrong fixes preceded it — reordering the maximise calls, then suspecting the screenshot tool.
The thing that settled it was enumerating the child HWNDs rather than reasoning about them.

`Topmost` is what makes the shell yield, but a topmost window that stays in front after you alt-tab
away is a trap, so it is released on deactivate and reclaimed on activate.

## Gestures, and why they are injected

Hold **both mouse buttons and swipe** to change tabs; the thumb buttons go back and forward.

Mouse input over web content never reaches WPF, for the same reason keyboard input does not
(`k.two-keyboard-paths`): the window under the cursor belongs to `msedgewebview2.exe`. Unlike the
keyboard there is **no `AcceleratorKeyPressed` equivalent for the mouse**
(`k.mouse-input-never-reaches-wpf`), so the only place the gesture can be recognised is inside the
page. `GestureScript` is injected with `AddScriptToExecuteOnDocumentCreatedAsync` and posts through
`window.chrome.webview`.

Both buttons down is `e.buttons & 3 === 3` — a chord no page uses, which is why it can be claimed
without breaking sites. The right-button release is suppressed only for a completed swipe, so an
ordinary right-click still opens the context menu.

Verified with real OS mouse input:

```
19:51:10.933  gesture tab-next     (swipe left)
19:51:16.450  gesture tab-prev     (swipe right)
```

**Known limit, and a real one:** injection cannot work where page script cannot run — the PDF
viewer, error pages, downloads, view-source. Every gesture therefore has a keyboard equivalent,
which goes down the native path instead.

## Eviction changes shape

Immersion evicts by **pure recency**; browse keeps the habit weighting from `0003`.

"The last few in the chain" is a different question from "what is worth keeping". Habit weighting is
right for a working session — a reference doc opened forty times should outrank something opened
once — but in a lean-back session it keeps the dashboard you check every morning alive instead of
the thing you were watching two tabs ago (`d.immersion-evicts-by-recency`).

Leaving the grid in immersion also does **not** hibernate everything else, which is what browse does.
The premise of the mode is that stepping between recent tabs is instant; evicting them every time
the grid closes would make the grid the most expensive thing in it.

## What immersion costs

Six real sites, every tab activated so the budget binds, one build, 95 s settle:

| Mode | Renderers | Total |
| --- | ---: | ---: |
| browse | 3 | 890 MB |
| immersion | 20 | **2197 MB** |

**Immersion costs about 2.5x browse.** That is the whole point of it being a mode you enter rather
than a default, and the number belongs next to the feature rather than in a footnote. The memory
thesis is not that memory does not matter — it is that you should be the one deciding when to spend
it, and be able to see what you spent.

## Not yet true

The grid is still `0007`'s: it forces the window to maximise and needs a double-click to open a tab.
That is `0010`. Immersion has no on-screen way back other than `F11` — no auto-revealing chrome on
mouse-to-top-edge. Gestures are horizontal only; there is no vertical swipe for the grid. Restored
tabs lose scroll position, as described above.
