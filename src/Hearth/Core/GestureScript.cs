namespace Hearth.Core;

/// <summary>
/// The listener injected into every page and frame to report navigation
/// gestures back to the shell.
///
/// WHY INJECTION AND NOT A WPF HANDLER (k.mouse-input-never-reaches-wpf). Mouse
/// input over web content is delivered to a window owned by msedgewebview2.exe,
/// exactly like keyboard input (k.two-keyboard-paths). WPF sees none of it:
/// MouseMove, MouseDown and PreviewMouseDown on the host all stay silent while
/// the cursor is over a page. Unlike the keyboard there is no
/// AcceleratorKeyPressed equivalent for the mouse, so the only place the gesture
/// can be recognised is inside the page itself.
///
/// Injected with AddScriptToExecuteOnDocumentCreatedAsync so it runs before page
/// script in every frame, and it posts through window.chrome.webview.
///
/// KNOWN LIMIT, and it is a real one: this cannot work where page script cannot
/// run -- the PDF viewer, browser error pages, downloads, view-source. On those
/// surfaces the gestures are simply unavailable, which is why every gesture has
/// a keyboard equivalent that goes down the native path instead.
/// </summary>
internal static class GestureScript
{
    /// <summary>Horizontal travel, in CSS pixels, before a swipe counts.</summary>
    private const int SwipeThreshold = 90;

    /// <summary>
    /// How close to an edge, in CSS pixels, counts as reaching for the chrome.
    /// Small enough that it takes intent, large enough to be hittable by
    /// slamming the pointer at the edge, which is how people actually do it.
    /// </summary>
    private const int EdgeThreshold = 4;

    public static string Source { get; } = $$"""
        (function () {
          if (window.__hearthGestures) return;
          window.__hearthGestures = true;

          var post = function (name) {
            try { window.chrome.webview.postMessage('hearth:' + name); } catch (e) {}
          };

          var startX = null;
          var fired = false;

          // buttons is a bitmask: 1 = left, 2 = right. Both down is 3, which is
          // the gesture -- and is also a chord no page uses for anything, which
          // is why it can be claimed without breaking sites.
          var bothDown = function (e) { return (e.buttons & 3) === 3; };

          window.addEventListener('mousedown', function (e) {
            if (bothDown(e)) { startX = e.screenX; fired = false; }
          }, true);

          window.addEventListener('mousemove', function (e) {
            if (startX === null || fired) return;
            if (!bothDown(e)) { startX = null; return; }

            var dx = e.screenX - startX;
            if (Math.abs(dx) < {{SwipeThreshold}}) return;

            fired = true;
            post(dx > 0 ? 'tab-prev' : 'tab-next');
          }, true);

          var end = function () { startX = null; };
          window.addEventListener('mouseup', end, true);
          window.addEventListener('mouseleave', end, true);

          // The right-button release at the end of a swipe would otherwise raise
          // a context menu over whatever the new tab is showing. Suppressed only
          // for the gesture itself, so an ordinary right-click still works.
          window.addEventListener('contextmenu', function (e) {
            if (fired) { e.preventDefault(); fired = false; }
          }, true);

          // Mouse thumb buttons. Chromium does not navigate on these inside an
          // embedded WebView2, so the shell has to.
          window.addEventListener('mouseup', function (e) {
            if (e.button === 3) { post('back'); }
            else if (e.button === 4) { post('forward'); }
          }, true);

          window.addEventListener('auxclick', function (e) {
            if (e.button === 3 || e.button === 4) e.preventDefault();
          }, true);

          // Edge proximity, for immersion's sliding chrome. The shell cannot
          // work this out for itself: the pointer is over a window it does not
          // own, so WPF sees no mouse position at all.
          //
          // Only transitions are posted, not every move, so an ordinary drag
          // across the page costs one comparison per event and nothing else.
          var lastEdge = 'none';
          window.addEventListener('mousemove', function (e) {
            var edge = 'none';
            if (e.clientY <= {{EdgeThreshold}}) edge = 'top';
            else if (e.clientY >= window.innerHeight - {{EdgeThreshold}}) edge = 'bottom';

            if (edge !== lastEdge) { lastEdge = edge; post('edge-' + edge); }
          }, true);
        })();
        """;
}
