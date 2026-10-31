// Animated coin background — ported from the V2.0 desktop wallet's "Pixelated Coins" skin
// (wwwroot/js/mining-background.js paintCoinGrid + wwwroot/css/wallet.css .skin-pixelated-coins),
// tuned for mobile: a fixed-size cell grid, oversized with a margin, that drifts diagonally by
// EXACTLY one pattern period (2 cells). Because the pattern repeats every 2 cells, the loop
// lands on an identical field — so the coins appear to travel down-left forever with no snap
// (the desktop does the same via a fixed 10x10 grid drifting 20% = 2 cells). Flip + drift are
// pure CSS (no requestAnimationFrame loop — cheap), it pauses while the app is backgrounded, and
// it self-disables under prefers-reduced-motion. Loaded as an ES module by the Blazor layout so
// no app-head HTML needs editing; module-scoped state is shared across importers.

const COIN_SRC = '_content/BlazecoinWallet.Lite.UI/img/coin.webp';
const CANVAS_PX = 160;          // per-cell coin render resolution (crisp at the display size)
const CELL_PX = 225;            // on-screen cell size in px (coin = 80% of this, ~180px)
const PERIOD = 2;               // the pattern repeats every 2 cells (stagger + checkerboard flip)
const MAX_CELLS = 160;          // battery cap — never paint more than this many canvases

let _host = null;
let _coinImg = null;
let _onVisibility = null;

function drawMirroredCoin(canvas, img) {
    canvas.width = CANVAS_PX;
    canvas.height = CANVAS_PX;
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = false;
    ctx.save();
    ctx.scale(-1, 1); // face right, like the desktop skin (CSS rotateY would clobber a scaleX)
    ctx.drawImage(img, -CANVAS_PX, 0, CANVAS_PX, CANVAS_PX);
    ctx.restore();
}

function layout() {
    // A fixed-px cell grid sized to the viewport PLUS a margin, so a diagonal drift of one
    // pattern period (2 cells) never exposes an edge. Cols/rows are kept EVEN so the 2-cell
    // period tiles cleanly to the grid edges, keeping the loop seamless.
    const vw = window.innerWidth || 400;
    const vh = window.innerHeight || 800;
    const drift = PERIOD * CELL_PX;
    const marginCells = PERIOD + 1;                 // drift (2 cells) + 1 cell slack, each side
    let cols = Math.round(vw / CELL_PX) + 2 * marginCells;
    let rows = Math.round(vh / CELL_PX) + 2 * marginCells;
    if (cols % 2) cols++;
    if (rows % 2) rows++;
    while (cols * rows > MAX_CELLS) { if (cols >= rows) cols -= 2; else rows -= 2; }
    return { cols, rows, drift, offset: marginCells * CELL_PX };
}

function paint() {
    if (!_host || !_coinImg) return;
    const { cols, rows, drift, offset } = layout();
    _host.innerHTML = '';
    _host.style.width = `${cols * CELL_PX}px`;
    _host.style.height = `${rows * CELL_PX}px`;
    _host.style.left = `${-offset}px`;
    _host.style.top = `${-offset}px`;
    _host.style.gridTemplateColumns = `repeat(${cols}, ${CELL_PX}px)`;
    _host.style.gridTemplateRows = `repeat(${rows}, ${CELL_PX}px)`;
    // The exact-2-cell drift vector (down-left), consumed by the drift keyframe.
    _host.style.setProperty('--drift-x', `-${drift}px`);
    _host.style.setProperty('--drift-y', `${drift}px`);

    const total = cols * rows;
    for (let i = 0; i < total; i++) {
        const row = Math.floor(i / cols);
        const col = i % cols;
        const cell = document.createElement('div');
        cell.className = 'coin-cell';
        if (row % 2 === 1) cell.classList.add('stagger');
        // Checkerboard flip phase (period 2) — tiles with the 2-cell drift, so the loop stays
        // seamless while neighbouring coins spin out of phase.
        if ((row + col) % 2 === 1) cell.classList.add('flip');
        const canvas = document.createElement('canvas');
        canvas.className = 'pixel-coin';
        drawMirroredCoin(canvas, _coinImg);
        cell.appendChild(canvas);
        _host.appendChild(cell);
    }
}

export function start(hostId) {
    const host = document.getElementById(hostId);
    if (!host) return;
    _host = host;
    host.classList.add('skin-pixelated-coins');
    host.classList.remove('lite-skin-off');

    // Pause the CSS animations when the tab/app is hidden — no cycles spent off-screen.
    if (!_onVisibility) {
        _onVisibility = () => {
            if (_host) _host.classList.toggle('lite-skin-paused', document.hidden);
        };
        document.addEventListener('visibilitychange', _onVisibility);
    }

    const render = () => paint();
    if (_coinImg && _coinImg.complete) {
        render();
    } else {
        _coinImg = new Image();
        _coinImg.onload = render;
        _coinImg.src = COIN_SRC;
    }
}

export function stop() {
    if (_onVisibility) {
        document.removeEventListener('visibilitychange', _onVisibility);
        _onVisibility = null;
    }
    if (_host) {
        _host.classList.remove('skin-pixelated-coins', 'lite-skin-paused');
        _host.classList.add('lite-skin-off');
        _host.innerHTML = '';
        _host.style.width = '';
        _host.style.height = '';
        _host.style.left = '';
        _host.style.top = '';
        _host.style.gridTemplateColumns = '';
        _host.style.gridTemplateRows = '';
        _host.style.removeProperty('--drift-x');
        _host.style.removeProperty('--drift-y');
    }
    _host = null;
}
