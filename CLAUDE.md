# Working in this repository

## Read this first

**[`docs/knowledge-graph.json`](docs/knowledge-graph.json) is the authoritative model of why this
codebase is shaped the way it is.** Read it before proposing or making architectural changes. The
code says what the system does; the graph says why, what was rejected, and what cannot move.

Key node types and how to treat them:

- `decision` — carries `rationale` and `revisit_if`. If a proposal doesn't clear the `revisit_if`
  bar, it has already been considered and rejected. Don't re-litigate it.
- `constraint` — platform, licensing or physical limits. **Immovable.** Not preferences.
- Any node with an `invariant` field — load-bearing. Violating it breaks the core thesis.

## The one thing that must never break

`d.shared-environment`: **all WebView2 instances share a single `CoreWebView2Environment`.**

One environment means one browser process hosting many renderers. If any code path calls
`EnsureCoreWebView2Async()` without passing the shared environment, WebView2 silently creates a
*new browser process* for that control. That single omission spawns a browser process per tab and
destroys the memory budget the entire project exists to enforce — and it fails silently, with no
error, showing up only as memory that never comes back.

Always thread the shared environment through. Never let a WebView2 self-initialise.

## Commit protocol

Every functional change ships as its own commit with a matching design note:

1. Implement the change.
2. Write `docs/commits/NNNN-slug.md` — what landed, why this shape, what was rejected, what it
   costs, and what's now measurable. Use the existing notes as the template.
3. Update `docs/knowledge-graph.json` **in the same commit** — add nodes for new components,
   decisions and constraints; add edges; bump `graph_updated_at_commit`. A feature whose nodes are
   missing is invisible to the next agent.
4. Update the `docs/knowledge-graph.md` mermaid diagrams and node index to match, **and the
   `DATA` object in `docs/knowledge-graph.html`** — the explorer embeds its own copy so it stays a
   single self-contained file, so the two can drift. Node and edge counts must match the JSON.
5. Add the commit to the status table in `README.md`.
6. Reference knowledge-graph node ids (e.g. `d.shared-environment`) in the commit message body.

Commit messages: conventional-commit prefix, imperative subject, body explaining *why*.

## Build and verify

```powershell
dotnet build src\Hearth\Hearth.csproj -c Debug --nologo
dotnet run   --project src\Hearth\Hearth.csproj
```

Memory claims must be **measured, never estimated**. The reclaim-delta method is the house
technique: sample the WebView2 process-tree working set, evict, sample again, attribute the
difference. Do not report a memory number this project has not actually observed — see
`k.no-per-tab-memory-api` for why the obvious API does not exist.

## Conventions

- `store/` beside the executable holds all runtime state (WebView2 profile, screenshots, session
  index). It is gitignored and safe to delete.
- Comments explain *why*, not *what*. The knowledge graph carries the long-form reasoning; inline
  comments should point at node ids rather than restating them.
- Target `net9.0-windows`, x64, nullable enabled.
