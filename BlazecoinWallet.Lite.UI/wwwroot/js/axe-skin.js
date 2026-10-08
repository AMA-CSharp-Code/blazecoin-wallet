// Animated axe-pattern background — ported from the website's User account page
// (Views/UserAccountPage/User.cshtml .bz-axe-bg, itself from axe_pattern.html): a 45°-rotated
// field of black columns, each a track of spinning axes flowing along it, separated by crimson
// stripes carrying an uphill dashed centre line. Mobile-tuned: the stage is sized to the
// viewport (not a fixed 5625px square) and the element count is capped. The seamlessness rules
// from the original carry over exactly:
//   • each track is 2× its unique run and loops translateY(-50%)→0, so slot k lands where
//     slot k+unique was — the spin phase MUST repeat with period `unique` or the wrap pops;
//   • the stripe dash period (125px = 40 dash + 85 gap) must divide the stage height, so the
//     stage side is snapped UP to a multiple of 125.
// Same module contract as coin-skin.js (start/stop on the shared #lite-skin-host), pauses while
// the app is backgrounded, self-disables under prefers-reduced-motion (CSS side).

const AXE_SRC = '_content/BlazecoinWallet.Lite.UI/img/axe.png';
const SLOT_PX = 220;        // slot square (the account page's 319, scaled for a phone)
const AXE_PX = 150;         // axe sprite inside the slot (219 → 150 at the same ratio)
const STRIPE_PX = 40;       // crimson stripe between columns (55 → 40)
const SPIN_S = 68;          // one full axe rotation — same speed as the account page
const DASH_PERIOD = 125;    // stripe dash period; stage side snaps to a multiple (seamless wrap)
const MAX_AXES = 320;       // battery cap — grow the slots rather than exceed this
// The account page's flow stagger table (duration, negative delay) so neighbouring columns
// never sync up; columns cycle through it.
const FLOW = [
    ['160s', '-0.0s'], ['188s', '-4.3s'], ['212s', '-8.6s'], ['144s', '-12.9s'], ['176s', '-17.2s'],
    ['200s', '-21.5s'], ['160s', '-25.8s'], ['188s', '-30.1s'], ['212s', '-34.4s'], ['144s', '-38.7s'],
    ['176s', '-3.0s'], ['200s', '-7.3s'], ['160s', '-11.6s'], ['188s', '-15.9s'], ['212s', '-20.2s'],
];

let _host = null;
let _onVisibility = null;

function layout() {
    // A square stage rotated 45° covers a vw×vh viewport when its side ≥ (vw+vh)/√2;
    // add slot slack, then snap up to the dash period so the stripe-line wrap is seamless.
    const vw = window.innerWidth || 400;
    const vh = window.innerHeight || 800;
    let slot = SLOT_PX, axe = AXE_PX, stripe = STRIPE_PX;
    const need = Math.ceil((vw + vh) / Math.SQRT2) + 2 * slot;
    const side = Math.ceil(need / DASH_PERIOD) * DASH_PERIOD;
    let cols, unique;
    for (; ;) {
        cols = Math.ceil(side / (slot + stripe));
        unique = Math.ceil(side / slot);
        if (cols * unique * 2 <= MAX_AXES) break;
        slot += 40; axe += 27; stripe += 7;   // keep the proportions, shrink the count
    }
    return { side, cols, unique, slot, axe, stripe };
}

function paint() {
    if (!_host) return;
    const { side, cols, unique, slot, axe, stripe } = layout();

    const stage = document.createElement('div');
    stage.className = 'axe-stage';
    stage.style.width = `${side}px`;
    stage.style.height = `${side}px`;

    for (let col = 0; col < cols; col++) {
        // A crimson stripe (with its uphill dashed centre line) before every column but the
        // first — the stripes replace a flex gap so each can carry the moving line.
        if (col > 0) {
            const s = document.createElement('div');
            s.className = 'axe-stripe';
            s.style.flex = `0 0 ${stripe}px`;
            const line = document.createElement('div');
            line.className = 'axe-stripe-line';
            s.appendChild(line);
            stage.appendChild(s);
        }

        const column = document.createElement('div');
        column.className = 'axe-col';
        column.style.flex = `0 0 ${slot}px`;

        const track = document.createElement('div');
        track.className = 'axe-track';
        const flow = FLOW[col % FLOW.length];
        track.style.setProperty('--fdur', flow[0]);
        track.style.animationDelay = flow[1];

        for (let s = 0; s < unique * 2; s++) {
            const slotDiv = document.createElement('div');
            slotDiv.className = 'axe-slot';
            slotDiv.style.width = `${slot}px`;
            slotDiv.style.height = `${slot}px`;

            // Stagger each axe's spin with a negative delay so they all start at different
            // angles. The phase repeats with period `unique` (half the track) — the flow loop
            // wraps by -50% and slot k lands where slot k+unique was. The static rotate
            // matches the delayed angle for reduced-motion users (the animation overrides it).
            const phase = ((col * 131 + (s % unique) * 197) % (SPIN_S * 10)) / 10.0;
            const angle = (phase / SPIN_S * 360).toFixed(1);
            // One gold axe per period, in every third column only, scattered by column.
            const isGold = col % 3 === 0 && (s % unique) === ((col * 7) % unique);

            const a = document.createElement('div');
            a.className = isGold ? 'axe axe-gold' : 'axe';
            a.style.width = `${axe}px`;
            a.style.height = `${axe}px`;
            a.style.backgroundImage = `url('${AXE_SRC}')`;
            a.style.animationDelay = `-${phase.toFixed(1)}s`;
            a.style.transform = `rotate(-${angle}deg)`;
            slotDiv.appendChild(a);
            track.appendChild(slotDiv);
        }

        column.appendChild(track);
        stage.appendChild(column);
    }

    _host.innerHTML = '';
    _host.appendChild(stage);
}

export function start(hostId) {
    const host = document.getElementById(hostId);
    if (!host) return;
    _host = host;
    host.classList.add('skin-axe-pattern');
    host.classList.remove('lite-skin-off');

    if (!_onVisibility) {
        _onVisibility = () => {
            if (_host) _host.classList.toggle('lite-skin-paused', document.hidden);
        };
        document.addEventListener('visibilitychange', _onVisibility);
    }

    paint();
}

export function stop() {
    if (_onVisibility) {
        document.removeEventListener('visibilitychange', _onVisibility);
        _onVisibility = null;
    }
    if (_host) {
        _host.classList.remove('skin-axe-pattern', 'lite-skin-paused');
        _host.classList.add('lite-skin-off');
        _host.innerHTML = '';
    }
    _host = null;
}
