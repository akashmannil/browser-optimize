# 0002 — Tab model

**Knowledge-graph nodes:** `cmp.tabmanager`, `cmp.browsertab`, `cmp.tabstate`, `k.site-isolation-multiplies-renderers`, `api.additional-browser-arguments`

## What landed

A real tab abstraction, a tab strip, and environment ownership moved out of `MainWindow` into
`TabManager`. Plus the first honest memory measurements — which turned up something that changes
the plan for commit `0003`.

## Why this shape

### A tab is not a WebView2 with metadata attached

`BrowserTab` holds URL, title, and activation history. Its `View` property — the actual WebView2 —
is **nullable, and null is the expected steady state**, not an error.

This inversion is the whole model in miniature. If a tab *were* a renderer wrapper, then evicting
one would mean destroying the tab, and every eviction path would need to defend against
use-after-free. Because identity lives in the tab and the renderer is a cache, eviction is just
`View = null` and the rest of the system keeps working.

`TabState` is ordered so higher means cheaper to hold and slower to restore. Eviction always moves
a tab to a higher value, which makes the budget logic in `0003` a comparison rather than a
special case.

### Tabs open Cold

`Open()` adds a tab and returns without creating a renderer. Opening 200 tabs costs 200 URLs. This
is the smallest possible version of the default-state inversion, and it is already load-bearing:
passing five startup URLs realises only the one that gets activated.

### Environment ownership moved to TabManager

Commit `0001` predicted this: an explicit environment makes startup asynchronous, and at N tabs
that becomes a sequencing problem. `TabManager` holds the `Task<CoreWebView2Environment>` rather
than the result, so every tab awaits the *same* initialisation instead of racing to create its own.

`RealiseAsync` is now the only method in the codebase that calls `EnsureCoreWebView2Async`. That
funnel is deliberate — it is the enforcement point for `d.shared-environment`.

### Startup URLs from the command line

Ordinary browser behaviour, but the real motive is measurement: memory runs at a given tab count
have to be reproducible without hand-clicking the tab strip.

## Measured

Filtering `msedgewebview2.exe` processes by Hearth's own `user-data-dir`, so other WebView2 apps
on the machine don't pollute the numbers:

| Tabs | Browser processes | Renderers | Total working set |
| ---: | ---: | ---: | ---: |
| 1 | 1 | 1 | 304.5 MB |
| 5 | **1** | **14** | 1170.3 MB |

**The good news:** browser process count stayed at **1** across five tabs. `d.shared-environment`
is verified, not just asserted.

**The bad news, and it's significant:** fourteen renderers for five tabs.

## The finding that changes commit 0003

WebView2 inherits Chromium's **site isolation**, which allocates a renderer per *site instance* —
including cross-origin iframes. Real pages (github, stackoverflow, HN) embed widgets, auth frames
and CDNs from other origins, so each tab fans out into several renderers.

Consequences:

1. A shared environment guarantees one **browser** process. It does **not** give one renderer per
   tab. Those are different claims and only the first was verified.
2. At ~234 MB/tab, naive extrapolation to 100 tabs exceeds **20 GB** — currently *worse* than
   Chrome. The architecture note's 40–80 MB per-tab figure describes a renderer, not a tab, and a
   tab is not one renderer.
3. **Capping live tabs does not cap renderers.** The budget in `0003` was designed around tab
   count. It needs to ration renderers, or an ad-heavy page will blow the ceiling on its own.

The lever is `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments`, which passes raw
Chromium switches at environment creation — specifically `--renderer-process-limit=N`, forcing
site instances to multiplex onto a capped renderer pool. This is essentially Firefox's
`dom.ipc.processCount`, and it's the only embedder-side process-model control that exists.

It is not free: capping renderers weakens site isolation, which is the mitigation for
Spectre-class cross-origin attacks, and makes a single renderer crash take down more tabs. That
trade-off is recorded on the constraint node and needs a decision in `0003`, not a silent default.

## What this costs

`TabManager` takes the content `Panel` in its constructor, coupling it to WPF. That's deliberate:
in WPF a renderer's lifetime *is* its visual-tree lifetime, so pretending otherwise would mean an
abstraction that lies. If a headless test harness is needed later, the seam is `RealiseAsync`.

## Not yet true

Still no budget and no eviction — every activated tab stays `Live` forever. `TabState.Warm`,
`Hibernated` and `Cold` are defined but only `Live` and `Cold` are reachable, and `Cold` only via
close. The numbers above are the baseline to beat, not a result.

## Next

`0003` introduces the live budget with LRU eviction, and must now also decide whether to cap the
renderer pool via `--renderer-process-limit` given the site-isolation trade-off.
