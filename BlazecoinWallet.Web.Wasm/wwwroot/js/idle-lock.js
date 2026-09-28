// Idle detector for the web head's auto-lock. Records the last user interaction (and the
// moment the tab became visible again) so the C# side can ask "how long since the user
// last touched the wallet?" without holding any timer of its own in JS. Passive listeners
// only; no DOM, no storage, no network.
let last = Date.now();
function touch() { last = Date.now(); }
const opts = { passive: true, capture: true };
for (const ev of ['pointerdown', 'keydown', 'touchstart', 'wheel', 'mousemove']) {
    document.addEventListener(ev, touch, opts);
}
// A hidden tab counts as idle: do NOT refresh on 'hidden'; do nothing special on 'visible'
// either — the C# poll decides, so a tab hidden longer than the timeout locks on return.

/** Milliseconds since the last interaction. */
export function idleMs() { return Date.now() - last; }

/** True while the tab is hidden (backgrounded / minimised). */
export function isHidden() { return document.visibilityState === 'hidden'; }
