# 0005 — Taskbar and low-power mode

**Knowledge-graph nodes:** `cmp.memoryprobe`, `d.content-blocking-is-the-indirect-lever`, `k.frame-blocking-breaks-pages`

## What landed

A real taskbar — back, forward, reload, home, address, and a measured memory readout — replacing
the developer telemetry strip. Plus low-power mode, which turned out to answer the dead end from
commit `0003`.

## The finding: content is the process-model lever

`0003` established that an embedder has **no** control over the WebView2 process model. Every
Chromium switch reached the browser process and was ignored. That looked terminal.

It wasn't, because the conclusion had a second half that went unexploited: **renderer count is a
function of page content.** Content is something an embedder controls absolutely. So low-power
mode refuses cross-origin subframe documents and media at the `WebResourceRequested` filter.

One tab, stackoverflow.com:

| Configuration | Renderers | Memory |
| --- | ---: | ---: |
| normal | 14 | 1355.6 MB |
| low power (blocking on) | **1** | **301.0 MB** |
| low power, blocking **disabled** | 14 | 1242.8 MB |

**−78%, and 14 renderers collapse to 1.**

The third row is the one that makes this conclusive. With low power engaged but blocking switched
off, renderer count returns to 14 — so the reduction is entirely attributable to frame blocking,
not to the tighter budget. With a single tab the budget never binds at all. Without that control
run the two effects would have been indistinguishable, which is the same confound that produced
`0002`'s retracted number.

A content filter achieved what every process-model switch could not.

## What it costs, honestly

Blocking cross-origin frames **breaks real things**: embedded video players, OAuth and SSO login
flows, comment widgets, payment frames, CAPTCHAs, map embeds. Some of these are the only route
through a page.

This is why it is a **mode**, with a toggle that visibly reads as engaged, and never a silent
default. A page that fails to log in with no explanation is worse than the memory it saves. The
future fix is a per-site allowlist — pass login and payment frames, still refuse ad and analytics
frames — which should keep most of the win with much less breakage.

## The memory readout is measured, not estimated

`MemoryProbe` takes a Toolhelp32 snapshot and walks descendants of our own PID, summing working
sets. It deliberately does not filter every `msedgewebview2.exe` on the machine: Windows runs
several other WebView2 apps — Widgets, Store, Teams — and counting theirs would inflate our number
with memory that isn't ours.

This is the workaround `k.no-per-tab-memory-api` prescribes, now running in-app on a two-second
cadence rather than only in benchmark scripts.

## Copy

The status strip used to read:

```
8 tabs · 3/6 live · 2 hibernated · renderer cap ∞ · Stack Overflow
```

It now reads:

```
847 MB    3 awake · 5 resting
```

**"Resting", not "hibernated."** Resting carries the promise that actually matters — *it will be
there when you come back* — where the mechanism's own vocabulary carries nothing. The old string
named renderers and caps, which is how the system is built, not what a person manages.

## Why the Big Picture button isn't here

It was drafted into this taskbar and pulled back out. A toggle that does nothing is worse than an
absent one, and Big Picture needs the thumbnail wall to be worth entering. It lands whole in
`0006`, button and behaviour together.

## What this costs

`ApplyContentPolicy` registers filters per realised tab, and toggling the mode reloads the active
tab so the change is visible immediately rather than arriving silently three navigations later.
That reload is a deliberate interruption — the alternative is a mode that appears to do nothing.

## Not yet true

The savings line reads *"N returned since this session's peak"*, which is honest but weak — peak
versus current is not the same as a counterfactual. Saying what a session *would* have cost
without eviction needs `m.reclaim-delta` attributing bytes per eviction, which is still unbuilt.

Low-power mode also has no per-site memory. Toggling it is global and forgotten on restart.

## Next

`0006` adds Big Picture mode: full-screen, keyboard-driven, one live tab, and the wall of
snapshots that commit `0004` has been quietly accumulating.
