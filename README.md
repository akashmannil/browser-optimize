# Hearth

A low-resource Windows browser shell built on WebView2.

> **Core bet:** every other browser treats *loaded* as a tab's default state and unloading as an
> emergency measure. Hearth inverts it — **hibernated is the default, live is a budgeted
> exception.** Tabs are documents on disk that get temporarily instantiated into memory.

The product promise that follows from this: **you set a RAM ceiling and the browser respects it.**
Chrome's memory usage is unbounded by design. Chrome Memory Saver and Firefox's tab unloader are
both *reactive* — they act once you are already in trouble. A budget is enforced by construction.

500 tabs and 50 tabs cost the same.

## Why this exists

People keep 100–500 tabs open and refuse to close them. That is not user error: the tab strip is
the only place where "things I still care about" stays visible, and every tool that promises to
tidy it up (OneTab, Session Buddy, Toby) converts a visible anxious pile into an invisible list
nobody ever revisits.

Meanwhile the cost is real but illegible. A Chromium renderer costs ~40–80 MB for a *blank page* —
V8 isolate, Blink, Mojo, sandbox — before any content loads. At 100 tabs that is ~6 GB of pure
overhead. **Tab count, not page weight, is the dominant cost driver.** Users experience a slow
machine and blame the machine.

Hearth attacks both halves: make eviction imperceptible, and make the cost visible.

## Status

Early. Built commit by commit, each with a design note in [`docs/commits/`](docs/commits/).

| Commit | What landed |
| --- | --- |
| [`0001`](docs/commits/0001-scaffold.md) | Repo, WPF + WebView2 shell, shared environment, docs + knowledge graph |
| [`0002`](docs/commits/0002-tab-model.md) | Tab model, tab strip, `TabManager` owns the environment |
| [`0003`](docs/commits/0003-live-budget.md) | Live-tab budget + score-based eviction. **Thesis validated** |
| [`0004`](docs/commits/0004-snapshot-hibernation.md) | Screenshot-backed hibernation; teardown on by default |
| [`0005`](docs/commits/0005-taskbar-low-power.md) | Taskbar, measured memory readout, low-power mode |
| [`0006`](docs/commits/0006-big-picture.md) | Big Picture — full-screen tab wall, live budget pinned to 1 |
| [`0007`](docs/commits/0007-ui-overhaul.md) | UI overhaul — custom chrome, light/dark theming, motion |
| [`0008`](docs/commits/0008-keyboard-and-lean-default.md) | Keyboard shortcuts that actually fire; lean by default + per-site shield |

### Lean is the default

There is no low-power toggle. The live budget is **3** and cross-origin subframes and media are
refused from startup — what used to be a mode is simply what the browser is (`d.lean-is-the-default`).

That is only defensible because breakage has a remedy in the same place as its explanation. Each
tab counts what was refused; a shield appears in the address bar **only when something actually
was**; one click exempts the host permanently and reloads. Granting a site is visibly expensive,
which is the point — on `stackoverflow.com` it goes from 1 renderer / 719 MB to 14 renderers /
1.6 GB, and the status bar says so.

### Keyboard

Chrome's bindings, because these live in muscle memory: `Ctrl+T`/`W`/`Shift+T`, `Ctrl+L`, `Ctrl+R`,
`Ctrl+Shift+R`, `Alt+←`/`→`, `Ctrl+Tab`, `Ctrl+1`–`8`, `Ctrl+9`, `Ctrl+±`/`0`, `F5`, `F6`.

Getting these to work at all needed two separate delivery paths. WebView2 is out-of-process, so
while a page has focus its key messages are queued to *the browser process's* thread and
`Window.PreviewKeyDown` never fires (`k.two-keyboard-paths`). Page keys arrive only through
`CoreWebView2Controller.AcceleratorKeyPressed`, which the WPF wrapper does not expose.

**Big Picture** (`F9` / `Ctrl+Shift+A`) — a full-screen, keyboard-driven wall of every tab, built
from the screenshots hibernation already produces. Entering it pins the live budget to **1**: you
are looking at pictures, so exactly one page needs to be real. The lean-back interface and the
memory architecture happen to want the same thing, which is the reason the mode exists rather than
being a skin. Leaving it puts every other tab straight to sleep, rather than waiting for pressure.

## Interface

There is no native title bar — the tab strip *is* the caption, with window controls at its right
edge. Light and dark are fully tokenised and follow Windows by default; the toolbar button cycles
System → Light → Dark. Neutrals are Apple's system greys, with the ember accent darkened in light
mode because ember on white fails contrast.

