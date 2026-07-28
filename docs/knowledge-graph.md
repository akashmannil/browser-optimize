# Hearth Knowledge Graph

This is the human-readable rendering of [`knowledge-graph.json`](knowledge-graph.json), which is the
authoritative machine-readable source. **If the two disagree, the JSON wins.**

> **Browsing by hand?** Open [`knowledge-graph.html`](knowledge-graph.html) — an interactive
> force-directed view of the same data, with filtering, search, and a record panel showing each
> node's rationale, invariants and relationships. Single self-contained file, no build step, no
> network access. Double-click it.

## Why this file exists

Source code records *what* a system does. It does not record why it is shaped that way, which
alternatives were considered and rejected, or which constraints are immovable. That reasoning
normally lives in a person's head and evaporates.

This graph is the durable form of that reasoning, written so that an AI agent picking up the
project cold can answer:

- Why is there exactly one `CoreWebView2Environment`? *(→ `d.shared-environment`)*
- Why not just fork Firefox? *(→ `d.webview2-shell`, `revisit_if`)*
- Why capture the screenshot on blur rather than on evict? *(→ `k.capture-requires-live`)*
- What breaks if I raise the live-tab budget? *(→ `c.process-overhead-dominates`)*

## How agents should use it

1. **Read before proposing architecture changes.** Nodes of type `decision` carry `rationale`
   and `revisit_if`. If your proposal does not clear the `revisit_if` bar, it has already been
   considered and rejected.
2. **Treat `constraint` nodes as immovable.** They encode platform and licensing limits, not
   preferences. `k.capture-requires-live` is a property of WebView2, not a design choice.
3. **Never violate an `invariant` field.** These are load-bearing.
4. **Update the graph in the same commit as the code.** A feature whose nodes are missing is
   invisible to the next agent.
5. **Reference nodes by `id` in commit messages and PRs.** IDs are stable; labels may be reworded.

## The graph

```mermaid
graph LR
  subgraph PROBLEMS
    P1["p.tab-hoarding<br/>Users won't close tabs"]
    P2["p.invisible-cost<br/>RAM cost is invisible"]
    P3["p.lossy-restore<br/>Suspenders restore lossily"]
    P4["p.archive-blackhole<br/>Archivers become graveyards"]
  end

  subgraph CONCEPTS
    C1["c.process-overhead-dominates<br/>40-80MB per blank renderer"]
    C2["c.hibernate-by-default<br/>CORE THESIS"]
    C3["c.ram-for-disk-trade<br/>~200:1"]
    C4["c.refindability<br/>(planned)"]
    C5["c.tab-taxonomy<br/>5 types, 1 costume"]
  end

  subgraph DECISIONS
    D1["d.webview2-shell<br/>Shell, not an engine fork"]
    D2["d.dotnet-wpf<br/>C# / .NET 9 / WPF"]
    D3["d.shared-environment<br/>ONE browser process"]
    D4["d.eviction-is-the-only-lever<br/>-66% measured"]
    D5["d.teardown-over-suspend<br/>-53% vs -35%"]
  end

  subgraph CONSTRAINTS
    K1["k.no-per-tab-memory-api"]
    K2["k.widevine-drm"]
    K3["k.windows-only"]
    K4["k.capture-requires-live"]
    K5["k.site-isolation-multiplies-renderers<br/>renderers track page content"]
    K6["k.no-process-model-control<br/>ALL flags ignored"]
  end

  subgraph WEBVIEW2_APIS
    A1["api.trysuspend"]
    A2["api.capturepreview"]
    A3["api.memory-target-level"]
    A4["api.additional-browser-arguments<br/>DEAD END"]
  end

  subgraph METRICS
    M1["m.steady-state-ceiling<br/>&lt;1.5GB at any tab count"]
    M2["m.restore-fidelity"]
    M3["m.reclaim-delta"]
  end

  P1 --> C2
  C1 --> C2
  C2 --> C3
  P3 -.constrains.-> C2
  P4 --> P1

  C2 --> D1
  D1 --> A1
  D1 --> A2
  D1 --> A3
  D2 -.implements.-> D1
  K3 -.constrains.-> D1
  K2 -.constrains.-> D1

  C1 --> D3
  D3 -.implements.-> C2
  K4 -.constrains.-> A2

  D1 --> A4
  K5 -.constrains.-> D3
  K5 -.constrains.-> M1
  C1 --> K5

  K5 --> K6
  K6 -.supersedes.-> A4
  K6 -.constrains.-> D1
  K6 --> D4
  D4 -.implements.-> C2
  D4 --> D5
  D5 -.blocked on.-> A2
  P3 -.constrains.-> D5
  A1 -.implements.-> D4

  C2 -.measured by.-> M1
  C2 -.measured by.-> M2
  P2 -.measured by.-> M3
  K1 -.constrains.-> M3

  style C2 fill:#E8833A,stroke:#8a4a12,color:#1E1F22
  style D3 fill:#2B6CB0,stroke:#1a4a7a,color:#fff
  style K1 fill:#742a2a,stroke:#4a1a1a,color:#fff
  style K2 fill:#742a2a,stroke:#4a1a1a,color:#fff
  style K3 fill:#742a2a,stroke:#4a1a1a,color:#fff
  style K4 fill:#742a2a,stroke:#4a1a1a,color:#fff
  style K5 fill:#742a2a,stroke:#4a1a1a,color:#fff
  style K6 fill:#742a2a,stroke:#4a1a1a,color:#fff
  style D4 fill:#2F855A,stroke:#1a4a30,color:#fff
  style D5 fill:#2F855A,stroke:#1a4a30,color:#fff
```

