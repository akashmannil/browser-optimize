# 0011 — The address bar follows the tab

**Knowledge-graph nodes:** `d.address-bar-follows-the-tab`

A one-condition fix, recorded because the reasoning is not obvious from the diff.

`Sync` refused to write the address bar whenever it had keyboard focus:

```csharp
if (active is not null && !AddressBar.IsFocused)
    AddressBar.Text = active.Url;
```

The intent is right — do not clobber a URL somebody is halfway through typing. But focus is the
wrong thing to key it on, because the address bar keeps focus across a tab switch. `Ctrl+T` focuses
it, and everything after that (`Ctrl+Tab`, `Ctrl+1`, `Ctrl+Shift+T`, picking a card in the grid)
changes tabs while it still has focus. So the bar went on displaying the *previous* tab's URL over
the *current* tab's page.

It showed up in the `0010` flow test: the strip highlighted **Example Domain**, the page was
`example.com`, and the address bar read `https://en.wikipedia.org/wiki/Tab_interface`.

That is the one thing an address bar must never do. It is the only place in the interface that
states what you are looking at, and a browser that misreports the current URL is not a cosmetic
problem — every judgement a person makes about whether to trust a page starts there.

The fix tracks which tab the displayed text belongs to and overwrites whenever that changes,
regardless of focus. Typing is still protected, because typing does not change the active tab.

```csharp
var switched = !ReferenceEquals(active, _addressBarTab);
if (active is not null && (switched || !AddressBar.IsFocused)) { ... }
```

Verified: with the address bar focused and holding `news.ycombinator.com`, `Ctrl+1` switches to tab
one and the bar reads `https://example.com/`.
