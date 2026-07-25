# 0008 — Shortcuts that fire, and lean by default

**Knowledge-graph nodes:** `k.two-keyboard-paths`, `k.wpf-wrapper-hides-controller`,
`k.double-dispatch-on-page-keys`, `k.source-is-stale-during-navigation`, `d.lean-is-the-default`,
`d.filtering-needs-an-escape-hatch`, `d.engine-labels-over-inference`, `cmp.shortcutrouter`,
`cmp.siterules`, `cmp.diag`, `m.filtering-reclaim`

Two changes that turn out to be the same change: stop treating the lean configuration as a mode the
user opts into, and make the browser respond to the keyboard like a browser.

## Why no keyboard shortcut worked

Up to `0007` every shortcut lived in `Window.PreviewKeyDown`. That event almost never fires.

WebView2 is out-of-process. The HWND that actually holds keyboard focus is created by
`msedgewebview2.exe`, so its `WM_KEYDOWN` messages are queued to **that process's** thread. Our
message loop never sees them — which also rules out `ComponentDispatcher` and every other
thread-level message filter, since there is no message on our thread to filter. The moment focus
lands on a page, which is the entire time anyone is actually browsing, WPF is deaf.

F11 appeared to work during development for the worst possible reason: after startup focus sits on
the address bar, so the chrome path handles it. Every test that began by pressing a key without
first clicking into the page tested the one case that already worked.

The runtime's answer is `CoreWebView2Controller.AcceleratorKeyPressed`, which forwards key presses
back to the embedder. The WPF wrapper does not expose the controller — `WebView2` holds a private
`m_webview2Base`, and `WebView2Base.CoreWebView2Controller` is `internal`
(`k.wpf-wrapper-hides-controller`). It is reached by reflection, cached once per process, and
fail-soft: if the lookup ever breaks, shortcuts degrade to chrome-only and the failure is logged
rather than thrown.

So there are two delivery paths and one map. `ShortcutRouter.Map` is static and pure precisely so
the two cannot drift; a shortcut that works in the address bar but not on a page is worse than one
that works nowhere, because it teaches the wrong model.

### The bug that only appears once both paths work

With the native hook attached, **one `Ctrl+T` opened two tabs.** Both paths fire for the same
keystroke: the wrapper re-raises the key into the WPF tree *and* the controller reports it
natively, 4 ms apart.

```
19:19:27.126  key chrome  Control+T -> NewTab handled=True
19:19:27.127  key page    Control+T -> NewTab handled=True
```

The obvious discriminators both lie. With a page focused, `IsKeyboardFocusWithin` reports `False`
and `Keyboard.FocusedElement` is `null` — neither is a bug, WPF genuinely does not have focus,
which is exactly why neither can be used to detect that it does not. `e.OriginalSource` is the
`WebView2` and is the only reliable signal (`k.double-dispatch-on-page-keys`).

### Verified, not assumed

`HEARTH_TRACE=1` writes each dispatch to `store/trace.log` with the path that carried it. Keys were
driven through the real OS input stack (`SendInput`, after `AttachThreadInput` to lift the
foreground restriction) with focus clicked into the page first:

```
key page    Control+T          -> NewTab           handled=True
key chrome  Control+Tab        -> NextTab          handled=True
key chrome  Control+D1         -> SelectTab        handled=True
key chrome  Control+D9         -> SelectLastTab    handled=True
key chrome  Control, Shift+Tab -> PreviousTab      handled=True
key chrome  None+F9            -> ToggleGrid       handled=True
key chrome  None+Escape        -> Dismiss          handled=True
key chrome  Control+Add        -> ZoomIn           handled=True
key chrome  Control+D0         -> ZoomReset        handled=True
key chrome  Control+W          -> CloseTab         handled=True
key chrome  Control, Shift+T   -> ReopenClosedTab  handled=True
key chrome  Alt+Left           -> Back             handled=True
```

Exactly one dispatch per key. The first goes through the page path because focus was in the page;
everything after goes through chrome because `Ctrl+T` moves focus to the address bar. That is the
correct handoff, and seeing it in the log is the only way to know it happened.

