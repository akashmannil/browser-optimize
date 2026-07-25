# Architecture

## Where browser memory actually goes

Optimisation work is only meaningful if it targets the dominant cost. Measured breakdown of a
Chromium-family browser:

| Cost centre | Magnitude | Notes |
|---|---|---|
| **Renderer process baseline** | **40–80 MB per tab** | V8 isolate, Blink, Mojo, sandbox — for a *blank page* |
| JS heap | 50–300 MB | Per modern SPA |
| Decoded image cache | Large and invisible | A 4K image is ~2 MB on disk, `3840×2160×4 ≈ 33 MB` decoded in RAM |
| GPU compositing surfaces | 100s of MB | Layer textures, often retained for backgrounded tabs |
| DOM / style / layout | Smallest slice | Proportional to node count |

The first row is the whole game. 100 tabs × 60 MB = **~6 GB before a single byte of content
loads**, and it has nothing to do with what's on the page. Tab *count* alone is the cost driver —
which is exactly the shape of the user problem (`p.tab-hoarding`).

**Corollary:** any memory strategy that does not reduce the number of live renderers is treating a
symptom. Compressing caches, tuning GC and trimming images all fight for the minority of the
budget.

## The inversion

Chrome Memory Saver and Firefox's tab unloader both exist, and both are **reactive** — they
trigger under memory pressure, after the user is already in trouble. Their default remains
"loaded", with unloading as the exception.

Hearth flips the default:

```
Conventional:  tab = a live renderer that may occasionally be unloaded
Hearth:        tab = a document on disk that is occasionally instantiated
```

A hard budget of N live renderers is enforced by construction. Tab 501 does not cost more than
tab 5, because the (N+1)-th activation evicts the least valuable live tab first.

## Tab lifecycle

```
   COLD ──────────► LIVE ──────────► WARM ──────────► HIBERNATED
   (URL +           (real            (TrySuspend,     (controller
    metadata,       WebView2,        controller       destroyed,
    never           holds a          retained,        screenshot +
    loaded)         renderer)        renderer         state on disk)
                                     mostly freed)
      ▲                │                                   │
      └────────────────┴───────────────────────────────────┘
                      rehydrate on activation
```

Four states, three eviction tiers, decreasing cost and increasing restore latency:

| State | Renderer cost | Restore latency | Mechanism |
|---|---|---|---|
| `Live` | Full | — | Visible, interactive |
| `Warm` | Reduced | Instant | `MemoryUsageTargetLevel = Low` (`api.memory-target-level`) |
| `Hibernated` | Near zero | ~100 ms | `TrySuspend()` (`api.trysuspend`) |
| `Cold` | Zero | Full page load | Controller destroyed; screenshot + state on disk |

Tiering matters because collapsing straight from `Live` to `Cold` is what makes existing
suspenders feel bad. The intermediate tiers absorb ordinary tab-switching, so full teardown only
happens to tabs the user genuinely isn't circling back to.

## Why eviction is imperceptible

Three mechanisms, all required together:

1. **Screenshot placeholder.** `CapturePreviewAsync()` (`api.capturepreview`) grabs the tab's
   pixels at blur. An evicted tab renders that image at the same scroll offset, so it is
   *visually indistinguishable from live*. Users don't notice the RAM cost; they shouldn't notice
   the fix either.
2. **State fidelity.** Scroll offsets (per scroll container, not just window), form field values,
   `<video>.currentTime` and `sessionStorage` are captured alongside the screenshot and replayed
   on rehydrate. `p.lossy-restore` is what kills adoption of every other suspender.
3. **Refindability-weighted eviction** (`c.refindability`, planned). A homepage or Wikipedia
   article is trivially recoverable and can be evicted hard. A filtered dashboard view, a deep
   search result or a half-filled form is not, and is preserved carefully. This attacks the
   underlying anxiety with logic rather than nagging.

**Timing constraint:** the screenshot must be taken at *blur*, while the WebView2 is still live and
rendered — never at eviction time. See `k.capture-requires-live`.

## Process model

```
                    ┌─────────────────────────────────┐
                    │  CoreWebView2Environment (ONE)  │
                    │   ├── browser process (ONE)     │
                    │   ├── renderer #1  ← live tab   │
                    │   ├── renderer #2  ← live tab   │
                    │   └── renderer #N  ← live tab   │
                    └─────────────────────────────────┘
                                   ▲
                     N is the budget, not the tab count
```

Every WebView2 created from a single environment shares one browser process. Letting each control
create its own environment — **which is the default if you don't pass one** — spawns a browser
process per tab and destroys the budget before it exists. This is the highest-leverage constraint
in the codebase (`d.shared-environment`); it is enforced by threading the shared environment
through every `EnsureCoreWebView2Async` call, and it fails *silently* when violated.

## Measuring cost without a per-tab memory API

Neither WebView2 nor Chromium exposes per-renderer memory to embedders (`k.no-per-tab-memory-api`).
The workaround is to measure rather than estimate:

1. Sample the working set of the whole WebView2 process tree.
2. Evict one tab.
3. Sample again; attribute the delta to that tab.

Repeated over a session this yields an empirically-calibrated per-site cost model — real measured
bytes, not the fabricated estimates most tab managers display. It doubles as the mechanism that
makes the invisible cost legible (`p.invisible-cost`), enabling honest statements like *"Figma has
cost you 1.4 GB for six days; you last touched it on Monday."*

## Rejected alternatives

| Rejected | Why |
|---|---|
| Browser extension | Cannot control the process model or do native state snapshotting — the two biggest levers |
| Engine from scratch | Ladybird: funded team since 2019, still pre-alpha. Servo: since 2012, compat still incomplete. Decade-scale for a team |
| Chromium fork | Site isolation spawns a process per site instance *by design*; you'd fight the codebase's core value. 30 GB checkout, ~100 GB build |
| Gecko fork | Genuinely the right long-term substrate — capped content-process pool already matches the goal. Deferred: months before the UX thesis can be tested at all |
| WinUI 3 host | MSIX packaging friction; WPF has the maturest WebView2 integration and builds cleanly from CLI |

The Gecko fork remains the plausible v2 substrate. The purpose of the WebView2 shell is to answer
one question cheaply: **does screenshot-backed restore feel convincing enough that users tolerate
an aggressive live-tab cap?** If it doesn't, a Gecko fork inherits the same failure — so it is
worth testing on the cheap substrate first.
