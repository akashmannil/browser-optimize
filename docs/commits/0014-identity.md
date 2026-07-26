# 0014 — An icon, a name, and a title that is not a debug string

**Knowledge-graph nodes:** `d.hearth-mark`, `cmp.icon-generator`

The app looked anonymous. Windows showed the generic application icon in the
taskbar, and the window title read `Hearth — WebView2 150.0.4078.83` — a
developer's debug string wearing the product's clothes.

## The mark

An arch: the mouth of a hearth, drawn as a thick **stroke**, with a single ember
banked on the floor inside it.

![The Hearth icon at 16, 20, 24, 32, 48, 64 and 128 px, on dark and light backgrounds](images/icon.png)

Two attempts were rejected before this one, and both were instructive.

**A flame.** It is what every "fast / hot / energy" product already reaches for,
so it says nothing, and its silhouette is all thin tapering points — below about
24 px it turns to mush.

**A solid arch.** It read as a **tombstone**. Adding a bright bar at its base for
the hearth floor made it a headstone on a plinth, which is not the feeling a
browser wants. The mistake was conceptual rather than aesthetic: a hearth is an
*opening*, and drawing it as a filled shape says the opposite of the intended
thing.

An arch stroke is a single closed geometric form. It survives 16 px, it is not a
shape another browser uses, and it says *hearth* rather than *fire* — which is
the actual idea, since the product is about banking embers and coming back to
them, not about burning.

The tile is a warm near-black so it reads as a dark object on both a light and a
dark taskbar rather than dissolving into either. Every size is rendered
independently at 16× and downsampled with LANCZOS; Windows' own icon scaler is
poor, and a single 256 px master shrunk to 16 px is mud.

## Generated, not drawn

`tools/make-icon.py` produces `hearth.ico` and the contact sheet above. An icon
committed as an opaque binary is one nobody can adjust later — the script is the
source, and it carries the two rejected directions in its header so the next
person does not re-try them.

## Two icons, not one

`ApplicationIcon` and `Window.Icon` are different things and both were needed.
The first is the icon *on the file*, used by Explorer, Alt+Tab and a pinned
taskbar entry. The second is the icon of a *running window*, used by the live
taskbar button. Setting only one leaves the other generic, which is exactly the
half-finished look being fixed.

Verified against the built binary rather than assumed:

```
exe icon extracted : 32x32
ProductName        : Hearth
FileDescription    : Hearth Browser
Company            : akashmannil
window title       : 'Hacker News — Hearth'
window icon handle : non-zero (the window carries its own icon)
```

## The title

`Page title — Hearth`, the way every browser does it, falling back to `Hearth`
alone when no page is loaded and `Hearth — immersion` in immersion.

The title is one of the few places the app has to state its own name: it is what
Alt+Tab and the taskbar preview show. The WebView2 runtime version that used to
live there is a diagnostic, and has moved to the trace log where diagnostics
belong.

## Not yet true

No installer, so Windows has nothing to register the app with — it will not
appear in "Open with", set-default-browser, or the Start menu without being
pinned by hand. The icon has no light-mode variant; the dark tile is designed to
work on both instead. There is no favicon shown per tab in the strip, which is
the other half of looking like a browser and needs a favicon fetch per site.
