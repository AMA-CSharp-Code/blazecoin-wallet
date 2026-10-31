// "Digital Fire Fighters" background — ported from the V2.0 desktop wallet's heaviest skin
// (wwwroot/js/mining-background.js paintDigitalFireFighters + wallet.css
// .skin-digital-fire-fighters), mobile-tuned: rows of firefighter tiles scrolling sideways in
// alternating directions. Each row builds one set of tiles then an exact clone of the set and
// translates by precisely one set width, so the loop is seamless (the incoming clones line up
// on the outgoing originals). Deliberately NOT ported from the desktop: the 387-image pool and
// its whole-set RAM preload (~645MB decoded — instant death on a phone) and the 16-image centre
// orbit (sits behind the cards at phone width, doubles the decode count). We ship a curated
// 14-image set (~0.6MB webp; the WebView fetches/decodes lazily, each unique image once) —
// worst case is a one-time fade-in per tile on first paint, never ongoing jank. Same module
// contract as coin-skin.js / axe-skin.js (start/stop on the shared #lite-skin-host), pauses
// while the app is backgrounded, self-disables under prefers-reduced-motion (CSS side).

const IMG_BASE = '_content/BlazecoinWallet.Lite.UI/img/ff/';
// The curated set (chosen by Andrew, 2026-07-27) — webp versions of the desktop originals.
const IMAGES = [
    'ComfyUI_00487_.webp', 'ComfyUI_00488_.webp', 'ComfyUI_00521_.webp', 'ComfyUI_00531_.webp',
    'ComfyUI_00671_.webp', 'ComfyUI_00868_.webp', 'ComfyUI_01145_.webp', 'ComfyUI_01327_.webp',
    'ComfyUI_01365_.webp', 'ComfyUI_01945_.webp', 'ComfyUI_02140_.webp', 'ComfyUI_02192_.webp',
    'ComfyUI_03737_.webp', 'ComfyUI_04628_.webp',
];
const TILE = 200;           // tile size in px (desktop uses 330; phone rows read better smaller)
const SPEED = 26;           // scroll speed in px/s (desktop 32 — a touch calmer on a phone)

let _host = null;
let _onVisibility = null;

function shuffle(arr) {
    for (let i = arr.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [arr[i], arr[j]] = [arr[j], arr[i]];
    }
    return arr;
}

function paint() {
    if (!_host) return;
    const vw = window.innerWidth || 400;
    const vh = window.innerHeight || 800;

    // Fixed-size tiles so the images keep the same apparent size at any viewport —
    // bigger screens get more rows/columns, not stretched art (desktop's rule).
    const rows = Math.max(3, Math.round(vh / TILE));
    const rowH = vh / rows;                       // ≈ TILE, fills the height exactly
    const perSet = Math.ceil(vw / TILE) + 2;      // cover the width plus a buffer
    const setW = perSet * TILE;
    const dur = (setW / SPEED).toFixed(1);

    const bg = document.createElement('div');
    bg.className = 'ff-bg';

    const pool = shuffle(IMAGES.slice());
    let imgIdx = 0;
    for (let r = 0; r < rows; r++) {
        const row = document.createElement('div');
        row.className = 'ff-row';
        row.style.height = `${rowH}px`;
        row.style.setProperty('--ff-set', `${setW}px`);
        row.style.animationName = (r % 2 === 0) ? 'lite-ff-right' : 'lite-ff-left';
        row.style.animationDuration = `${dur}s`;
        for (let c = 0; c < perSet; c++) {
            const cell = document.createElement('div');
            cell.className = 'ff-tile';
            cell.style.width = `${TILE}px`;
            const img = document.createElement('img');
            img.src = IMG_BASE + pool[(imgIdx++) % pool.length];
            img.alt = '';
            cell.appendChild(img);
            row.appendChild(cell);
        }
        // Duplicate the set as exact clones so the one-set scroll loops seamlessly.
        const set = Array.from(row.children);
        for (const c of set) row.appendChild(c.cloneNode(true));
        bg.appendChild(row);
    }

    _host.innerHTML = '';
    _host.appendChild(bg);
}

export function start(hostId) {
    const host = document.getElementById(hostId);
    if (!host) return;
    _host = host;
    host.classList.add('skin-digital-fire-fighters');
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
        _host.classList.remove('skin-digital-fire-fighters', 'lite-skin-paused');
        _host.classList.add('lite-skin-off');
        _host.innerHTML = '';
    }
    _host = null;
}