```powershell
HEARTH_THEME=light   # or dark, or unset to follow Windows
```

### Content filtering (commits `0005`, `0008`)

One tab on stackoverflow.com:

| Configuration | Renderers | Memory |
| --- | ---: | ---: |
| normal | 14 | 1356 MB |
| filtering on | **1** | **301 MB** (−78%) |
| filtering on, blocking disabled *(control)* | 14 | 1243 MB |

Commit `0003` found that no Chromium process-model switch has any effect through WebView2. But
renderer count is a function of page *content*, and content an embedder controls completely — so
the request filter refuses cross-origin subframes and media. The control run isolates the win to
frame blocking rather than the tighter budget.

It breaks things, deliberately and visibly: embedded video, OAuth logins, payment frames and
CAPTCHAs all stop working. Through `0007` that was why it stayed behind a toggle. Since `0008` it
is the default, because the shield gives it a one-click, per-host way out — and `0008` re-measured
the same 14→1 renderer result under a narrower cross-site-only rule.

### Measured (commit `0004`)

Eight real sites, every tab activated so the budget binds. One build, 95 s settle, only the
teardown flag varying. Working set summed over the WebView2 process tree, filtered to Hearth's
own profile:

| Configuration | Renderers | Total | Reduction |
| --- | ---: | ---: | ---: |
| budget=8 (no eviction) | 22 | 2234 MB | — |
| budget=3, `TrySuspend` | 15 | 1520 MB | **−32%** |
| budget=3, full teardown | 8 | 1150 MB | **−49%** |
| budget=1, full teardown | 2 | 625 MB | **−72%** |

Snapshots cost **26.8 KB** each on average against roughly **230 MB** per live tab — a real
RAM-for-disk trade near **8,000:1**, some forty times better than the 200:1 originally predicted.
A thousand hibernated tabs would occupy about 27 MB.

And with the budget fixed at 3, varying only how many tabs are open:

| Tabs | Renderers | Total |
| ---: | ---: | ---: |
| 4 | 3 | 752 MB |
| 8 | 5 | 911 MB |
| 16 | 3 | **548 MB** |

**Sixteen tabs cost less than four.** Memory is decoupled from tab count; the residual variance is
which *pages* are live when sampling stops, not how many tabs exist.

### Two findings worth knowing

**An embedder has no control over the WebView2 process model.** `--renderer-process-limit` and
`--process-per-site` reach the browser process — verified in its command line — and are then
ignored. All configurations produced exactly 14 renderers on the same page. Site isolation is
permitted to exceed the soft cap, because a cross-origin frame *must* get a dedicated process.
Renderer count is therefore a function of page content alone, which leaves eviction as the only
available lever. This is also the strongest argument that WebView2 is a staging post rather than
the destination — a Gecko fork regains this control via `dom.ipc.processCount`.

**Commit `0002` reported a per-tab cost that was wrong**, and it is retracted in `0003`. Only the
last startup URL was activated, so that run had one live tab, not five; page complexity was
misread as per-tab cost. Cold tabs cost nothing: 5× stackoverflow measured identically to 1×.

## Requirements

- Windows 10/11 x64
- .NET 9 SDK
- WebView2 Runtime (ships with Windows 11; [Evergreen installer](https://developer.microsoft.com/microsoft-edge/webview2/) otherwise)

## Build and run

```powershell
dotnet build src\Hearth\Hearth.csproj -c Debug
dotnet run   --project src\Hearth\Hearth.csproj
```

Runtime state (WebView2 profile, hibernation screenshots, session index) is written to a `store/`
folder beside the built executable, so a checkout stays self-contained and disposable.

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — the design and where memory actually goes
- [`docs/knowledge-graph.html`](docs/knowledge-graph.html) — **interactive graph explorer**; open it in a browser
- [`docs/knowledge-graph.json`](docs/knowledge-graph.json) — machine-readable, for AI agents
- [`docs/knowledge-graph.md`](docs/knowledge-graph.md) — mermaid rendering and node index
- [`docs/commits/`](docs/commits/) — one design note per commit

## Known limits

These are constraints, not bugs — see the `constraint` nodes in the knowledge graph.

- **Windows-only.** WebView2 targets Windows; cross-platform needs CEF or wry.
- **DRM.** Netflix/Spotify need Widevine, which requires a licence relationship with Google.
- **No per-tab memory API.** Neither WebView2 nor Chromium exposes per-renderer memory to
  embedders. Per-tab cost is inferred by measuring reclaim deltas across eviction.