## Input and defaults (commit `0008`)

Two findings that are invisible in the source and expensive to rediscover.

```mermaid
graph TD
  subgraph KEYBOARD["Keyboard: two paths, one map"]
    KEY(["a keypress"])
    CHROME["chrome focus<br/>Window.PreviewKeyDown"]
    PAGE["page focus<br/>AcceleratorKeyPressed"]
    MAP["ShortcutRouter.Map<br/>static + pure"]
    DISPATCH["MainWindow.Execute"]

    KEY --> CHROME
    KEY --> PAGE
    CHROME -->|"declines if<br/>OriginalSource is WebView2"| MAP
    PAGE --> MAP
    MAP --> DISPATCH
  end

  KP["k.two-keyboard-paths<br/>WPF is deaf while a page has focus"]
  KW["k.wpf-wrapper-hides-controller<br/>reflection, fail-soft"]
  KD["k.double-dispatch-on-page-keys<br/>one Ctrl+T opened two tabs"]

  KP --> PAGE
  KW --> PAGE
  KD --> CHROME

  subgraph FILTER["Content filter: lean by default"]
    DL["d.lean-is-the-default<br/>no toggle; budget 3"]
    DE["d.filtering-needs-an-escape-hatch<br/>shield + per-host allowlist"]
    DG["d.engine-labels-over-inference<br/>Sec-Fetch-Dest / Sec-Fetch-Site"]
    KS["k.source-is-stale-during-navigation<br/>blocked every top-level nav"]

    DL --> DE
    KS --> DG
    DG --> DL
  end

  style KP fill:#742a2a,stroke:#4a1a1a,color:#fff
  style KW fill:#742a2a,stroke:#4a1a1a,color:#fff
  style KD fill:#742a2a,stroke:#4a1a1a,color:#fff
  style KS fill:#742a2a,stroke:#4a1a1a,color:#fff
  style DL fill:#2F855A,stroke:#1a4a30,color:#fff
  style DE fill:#2F855A,stroke:#1a4a30,color:#fff
  style DG fill:#2F855A,stroke:#1a4a30,color:#fff
  style MAP fill:#E8833A,stroke:#8a4a12,color:#1E1F22
```

`Map` is highlighted for the same reason `TabManager` is below: it holds an invariant. Both paths
must resolve through it, and a second key-to-command table anywhere in the codebase is a bug.

### Filtering as a default (commit `0008`)

One tab on `stackoverflow.com`, one Debug build, 90 s settle, only `HEARTH_BLOCK_FRAMES` varying:

| Configuration | Renderers | Total | |
| --- | ---: | ---: | ---: |
| filtering on (default) | **1** | **719 MB** | **−55%** |
| filtering off *(control)* | 14 | 1599 MB | — |
| after granting the site via the shield | 14 | 1.6 GB | — |

Absolute figures are **not** comparable with `0005`'s: this is a Debug build and includes Hearth's
own WPF process. The within-run control is what the claim rests on.

## Modes (commit `0009`)

