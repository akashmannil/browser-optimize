# 0004 — Screenshot-backed hibernation

**Knowledge-graph nodes:** `cmp.snapshotstore`, `k.capture-requires-live`, `d.teardown-over-suspend`, `c.ram-for-disk-trade`, `m.restore-fidelity`

## What landed

Tabs now photograph themselves before losing their renderer. An evicted tab paints its own last
frame while it rebuilds, returns to the scroll offset it was left at, and full teardown is enabled
by default because there is finally something to repaint from.

Also: the measurement harness was wrong, so `0003`'s numbers are re-measured below.

## The bug worth recording

The first implementation captured inside `HibernateAsync`, right before tearing the tab down. That
is the obvious place. It produced **one file, zero bytes.**

`ActivateAsync` collapses the outgoing tab's `WebView2` *before* `EnforceBudgetAsync` runs, and
`CapturePreviewAsync` cannot photograph a collapsed controller — there is nothing painted. By the
time eviction happens the moment has passed.

This is `k.capture-requires-live`, a constraint written down in commit `0001`, violated in `0004`
by the person who wrote it. Writing a constraint down does not make it operational; the useful
form is not "capture while live" but:

> **Capture at blur, on the outgoing tab, before any visibility change.**

The correct place to capture is not where eviction happens. It is where focus leaves.

A second failure hid behind the first: writing `CapturePreviewAsync`'s stream straight into a
`FileStream` leaves a **0-byte PNG** when the capture yields nothing — and a 0-byte PNG is
indistinguishable from a real snapshot at the call site. That would have licensed a teardown with
nothing to repaint, which is precisely the failure the whole commit exists to prevent. Captures
are now buffered in memory and only written on non-zero length.

## Teardown is gated, not assumed

`AllowFullTeardown` now defaults **on**, but every teardown is conditional on
`Snapshots.Has(tab.Id)`. No snapshot means fall back to suspension — the page stays alive and
therefore stays lookable-at. The reclaim is taken in the common case without ever evicting a tab
that cannot be redrawn.

## Corrected measurements

`0003` sampled at 40 s. With eight tabs activating in sequence, that was **before the run had
settled** — it compared partially-loaded states. `HEARTH_FULL_TEARDOWN` also could only be switched
*on*, so it could not be A/B'd against its own absence on one build. Both are fixed: the flag is
bidirectional, and runs settle for 95 s (verified stable — 1126 MB at 45 s, 1117 MB at 75 s,
1116 MB at 100 s).

One build, 95 s settle, only the teardown flag varying:

| Configuration | Renderers | Memory | Reduction |
| --- | ---: | ---: | ---: |
| budget=8, no eviction | 22 | 2234 MB | — |
| budget=3, suspend only | 15 | 1520 MB | −32% |
| budget=3, teardown | 8 | 1150 MB | −49% |
| budget=1, teardown | 2 | **625 MB** | **−72%** |

The baseline is higher than `0003`'s (2234 vs 1823 MB) because every tab now fully loads before
sampling. The *conclusions* are unchanged and the deepest budget improved from −66% to −72%.
Teardown beating suspension by a wide margin is reconfirmed.

**A flag that cannot be turned off cannot be measured.** That is the harness lesson, and it is the
same family of error as `0002`'s: both produced real numbers that answered the wrong question.

## The disk trade is far better than predicted

`c.ram-for-disk-trade` predicted ~150 KB per snapshot and roughly 200:1. Measured across seven
real sites:

- **26.8 KB** average per PNG, 188 KB for all seven
- Marginal cost of one live tab: **~230 MB** on the same workload
- Real ratio: **~8,000:1** — about forty times better than predicted

PNG was expected to be the wasteful choice. Page screenshots compress extremely well because large
areas are flat colour. 1,000 hibernated tabs would occupy ~27 MB, so there is no reason to ration
snapshots, downscale them, or move to WebP for space.

## What this costs

Capture adds latency to every tab switch — a screenshot plus a scroll-reading script, awaited
inside the budget gate. It is why the settle time moved from 40 s to 95 s for eight tabs. Worth
watching if tab-switch latency becomes noticeable.

## Not yet true

**Restore fidelity is partial and unverified by a human.** Scroll offset is captured and replayed,
but form values, `sessionStorage`, media `currentTime`, and per-scroll-container offsets are all
still lost. A torn-down tab performs a real network reload — the placeholder *hides* the rebuild,
it does not remove it.

And the 220 ms hold before dropping the placeholder is a guess, not a measurement. Nobody has yet
confirmed the swap to live content is imperceptible, which is the entire premise. That is the most
likely place a visible flash is hiding.

## Next

`0005` replaces the developer-telemetry status bar with a real taskbar: navigation, a memory
readout in human terms, and the mode switcher that `0006` and `0007` need.