`Alt+Left` deserves a note: WPF reports every Alt combination as `Key.System` and hides the real key
in `SystemKey`, so the back shortcut had been matching nothing at all.

## Lean is the default now

The low-power toggle is gone. Browsing runs at the lean setting — budget 3, content filtering on —
and there is no pill in the chrome announcing a mode, because it is not a mode.

`0005` deliberately made this a mode, and that reasoning was sound at the time: blocking
cross-origin frames removes OAuth logins, payment frames, CAPTCHAs and embedded players
(`k.frame-blocking-breaks-pages`), and shipping that silently produces a browser that cannot log
into anything. The user's conclusion would be that the browser is broken, which would be the
correct conclusion.

What changed is not the risk but the recourse. `0005` itself recorded the way out, under `future`:
a per-site allowlist. That is `SiteRules`, and it is what makes a default defensible:

- the tab counts what was refused;
- the shield appears in the address bar **only when something was actually refused** — a badge
  that is always on is wallpaper, one that appears exactly when a login button stops responding is
  an explanation;
- one click exempts the host permanently and reloads.

Measured on `stackoverflow.com`, one tab, same build, 90 s settle, only `HEARTH_BLOCK_FRAMES`
varying:

| Configuration | Renderers | Total | |
| --- | ---: | ---: | ---: |
| filtering on (default) | **1** | **719 MB** | **−55%** |
| filtering off *(control)* | 14 | 1599 MB | — |

Clicking the shield and reloading takes it straight back to **14 renderers**, and the status bar
reads **1.6 GB**. That number is the feature. The whole product argument is that this cost is
normally invisible; unlocking a site should show you exactly what you bought.

*(Debug build, and the total includes Hearth's own WPF process, so these absolute figures are not
comparable with `0005`'s. The control run is what the claim rests on.)*

## A bug that making it the default immediately exposed

The first build rendered a blank page on every tab. The filter was blocking **the top-level
navigation itself**.

The origin test compared each request's host against `core.Source`. When the very first document
request goes out, `core.Source` is still `about:blank`, so "differs from the current page" is
trivially true and the page's own navigation is refused. This had been live since `0005` and was
completely invisible, because filtering could only ever be switched on *mid-session* — by which
time a page had committed and `core.Source` was real (`k.source-is-stale-during-navigation`).

The fix is to stop inferring what a request is and ask the engine
(`d.engine-labels-over-inference`). Chromium labels every request it makes: `Sec-Fetch-Dest` says
what the request is for, `Sec-Fetch-Site` says how far it reaches. A top-level navigation is
`dest=document` and is never refused; a cross-origin subframe is `dest=iframe` with
`site=cross-site`. Neither depends on any state we have to keep correct.

That rewrite also narrowed media blocking to **cross-site only**. Same-origin media costs no extra
renderer — `0005`'s control run proved the win came from frames — so refusing a page's own audio
player broke sites in exchange for nothing.

## Also in this commit

- **Closed tabs come back** (`Ctrl+Shift+T`), bounded to 25. Only URL and title return: the
  renderer was destroyed and its snapshot deliberately deleted with it, so this is a fresh load and
  is not described as a restore.
- **Closing a tab selects its neighbour**, not the last tab in the strip.
- **Zoom** on Chrome's ladder, with the factor shown in the status bar when it is not 100%.
- **Hard reload** goes through `Page.reload` with `ignoreCache`, because `CoreWebView2.Reload()`
  honours the cache and that is the one thing the shortcut exists to defeat.
- `HEARTH_LOW_POWER` and `HEARTH_LOW_POWER_BUDGET` are gone. `HEARTH_BLOCK_FRAMES=0` remains,
  because it is the control run for every claim above.

## Not yet true

The grid still forces the window to maximise and still needs a double-click to open a tab; that is
`0010`. There is no find-in-page. `Ctrl+Shift+T` does not restore scroll position even though the
snapshot machinery could support it. The shield is per-host and permanent — there is no
"just this once", and no UI to review or revoke a grant beyond deleting `store/site-rules.json`.
