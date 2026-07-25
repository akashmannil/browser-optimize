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
| [`0002`](docs/commits/0002-tab-model.md) | Tab model, tab strip, `TabManager` owns the environment; baseline measured |

### Measured baseline (commit `0002`, before any eviction exists)

| Tabs | Browser processes | Renderers | Total working set |
| ---: | ---: | ---: | ---: |
| 1 | 1 | 1 | 304.5 MB |
| 5 | **1** | **14** | 1170.3 MB |

One browser process across five tabs confirms the shared-environment invariant. But fourteen
renderers for five tabs is Chromium **site isolation** allocating a renderer per cross-origin
iframe — so capping live *tabs* does not cap *renderers*. At ~234 MB/tab this currently
extrapolates worse than Chrome. Flattening that curve is the entire point of the next commits.

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
- [`docs/knowledge-graph.md`](docs/knowledge-graph.md) — visual reasoning graph
- [`docs/knowledge-graph.json`](docs/knowledge-graph.json) — machine-readable, for AI agents
- [`docs/commits/`](docs/commits/) — one design note per commit

## Known limits

These are constraints, not bugs — see the `constraint` nodes in the knowledge graph.

- **Windows-only.** WebView2 targets Windows; cross-platform needs CEF or wry.
- **DRM.** Netflix/Spotify need Widevine, which requires a licence relationship with Google.
- **No per-tab memory API.** Neither WebView2 nor Chromium exposes per-renderer memory to
  embedders. Per-tab cost is inferred by measuring reclaim deltas across eviction.
