# 0003 — Live budget and eviction

**Knowledge-graph nodes:** `k.no-process-model-control`, `d.eviction-is-the-only-lever`, `d.teardown-over-suspend`, `cmp.evictionpolicy`, `cmp.hearthoptions`

This is the commit where the thesis was actually tested. It produced one retraction, one dead end,
and one result.

## First: retracting a claim from 0002

Commit `0002` reported "5 tabs → 14 renderers → 1170 MB, ~234 MB/tab, extrapolating past 20 GB at
100 tabs." **That interpretation was wrong**, and the error is worth recording.

Only the *last* startup URL is activated. That run had **one** live tab, not five. The 14 renderers
and 1170 MB belonged entirely to `stackoverflow.com`'s cross-origin frames. Page complexity was
misread as per-tab cost.

Disproof:

| Run | Renderers | Total |
| --- | ---: | ---: |
| 1× stackoverflow (1 live) | 14 | 1176.9 MB |
| 5× stackoverflow (1 live, 4 cold) | 14 | 1166.6 MB |
| 1× example.com (1 live) | 1 | 302.6 MB |

Five tabs cost the same as one. **Cold tabs cost nothing** — which the architecture predicted and
`0002` simply failed to test. The lesson, now recorded on the metric node: always report *live*
tab count next to *open* tab count. Conflating them invents cost that doesn't exist.

## The dead end: no process-model control exists

`0002` assumed `--renderer-process-limit` was the lever for
`k.site-isolation-multiplies-renderers`. It isn't. Measured on stackoverflow.com, 1 live tab:

| Configuration | Renderers | Total |
| --- | ---: | ---: |
| no flags | 14 | 1179.9 MB |
| `--renderer-process-limit=4` | 14 | 1164.4 MB |
| `--process-per-site` | 14 | 1170.9 MB |
| both | 14 | 1161.2 MB |

Identical. And this is not a plumbing bug — the flag was confirmed present in the browser
process's own command line. Chromium receives it and ignores it, because site isolation is
permitted to exceed the soft process cap: a site-isolated cross-origin frame *must* get a
dedicated process.

**So renderer count is a function of page content alone, and an embedder cannot influence it.**

`RendererProcessLimit` and `ProcessPerSite` stay in `HearthOptions`, defaulted off, deliberately
retained as documented dead ends so the next person doesn't rediscover and retry them.

This is the strongest argument so far that WebView2 is a staging post rather than the destination
(`d.webview2-shell`). A Gecko fork regains exactly this control, because `dom.ipc.processCount`
genuinely does cap the content-process pool.

## The result: eviction is the only lever, and it works

Eight real sites, every tab activated in turn so the budget actually binds:

| Configuration | Renderers | Total | Reduction |
| --- | ---: | ---: | ---: |
| budget=8 (no eviction) | 21 | 1823.3 MB | — |
| budget=3, `TrySuspend` | 10 | 1187.6 MB | **−35%** |
| budget=3, full teardown | 5 | 854.0 MB | **−53%** |
| budget=1, full teardown | 2 | 627.7 MB | **−66%** |

Every process-model flag moved nothing; eviction moved two thirds. That asymmetry is the whole
finding.

And the headline claim — flatness. Budget fixed at 3, tab count varied:

| Tabs | Renderers | Total |
| ---: | ---: | ---: |
| 4 | 3 | 751.9 MB |
| 8 | 5 | 910.8 MB |
| 16 | 3 | **548.0 MB** |

**Sixteen tabs cost less than four.** Memory is fully decoupled from tab count. The residual
variance is driven by *which* pages happen to be live when sampling stops — the 16-tab run ended
on lightweight docs sites, the 4-tab run ended on GitHub and HN — not by how many tabs exist.

That is `c.hibernate-by-default` validated on real pages.

## Why this shape

### Eviction scores rather than sorts by recency

Pure LRU can't distinguish a reference doc reopened twenty times a day from an article opened once
and abandoned — the moment after you switch away, both look identical. `EvictionPolicy` weights
decaying recency `1/(1+idleMinutes)` by dampened habit `log(1+activationCount)`. Never-activated
tabs score negative infinity: opened in the background, never visited, no working context to lose.

This is also where `c.refindability` will land. A half-filled form should resist eviction far
harder than a homepage, because being wrong there costs a *loss*, not a reload.

### Teardown is gated off by default

It's the bigger lever (−53% vs −35%) but it stays off until `0004`. Without a screenshot
placeholder a torn-down tab reloads visibly on return — precisely the `p.lossy-restore` papercut
that makes users disable every other suspender. Shipping the memory win at the cost of the
perceptual promise would be trading away the thing that makes the product work.

### Budget enforcement is serialised

`ActivateAsync` holds a semaphore across realise → activate → enforce. Activation is async and
user-driven, so two fast tab switches would otherwise interleave and either evict past the budget
or race on the same controller.

### Tunables are environment-overridable

Every number above came from one build, varying only environment variables. Comparing across
*builds* would have made the runs non-comparable — a subtler version of the same mistake `0002`
made.

## What this costs

`TrySuspend` legitimately returns `false` — media playback, downloads and live connections all
block suspension. That's treated as a normal outcome: the tab stays `Live` and remains a candidate
next time, rather than being forced into an inconsistent state.

## Not yet true

Restore fidelity is still **unmeasured and probably poor**. Resuming a suspended tab has not been
checked for scroll position, form state or media timestamp, and full teardown currently reloads
from scratch. `m.restore-fidelity` stays `not-yet-measured`, and it is the metric that decides
whether any of the above is usable.

## Next

`0004` adds screenshot-backed hibernation, which is what unblocks full teardown as a default and
turns a 53% reduction into one users can't feel.