```mermaid
graph LR
  KA["k.browser-args-fixed-at-creation<br/>switches set once, at CreateAsync"]
  DM["d.mode-switch-restarts<br/>relaunch with --mode="]
  SS["cmp.sessionstore<br/>tabs survive the restart"]
  ID["d.session-carries-tab-ids<br/>snapshots stay reachable"]

  BR["BROWSE<br/>budget 3 · filter on<br/>3 renderers · 890 MB"]
  IM["IMMERSION<br/>budget 5 · filter off · fullscreen<br/>20 renderers · 2197 MB"]

  KA --> DM
  DM --> SS
  SS --> ID
  DM --> BR
  DM --> IM

  KF["k.maximised-is-not-fullscreen<br/>the shell never yields to a maximised window"]
  KM["k.mouse-input-never-reaches-wpf<br/>no AcceleratorKeyPressed for the mouse"]
  KF --> IM
  KM --> IM

  style KA fill:#742a2a,stroke:#4a1a1a,color:#fff
  style KF fill:#742a2a,stroke:#4a1a1a,color:#fff
  style KM fill:#742a2a,stroke:#4a1a1a,color:#fff
  style DM fill:#2F855A,stroke:#1a4a30,color:#fff
  style ID fill:#2F855A,stroke:#1a4a30,color:#fff
  style BR fill:#2B6CB0,stroke:#1a4a7a,color:#fff
  style IM fill:#E8833A,stroke:#8a4a12,color:#1E1F22
```

Six real sites, every tab activated so the budget binds, one Debug build, 95 s settle:

| Mode | Renderers | Total |
| --- | ---: | ---: |
| browse | 3 | 890 MB |
| immersion | 20 | **2197 MB** |

**Immersion costs about 2.5x browse**, which is why it is entered deliberately. The thesis was never
that memory does not matter — it is that the user should decide when to spend it and be able to see
what they spent (`m.immersion-cost`).

## Transitions (commit `0015`)

Every transition in Hearth works around one fact: **the page cannot be animated.** WebView2 is a
child HWND that paints over all WPF content and ignores WPF opacity entirely, so nothing can be
blended with it. What is left is holding a still image, or an empty ground, until the real thing is
ready — and then cutting.

```mermaid
graph TD
  AIR["k.wpf-airspace<br/>a child HWND paints over everything"]

  NEV["k.the-placeholder-was-never-visible<br/>the snapshot was under a live page<br/>for three commits"]
  REV["d.reveal-when-the-page-is-ready<br/>hide the pages first,<br/>hold the new one back"]
  SIG["k.navigation-completed-is-not-first-paint<br/>DOMContentLoaded is ~1 s earlier"]

  WIN["d.airspace-needs-a-second-window<br/>a sibling HWND composites above"]
  VEIL["cmp.veil<br/>the curtain"]
  START["d.startup-is-a-transition"]
  REST["d.the-restart-is-a-transition<br/>same mark either side of the exit"]
  LAY["k.layered-windows-are-software-drawn<br/>124 fps opaque vs 63 dissolved"]

  MARK["d.one-marker-travels<br/>one border moves between chips"]
  STARVE["k.starving-the-layout-phase-blanks-webview2<br/>an unbounded retry blanked the page"]

  AIR --> NEV
  NEV --> REV
  SIG --> REV
  AIR --> WIN
  WIN --> VEIL
  VEIL --> START
  VEIL --> REST
  LAY --> VEIL
  SIG --> START
  MARK --> STARVE

  style AIR fill:#742a2a,stroke:#4a1a1a,color:#fff
  style NEV fill:#742a2a,stroke:#4a1a1a,color:#fff
  style SIG fill:#742a2a,stroke:#4a1a1a,color:#fff
  style LAY fill:#742a2a,stroke:#4a1a1a,color:#fff
  style STARVE fill:#742a2a,stroke:#4a1a1a,color:#fff
  style REV fill:#2F855A,stroke:#1a4a30,color:#fff
  style START fill:#2F855A,stroke:#1a4a30,color:#fff
  style REST fill:#2F855A,stroke:#1a4a30,color:#fff
  style MARK fill:#2F855A,stroke:#1a4a30,color:#fff
  style WIN fill:#2F855A,stroke:#1a4a30,color:#fff
  style VEIL fill:#E8833A,stroke:#8a4a12,color:#1E1F22
```

### The transition that was never on screen

Since `0004` the shell has painted an evicted tab's screenshot while its renderer rebuilt, then
cross-faded it out. Filmed at ~37 fps, that switch showed the page being left, **a near-black
frame**, then the new page cutting in. The snapshot was never visible: it sat under the outgoing
page for the whole rebuild, then under the unpainted incoming one.

| Pane luminance across a cold-tab switch | minimum | frames that are neither page |
| --- | ---: | ---: |
| before (`0004`–`0014`) | 18, 78 | 2, 1 |
| after (`0015`) | 229, 185, 232 | 0, 1, 0 |

### The curtain, opaque by measurement

Three cold starts each, one build, only the curtain's exit path varying:

