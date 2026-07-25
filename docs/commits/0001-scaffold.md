# 0001 — Scaffold

**Knowledge-graph nodes:** `d.webview2-shell`, `d.dotnet-wpf`, `d.shared-environment`, `cmp.app`, `cmp.mainwindow`

## What landed

A WPF + WebView2 shell that builds, launches, navigates, and creates its `CoreWebView2Environment`
explicitly. Plus the documentation spine: architecture note, knowledge graph, and this commit-note
format.

## Why this shape

### The environment is created explicitly, not implicitly

The obvious way to host WebView2 in WPF is to drop the control in XAML and call
`EnsureCoreWebView2Async()` with no arguments. That works, and it is wrong for this project.

With no argument, each control creates **its own environment**, and each environment gets **its own
browser process**. A twenty-tab session would run twenty browser processes. The memory budget this
project exists to enforce would be destroyed before a single feature was written — silently, with
no error, visible only as memory that never comes back.

So even at one tab, `MainWindow` creates the environment itself and passes it in. The single-tab
scaffold already has the multi-tab architecture's load-bearing constraint baked in. This is
`d.shared-environment`, and `CLAUDE.md` calls it out as the invariant that must never break.

### The user-data folder is explicit too

It's pinned to `store/webview2` beside the executable. Two reasons: a checkout stays
self-contained and disposable, and the hibernation store in commit `0004` needs a stable,
known location to write screenshots next to the profile they belong to.

### WPF over WinUI 3

WinUI 3 is the more modern host, but it pulls in MSIX packaging friction that fights `dotnet run`.
WPF has the maturest WebView2 integration, builds cleanly from the CLI, and — importantly for
commits `0003`–`0004` — gives direct control over individual `CoreWebView2Controller` lifetimes,
which is the mechanism eviction is built on.

### SDK version pinned to the runtime line

`Microsoft.Web.WebView2` is pinned to `1.0.4078.44` to match the installed runtime
(`150.0.4078.83`). The SDK is forward-compatible, so this isn't strictly required — but
`TrySuspend` and `MemoryUsageTargetLevel` behaviour is exactly what gets benchmarked in commit
`0005`, and a floating SDK line would make those measurements non-reproducible.

## What this costs

The explicit environment makes startup asynchronous — `MainWindow` can't navigate until
`CreateAsync` completes. At one tab that's invisible. At N tabs it becomes a real sequencing
problem: tab restoration has to await a shared environment that may still be initialising. Commit
`0002` handles it with a single awaited task the tab manager holds, rather than each tab racing to
initialise.

## Verified

```
dotnet build src\Hearth\Hearth.csproj -c Debug
→ Build succeeded. 0 Warning(s) 0 Error(s)
```

Toolchain confirmed present: .NET SDK 9.0.304, WebView2 Runtime 150.0.4078.83, VS 2022, x64.

## Not yet true

The README's central claim — flat memory at any tab count — is **unmeasured**. There is one tab and
no budget. `m.steady-state-ceiling`, `m.restore-fidelity` and `m.reclaim-delta` are all
`not-yet-measured` in the graph and stay that way until commit `0005` produces real numbers.

## Next

`0002` introduces the tab model and moves the shared environment out of `MainWindow` into a
`TabManager` that owns environment lifetime for every tab.