| | ms/frame | fps | long frames | CPU |
| --- | ---: | ---: | ---: | ---: |
| opaque, contents fade | **8.0** | 124–125 | **0, 0, 0** | ~292 ms |
| `AllowsTransparency`, window fades | 16.2 | 56–67 | 0, 0, 1 | ~771 ms |

The window fade is the better-looking of the two — the chrome and the live page arrive together —
and costs half the frame rate, because a per-pixel-alpha window is composited in software
(`k.layered-windows-are-software-drawn`). Reachable as `HEARTH_VEIL=dissolve`.

## Measured results (commit `0003`)

Eight real sites, every tab activated so the budget binds:

| Configuration | Renderers | Total | Reduction |
| --- | ---: | ---: | ---: |
| budget=8 (no eviction) | 21 | 1823 MB | — |
| budget=3, `TrySuspend` | 10 | 1188 MB | −35% |
| budget=3, full teardown | 5 | 854 MB | −53% |
| budget=1, full teardown | 2 | 628 MB | −66% |

Budget fixed at 3, tab count varied — the flatness claim:

| Tabs | Renderers | Total |
| ---: | ---: | ---: |
| 4 | 3 | 752 MB |
| 8 | 5 | 911 MB |
| 16 | 3 | **548 MB** |

Sixteen tabs cost less than four. Memory is decoupled from tab count (`c.hibernate-by-default`
validated). Residual variance is which pages are live at sample time, not how many tabs exist.

### Retraction

Commit `0002` claimed ~234 MB/tab extrapolating past 20 GB at 100 tabs. **Retracted.** That run
had one live tab, not five — only the last startup URL is activated — so 14 renderers and 1170 MB
belonged to stackoverflow.com alone. Cold tabs cost nothing: 5× stackoverflow measured identically
to 1×. Always report live tab count beside open tab count.

### Dead end

No Chromium process-model switch has any effect through WebView2 (`k.no-process-model-control`).
`--renderer-process-limit=4`, `--process-per-site`, and both together all produced exactly 14
renderers on the same page, with the flag verified present in the browser process command line.
Eviction is the only lever that exists here.

## Component map

```mermaid
graph TD
  APP["cmp.app<br/>App.xaml.cs<br/>owns StoreRoot"]
  MW["cmp.mainwindow<br/>Views/MainWindow.xaml.cs<br/>shell + tab strip"]
  TM["cmp.tabmanager<br/>Core/TabManager.cs<br/>OWNS shared environment<br/>enforces the budget"]
  BT["cmp.browsertab<br/>Core/BrowserTab.cs<br/>durable tab identity"]
  TS["cmp.tabstate<br/>Core/TabState.cs<br/>Live/Warm/Hibernated/Cold"]
  EP["cmp.evictionpolicy<br/>Core/EvictionPolicy.cs<br/>scores eviction victims"]
  HO["cmp.hearthoptions<br/>Core/HearthOptions.cs<br/>all memory tunables"]

  MW --> TM
  TM --> APP
  TM --> BT
  TM --> EP
  TM --> HO
  EP --> BT
  BT --> TS

  style APP fill:#2B2D31,stroke:#8A8D93,color:#E3E3E3
  style MW  fill:#2B2D31,stroke:#8A8D93,color:#E3E3E3
  style TM  fill:#2B6CB0,stroke:#1a4a7a,color:#fff
  style BT  fill:#2B2D31,stroke:#8A8D93,color:#E3E3E3
  style TS  fill:#2B2D31,stroke:#8A8D93,color:#E3E3E3
  style EP  fill:#2F855A,stroke:#1a4a30,color:#fff
  style HO  fill:#2B2D31,stroke:#8A8D93,color:#E3E3E3
```

`TabManager` is highlighted because it holds the `d.shared-environment` invariant: it is the only
component permitted to call `EnsureCoreWebView2Async`, and it always passes the one environment it
owns. Environment ownership moved here from `MainWindow` in commit `0002`.

## Node index

<!-- GENERATED: node index below is rebuilt from knowledge-graph.json. -->

**114 nodes, 263 edges**, current to commit `0015`. This table is generated from the JSON; edit the JSON, never this table.

### Problems (4)

| id | one-line |
| --- | --- |
| `p.tab-hoarding` | People keep 100-500 tabs open because the tab strip is the only place where 'things I still care about' remain visible. Refusing to close is a... |
| `p.invisible-cost` | Users experience the symptom (a slow machine) but do not attribute it to the browser. Because the cost is never made legible, there is no pressure to... |
| `p.lossy-restore` | Chrome Memory Saver, The Great Suspender and similar tools lose scroll position, form state and media timestamps on restore. The papercut causes... |
| `p.archive-blackhole` | OneTab, Session Buddy and Toby convert a visible anxious pile into an invisible list nobody revisits. Users intuit this and resist adoption,... |

### Concepts (5)

| id | one-line |
| --- | --- |
| `c.process-overhead-dominates` | Any memory strategy that does not reduce the number of live renderers is treating a symptom. |
| `c.tab-taxonomy` | Active work, a todo, a reference doc, a read-later item, and a fear-of-loss artifact all wear the same UI costume. Applying one lifecycle policy to... |
| `c.refindability` | How hard a tab would be to recover if closed. A homepage or Wikipedia article is trivially refindable; a filtered dashboard view, a deep search... |
| `c.ram-for-disk-trade` | Disk cost is effectively free at any realistic tab count. 1,000 hibernated tabs would occupy roughly 27 MB. There is no reason to ration snapshots or... |
| `c.hibernate-by-default` **[core thesis]** | Every other browser treats loaded as default and unloading as an emergency. Chrome Memory Saver and Firefox tab unloading are REACTIVE, triggering... |

### Decisions (28)

| id | one-line |
| --- | --- |
| `d.webview2-shell` | De-risk the product thesis on the cheap substrate first. If screenshot-backed restore does not feel convincing here, a Gecko fork inherits the same... |
| `d.dotnet-wpf` | Chosen partly by environment: .NET 9 SDK and VS 2022 present, no Rust toolchain installed. WinUI3 was rejected for packaging friction. |
| `d.shared-environment` | Never call EnsureCoreWebView2Async without passing the shared environment. |
| `d.content-blocking-is-the-indirect-lever` | This is the answer to k.no-process-model-control and it closes the loop opened in 0003. Every Chromium switch was ignored; a content filter moved... |
| `d.eviction-is-the-only-lever` | Measured: eviction alone cut memory 35% (TrySuspend) to 66% (full teardown) at a fixed 8-tab workload, while every process-model flag produced no... |
| `d.teardown-over-suspend` | TrySuspend is worth keeping as the fast-resume tier for recently-blurred tabs, but it cannot carry the memory promise on its own. |
| `d.tokenised-theming` | Both dictionaries must expose exactly the same keys. A key present in one and missing from the other resolves to nothing after a swap and paints... |
| `d.bigpicture-exit-hibernates` | Leaving the wall is the clearest signal available that the user surveyed everything and picked one thing. Waiting for some later activation to push... |
| `d.bigpicture-is-budget-one` | The lean-back aesthetic and the memory architecture want the same thing here, which is the reason the mode is worth having rather than being a skin.... |
| `d.lean-is-the-default` | c.hibernate-by-default says hibernated is a tab's default state. A browser that only honours that once the user finds a toggle does not hold the... |
| `d.filtering-needs-an-escape-hatch` | Filtering can only be a default if a user who hits a broken login has an explanation and a remedy in the same place. A permanently visible badge is... |
| `d.engine-labels-over-inference` | Chromium computes these on every request and they depend on no state the embedder has to keep correct. The URL-comparison version needed Source to be... |
| `d.mode-switch-restarts` | Forced by k.browser-args-fixed-at-creation. The honest alternative was not 'switch modes live' -- that is unavailable -- but 'ship a mode that... |
| `d.session-carries-tab-ids` | Snapshots are keyed by tab id. Restoring with fresh ids would orphan every screenshot the previous run captured -- still on disk, permanently... |
| `d.immersion-evicts-by-recency` | 'The last few in the chain' is a different question from 'what is worth keeping'. Habit weighting is right for a working session -- a reference doc... |
| `d.grid-inherits-the-mode` | One layout, two correct results, and no mode check in the XAML. Up to 0009 the grid forced the window to maximise, so opening it resized the browser... |
| `d.single-click-opens` | This was the single biggest reason the grid read as broken. Nothing else in this app, and no tab switcher anyone has used, needs two clicks to pick a... |
| `d.zoom-connects-card-to-page` | The transition should make the page you land on visibly the card you chose. A cut, or a plain crossfade, leaves the user to re-establish where they... |
| `d.address-bar-follows-the-tab` | Guarding on focus alone was wrong because focus SURVIVES a tab switch: Ctrl+T focuses the bar, and every switch after that (Ctrl+Tab, Ctrl+1,... |
| `d.airspace-needs-a-second-window` | k.wpf-airspace makes an in-window overlay invisible the moment a page paints. 0006 solved that for the tab grid by collapsing the content host, which... |
| `d.snapshot-handoff-covers-the-load` | k.collapsed-host-blocks-initialisation forces the order 'animate, then activate', so the zoom finished and the page load began with nothing on screen... |
| `d.one-motion-vocabulary` | Before 0013 there were two durations and four easings applied more or less at random, and half the controls had no motion at all. The app read as... |
| `d.hearth-mark` | It says hearth rather than fire, which is the actual idea: the product is about banking embers and coming back to them, not about burning. An arch is... |
| `d.startup-is-a-transition` | Creating a browser process and loading a page takes seconds and used to happen in full view, as an empty grey window that looked hung. The wait is... |
| `d.the-restart-is-a-transition` | Switching mode kills the application and starts another one, which cannot be hidden. The mark is the only thing that can survive the boundary, so it... |
| `d.reveal-when-the-page-is-ready` | It is the only way the hibernation placeholder is ever on screen (k.the-placeholder-was-never-visible), and it removes the near-black frame that used... |
| `d.one-marker-travels` | Two chips fading in opposite directions read as two unrelated events. Browsers that move a single indicator read as continuous, because the eye... |
| `d.motion-honours-the-system-switch` | A browser is exactly the kind of application people turn animations off for: weak hardware, remote desktop, or a vestibular disorder that makes a... |

### Constraints (immovable) (28)

| id | one-line |
| --- | --- |
| `k.no-per-tab-memory-api` | Neither WebView2 nor Chromium exposes per-renderer memory to embedders. Per-tab cost must be inferred by measuring the reclaim delta across a... |
| `k.widevine-drm` | Netflix, Spotify and Prime need Widevine. WebView2 inherits Edge's Widevine support, but a shipped product needs a licence relationship with Google.... |
| `k.windows-only` | WebView2 targets Windows. Cross-platform would require CEF, Tauri's wry, or a per-OS webview abstraction. |
| `k.capture-requires-live` | A tab restored from a cold session has no screenshot until it has been live once. |
| `k.site-isolation-multiplies-renderers` | A shared environment guarantees one BROWSER process, not one renderer per tab. Capping live tabs alone does not cap renderers; ad-heavy or... |
| `k.no-process-model-control` | Renderer count is a function of page content alone. The ONLY memory lever available to a WebView2 embedder is reducing the number of live tabs --... |
| `k.frame-blocking-breaks-pages` | Through 0007 this is why it was a MODE and not a default. Since 0008 it IS the default, which is only defensible because... |
| `k.custom-chrome-maximise` | Removing the native title bar is not free. The first build hid the memory readout behind the taskbar whenever the window was maximised -- the readout... |
| `k.powershell-utf8-corruption` | This shipped a visible UI bug -- the Big Picture subtitle rendered as '6 open A. one stays awake'. A blanket Latin-1 to UTF-8 repair then made it... |
| `k.wpf-airspace` | Any full-window UI in this app must COLLAPSE the WebView2 host rather than stack above it. There is no Z-order fix. |
| `k.capture-needs-first-paint` | Capture is gated on BrowserTab.HasRendered, set from a successful NavigationCompleted and cleared on teardown. A refused capture correctly also... |
| `k.two-keyboard-paths` | ShortcutRouter.Map is static and pure, and is the only place a key becomes a command. |
| `k.wpf-wrapper-hides-controller` | Page-level keyboard handling requires reflection. It is cached once per process and fail-soft: a failed lookup degrades shortcuts to chrome-only and... |
| `k.double-dispatch-on-page-keys` | The chrome path must decline any keystroke that originated in web content, but only while the native hook is known to be working. |
| `k.source-is-stale-during-navigation` | This shipped in 0005 and was invisible for three commits, because filtering could only be engaged mid-session -- by which time a page had committed... |
| `k.browser-args-fixed-at-creation` | Any setting expressed as a Chromium switch cannot be changed in a running process. This is why switching modes RESTARTS Hearth rather than... |
| `k.maximised-is-not-fullscreen` | Real fullscreen is WindowState.Normal, Topmost, sized explicitly to the monitor in DIPs. MaximiseFix.Fullscreen does this; the WM_GETMINMAXINFO hook... |
| `k.mouse-input-never-reaches-wpf` | Pointer gestures can only be recognised inside the page, by an injected listener posting through window.chrome.webview. |
| `k.collapsed-host-blocks-initialisation` | MainWindow.RestoreShell runs, including a forced UpdateLayout, before any activation triggered from the grid. |
| `k.non-ascii-literals-do-not-survive-tooling` | Every hand-written C# file under src/Hearth contains zero bytes above 0x7F. Checkable in one line: sum(1 for b in open(path,"rb").read() if b > 127)... |
| `k.subtree-opacity-is-not-free` | No animation targets Opacity on a container that has more than a couple of overlapping children. |
| `k.effects-are-per-frame` | Elevation is expressed with a hairline border and background contrast instead. Effects are acceptable only on things that do not animate and do not... |
| `k.cached-bitmap-loses-to-visualbrush` | The obvious 'rasterise once' optimisation is wrong for anything that scales up substantially. Reverted; the VisualBrush is the shipped path. |
| `k.begintime-null-never-runs` | BeginTime is a TimeSpan? whose default is TimeSpan.Zero. Assigning null does not mean 'no delay', it means the timeline never begins. No error is... |
| `k.the-placeholder-was-never-visible` | A transition that hides a WebView2 must take the pages off screen BEFORE the rebuild and hold the new control back until its page has painted. There... |
| `k.navigation-completed-is-not-first-paint` | CoreWebView2.NavigationCompleted waits for every subresource on the page. Against google.com it fired after the startup curtain's 2.6 s ceiling had... |
| `k.starving-the-layout-phase-blanks-webview2` | A dispatcher callback that reschedules itself must have a bounded retry count. One that can never succeed does not fail loudly, it starves whatever... |
| `k.layered-windows-are-software-drawn` | Per-pixel-alpha (layered) windows bypass the GPU: the whole window is composited on the UI thread every frame. At browser size that halves the frame... |

### WebView2 APIs (4)

| id | one-line |
| --- | --- |
| `api.additional-browser-arguments` | Passes raw Chromium command-line switches to the browser process at environment-creation time. The only route to process-model control from an... |
| `api.trysuspend` | Suspends a WebView2, freeing significant renderer memory while keeping the controller alive for fast resume. Requires the controller to be invisible... |
| `api.capturepreview` | Captures the visible content of a WebView2 to a PNG or JPEG stream. Used to produce the visual placeholder that makes an evicted tab... |
| `api.memory-target-level` | Hints to the runtime that a WebView2 should minimise memory usage. Cheaper and lower-fidelity than TrySuspend; usable as an intermediate tier for... |

### Metrics (6)

| id | one-line |
| --- | --- |
| `m.steady-state-ceiling` | Target: total working set stays flat as tab count grows. Reference points: Chrome at 100 tabs is 6-10 GB, Firefox 2-4 GB. Hearth target is under 1.5... |
| `m.restore-fidelity` | Whether a rehydrated tab returns to the same scroll offset, form state and media position. The product thesis fails if users can feel eviction... |
| `m.reclaim-delta` | Working-set bytes returned to the OS by evicting one tab. Doubles as the per-tab cost estimate that makes the invisible cost legible to users. |
| `m.filtering-reclaim` | One tab on stackoverflow.com, one Debug build, 90 s settle, only HEARTH_BLOCK_FRAMES varying. Filtering on: 1 renderer / 719 MB. Filtering off... |
| `m.immersion-cost` | Six real sites, every tab activated so the budget binds, one Debug build, 95 s settle. Browse: 3 renderers / 890 MB. Immersion: 20 renderers / 2197... |
| `m.animation-smoothness` | Tab grid entrance after 0013: 0.7 long frames (>25 ms) per 450 ms entrance across six runs, worst gap 17-78 ms, typically 70-120 fps. The animations... |

### Components (24)

| id | one-line |
| --- | --- |
| `cmp.app` | Application entry point. Owns StoreRoot, the single on-disk location for the WebView2 user-data folder, hibernation screenshots and the session index. |
| `cmp.mainwindow` | Application shell. At commit 0001 it hosts a single WebView2 and creates the shared environment, proving the toolchain. Becomes the tab-strip host in... |
| `cmp.tabstate` | Four-state lifecycle enum (Live, Warm, Hibernated, Cold). Ordered so that a higher value means cheaper to hold and slower to restore; eviction always... |
| `cmp.browsertab` | Null View must never be treated as an error condition. Tabs are documents; renderers are a cache. |
| `cmp.tabmanager` | RealiseAsync is the ONLY place permitted to call EnsureCoreWebView2Async, and it always passes the shared environment. |
| `cmp.thememanager` | The token dictionary is always slot 0 of MergedDictionaries; it is replaced in place so styles declared after it keep resolving. |
| `cmp.maximisefix` | Hooks WM_GETMINMAXINFO so a custom-chrome window maximises to the work area instead of over the taskbar. Resolves the monitor per-window for... |
| `cmp.snapshotconverter` | Decoding full-resolution page captures for dozens of thumbnails would spend more memory on the tab overview than on the tabs, which would be an... |
| `cmp.memoryprobe` | Never display a memory number this process has not actually observed. The project's argument is that the cost is real; a fabricated figure forfeits... |
| `cmp.snapshotstore` | Capture only ever happens at blur, on the outgoing tab, before any visibility change. Never write the capture stream straight to a file -- buffer it... |
| `cmp.hearthoptions` | All memory tunables in one record, with environment-variable overrides so configurations can be A/B tested from a script without rebuilding. Gathered... |
| `cmp.evictionpolicy` | Scores live tabs worst-first. Recency decays as 1/(1+idleMinutes) and is weighted by log(1+activationCount), so a reference doc reopened twenty times... |
| `cmp.shortcutrouter` | Map is static and pure. Both paths must resolve through it; a second key-to-command table anywhere is a bug. |
| `cmp.siterules` | This is the component that makes filtering-by-default defensible rather than merely aggressive. |
| `cmp.diag` | 0008's central claim -- that a keystroke landing on a page reaches the shell -- cannot be checked by reading code or looking at the window, because... |
| `cmp.modeprofile` | Mode is fixed for the lifetime of the process. Anything that wants to vary it must go through App.RestartInto. |
| `cmp.sessionstore` | Exists to make d.mode-switch-restarts affordable. A browser that loses your tabs when you change a setting is one nobody changes the setting on. |
| `cmp.gesturescript` | The only place a pointer gesture can be recognised, per k.mouse-input-never-reaches-wpf. |
| `cmp.edgebar` | Never activates and never resizes the owner. Both would undo the fullscreen state the mode depends on. |
| `cmp.motion` | Only ever targets Opacity or a Transform. |
| `cmp.framemeter` | Smoothness was the one quality claim in the project that had never been measured. Long-frame count is the headline: mean frame rate saturates at the... |
| `cmp.icon-generator` | Each size is rendered independently at 16x supersample and downsampled with LANCZOS. Scaling a single 256px master down to 16px produces mud. |
| `cmp.veil` | Opaque, never AllowsTransparency. A layered window at this size is software-composited and costs half the frame rate... |
| `cmp.hearth-mark` | The breath is one Opacity on one small leaf: no effect, no layout, no transform. It runs during the busiest moment in the process and its only job is... |

### Commits (15)

| id | one-line |
| --- | --- |
| `commit.0001` | Repo initialised, WPF + WebView2 shell builds and navigates, documentation and knowledge-graph structure established. |
| `commit.0002` | Tab abstraction and TabManager introduced; environment ownership moved out of the window. Verified one browser process across 5 tabs, and discovered... |
| `commit.0003` | Live-tab budget with score-based eviction. Established that no Chromium process-model flag has any effect through WebView2, that eviction is... |
| `commit.0007` | Native title bar removed and the tab strip became the caption; full light/dark token theming following Windows; animated hover, selection and mode... |
| `commit.0006` | Full-screen, keyboard-driven wall of tab snapshots with the live budget pinned to one. Discovered the WPF airspace constraint and the... |
| `commit.0005` | Replaced the developer status strip with a real taskbar: navigation, a measured memory readout in plain language, and a low-power toggle. Low power... |
| `commit.0004` | Snapshot capture at blur, scroll replay on restore, placeholder painting during rebuild, and teardown enabled by default gated on holding a snapshot.... |
| `commit.0008` | Browser keyboard shortcuts wired on both delivery paths and verified through the real OS input stack; low-power toggle removed and lean made the... |
| `commit.0009` | Immersion added as the opposite pole to browse: real device fullscreen, generous recency-chain budget, filtering off, GPU and anti-throttling... |
| `commit.0010` | Found and fixed the reason picking a tab from the grid hung: activation was started while the content host was still collapsed. Grid no longer... |
| `commit.0011` | The address bar no longer keeps showing the previous tab's URL after a switch made while it had keyboard focus. |
| `commit.0012` | Restored the maximise and immersion buttons, which had been empty strings since 0008, and made every non-ASCII source literal an escape. Labelled the... |
| `commit.0013` | A shared motion vocabulary applied app-wide, with previously static controls animated and press feedback added. Introduced frame instrumentation,... |
| `commit.0014` | Real application icon (arch-and-ember mark, generated and reproducible), wired as both ApplicationIcon and Window.Icon, plus assembly metadata and a... |
| `commit.0015` | Startup curtain and mode-restart handoff carrying the mark; the hibernation placeholder made visible for the first time since 0004; a travelling... |
