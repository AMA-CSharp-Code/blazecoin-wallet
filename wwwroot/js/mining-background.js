// Background animations for the wallet pages.
//
//   * Dashboard has a swappable skin system (see window.DashboardSkins)
//     letting users pick between Pixelated Coins, Brick Tile Wall, and
//     Plain. The active skin paints into #dashboardBackground.
//   * Mining page has one fixed background (brick tile wall), invoked
//     via initMiningBackground() and painted into #tileBg.
//   * Header coins on the Dashboard are driven by startHeaderCoinSync(),
//     which reads the live CSS animation phase from the page coins so
//     they stay synced across route changes.

// ---------- Shared painters ----------

function shuffle(arr) {
    for (let i = arr.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [arr[i], arr[j]] = [arr[j], arr[i]];
    }
    return arr;
}

// ---------- Startup splash dismissal ----------
// The image-heavy skins + the Send/Receive/Transactions sets warm into RAM at
// startup (hundreds of MB to decode), which janks the first few seconds. The
// #app-splash overlay (in index.html) stays up until that caching settles: every
// preload registers its image count and reports each decode here; once decoded
// catches up — after all the staggered startup caches have registered (the last,
// CoinPiles, fires at 2000ms) — the splash fades out. A hard fallback guarantees
// it never gets stuck.
window.__preload = window.__preload || { total: 0, done: 0, settleTimer: null };
function splashTrackTotal(n) { window.__preload.total += n; }
function splashTrackOne() { window.__preload.done++; maybeDismissSplash(); }
function maybeDismissSplash() {
    const p = window.__preload;
    if (p.total === 0 || p.done < p.total) return;
    const now = performance.now();
    if (now < 2500) {
        // Everything decoded so far, but a later-staggered cache may not have
        // registered its total yet — re-check once past the last startup warm.
        clearTimeout(p.settleTimer);
        p.settleTimer = setTimeout(maybeDismissSplash, 2600 - now);
        return;
    }
    clearTimeout(p.settleTimer);
    p.settleTimer = setTimeout(function () { if (p.done >= p.total) dismissAppSplash(); }, 400);
}
function dismissAppSplash() {
    if (window.__splashDismissed) return;
    window.__splashDismissed = true;
    const el = document.getElementById('app-splash');
    if (!el) return;
    el.classList.add('hide');
    setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 700);
}
// Hard fallback so the splash can never get stuck (e.g. a decode that hangs).
setTimeout(dismissAppSplash, 15000);

const MINING_IMAGES = [
    'ComfyUI_02582_.webp', 'ComfyUI_02645_.webp', 'ComfyUI_02686_.webp', 'ComfyUI_02690_.webp',
    'ComfyUI_02693_.webp', 'ComfyUI_02695_.webp', 'ComfyUI_02704_.webp', 'ComfyUI_02709_.webp',
    'ComfyUI_02743_.webp', 'ComfyUI_02749_.webp', 'ComfyUI_02750_.webp', 'ComfyUI_02829_.webp',
    'ComfyUI_02958_.webp', 'ComfyUI_02965_.webp', 'ComfyUI_02969_.webp', 'ComfyUI_02980_.webp',
    'ComfyUI_03031_.webp', 'ComfyUI_03052_.webp', 'ComfyUI_03077_.webp', 'ComfyUI_03113_.webp',
    'ComfyUI_03146_.webp', 'ComfyUI_03220_.webp', 'ComfyUI_03224_.webp', 'ComfyUI_03231_.webp',
    'ComfyUI_03236_.webp', 'ComfyUI_03258_.webp', 'ComfyUI_03313_.webp', 'ComfyUI_03317_.webp',
    'ComfyUI_03327_.webp', 'ComfyUI_03363_.webp', 'ComfyUI_03391_.webp', 'ComfyUI_03448_.webp',
    'ComfyUI_03461_.webp', 'ComfyUI_03485_.webp', 'ComfyUI_03507_.webp', 'ComfyUI_03509_.webp',
    'ComfyUI_03523_.webp', 'ComfyUI_03628_.webp'
];

// Warm the Mining skin's image set. Idempotent. (preloadSkinImagesToRAM is a
// hoisted function declaration defined further down, available by the time this
// runs.)
function preloadMiningToRAM() {
    preloadSkinImagesToRAM('mining', 'MiningPoolImages/', MINING_IMAGES);
}
// Mining is the Mining page's permanent background and Mining is a sidebar
// destination hit most sessions, so warm it UNCONDITIONALLY at startup (not gated
// on the saved dashboard skin) and keep the cache constantly resident. Staggered
// after Network's warm so the visible skin and the Console background go first.
// Idempotent (the paint function call below is a no-op once this has run).
// (~54MB of image bytes held for the session — accepted because the Mining
// background is used every session.)
setTimeout(preloadMiningToRAM, 1500);

function paintTileRows(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadMiningToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    let shuffled = shuffle(MINING_IMAGES.slice());
    let idx = 0;
    function nextImage() {
        if (idx >= shuffled.length) { shuffled = shuffle(MINING_IMAGES.slice()); idx = 0; }
        return 'MiningPoolImages/' + shuffled[idx++];
    }

    const cols = 10;
    const tileW = window.innerWidth / 4;
    const tileH = tileW * (1024 / 1536);
    const rows = Math.ceil(window.innerHeight / tileH) + 4;

    for (let r = 0; r < rows; r++) {
        const row = document.createElement('div');
        row.className = 'tile-row';
        for (let c = 0; c < cols; c++) {
            const img = document.createElement('img');
            img.src = nextImage();
            img.alt = '';
            row.appendChild(img);
        }
        host.appendChild(row);
    }
}

const POOL_IMAGES = [
    'ComfyUI_06419_.webp', 'ComfyUI_06426_.webp', 'ComfyUI_06437_.webp', 'ComfyUI_06453_.webp',
    'ComfyUI_06455_.webp', 'ComfyUI_06456_.webp', 'ComfyUI_06460_.webp', 'ComfyUI_06467_.webp',
    'ComfyUI_06485_.webp', 'ComfyUI_06491_.webp', 'ComfyUI_06513_.webp', 'ComfyUI_06542_.webp',
    'ComfyUI_06552_.webp', 'ComfyUI_06561_.webp', 'ComfyUI_06566_.webp', 'ComfyUI_06571_.webp',
    'ComfyUI_06573_.webp', 'ComfyUI_06612_.webp', 'ComfyUI_06618_.webp', 'ComfyUI_06673_.webp',
    'ComfyUI_06674_.webp', 'ComfyUI_06693_.webp', 'ComfyUI_06727_.webp', 'ComfyUI_06750_.webp',
    'ComfyUI_06805_.webp', 'ComfyUI_06855_.webp', 'ComfyUI_06938_.webp', 'ComfyUI_06963_.webp',
    'ComfyUI_07217_.webp', 'ComfyUI_07257_.webp', 'ComfyUI_08208_.webp', 'ComfyUI_08390_.webp',
    'ComfyUI_08622_.webp', 'ComfyUI_08771_.webp'
];

// Warm the Mining Pools skin's image set. Idempotent.
function preloadMiningPoolsToRAM() {
    preloadSkinImagesToRAM('pool', 'PoolImages/', POOL_IMAGES);
}
// Only warm the cache when the Mining Pools skin is the saved dashboard choice;
// a runtime switch warms it via the paint function below.
try {
    if (localStorage.getItem('dashboard_skin') === 'mining-pools') {
        setTimeout(preloadMiningPoolsToRAM, 300);
    }
} catch (e) { /* localStorage unavailable */ }

// Mining-pool tile grid: 9x9 cells in a 3x3 base pattern tiled three times.
// Each cell starts at low-res pixelated and progressively renders higher
// resolution over ~20s before swapping to a full-res <img>. The grid is
// sized 300% of viewport and drifts diagonally on a 60s loop. Ported from
// Blazecoin_MVC_Web_App/Views/Miningpool/Index.cshtml.
function paintPoolGrid(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadMiningPoolsToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const cols = 9;
    const half = 3;
    const tileCount = cols * cols;
    const minRes = 8;
    const maxRes = 300;
    const logMin = Math.log(minRes);
    const logMax = Math.log(maxRes);
    const fadeInDuration = 20000;

    // Pick 9 unique images for the 3x3 base; the 9x9 is that base tiled.
    const shuffled = shuffle(POOL_IMAGES.slice());
    const baseImages = shuffled.slice(0, half * half);

    function getImageName(idx) {
        const row = Math.floor(idx / cols) % half;
        const col = (idx % cols) % half;
        return baseImages[row * half + col];
    }

    for (let i = 0; i < tileCount; i++) {
        const cell = document.createElement('div');
        cell.className = 'kb-cell';

        const canvas = document.createElement('canvas');
        canvas.width = maxRes;
        canvas.height = maxRes;
        cell.appendChild(canvas);
        host.appendChild(cell);

        const offscreen = document.createElement('canvas');
        const imgName = getImageName(i);
        const imgSrc = new Image();
        imgSrc.onload = (function (canvas, offscreen, imgSrc, imgName) {
            return function () {
                const cw = maxRes, ch = maxRes;
                const imgW = imgSrc.naturalWidth;
                const imgH = imgSrc.naturalHeight;
                let sx, sy, sw, sh;
                if (imgW / imgH > 1) {
                    sh = imgH; sw = imgH; sx = (imgW - sw) / 2; sy = 0;
                } else {
                    sw = imgW; sh = imgW; sx = 0; sy = (imgH - sh) / 2;
                }
                const ctx = canvas.getContext('2d');
                ctx.imageSmoothingEnabled = false;
                let lastRes = -1;
                let done = false;
                const startTime = performance.now();

                offscreen.width = minRes;
                offscreen.height = minRes;
                offscreen.getContext('2d').drawImage(imgSrc, sx, sy, sw, sh, 0, 0, minRes, minRes);
                ctx.drawImage(offscreen, 0, 0, minRes, minRes, 0, 0, cw, ch);
                lastRes = minRes;

                function animate(now) {
                    if (done) return;
                    if (!canvas.isConnected) return; // skin switched away
                    const elapsed = now - startTime;
                    if (elapsed >= fadeInDuration) {
                        done = true;
                        const finalImg = document.createElement('img');
                        finalImg.src = 'PoolImages/' + imgName;
                        finalImg.alt = '';
                        if (canvas.parentNode) canvas.parentNode.replaceChild(finalImg, canvas);
                        return;
                    }
                    let p = elapsed / fadeInDuration;
                    p = p * p * (3 - 2 * p); // smoothstep
                    let res = Math.round(Math.exp(logMin + (logMax - logMin) * p));
                    res = Math.max(minRes, Math.min(maxRes, res));
                    if (res !== lastRes) {
                        lastRes = res;
                        offscreen.width = res;
                        offscreen.height = res;
                        offscreen.getContext('2d').drawImage(imgSrc, sx, sy, sw, sh, 0, 0, res, res);
                        ctx.imageSmoothingEnabled = false;
                        ctx.clearRect(0, 0, cw, ch);
                        ctx.drawImage(offscreen, 0, 0, res, res, 0, 0, cw, ch);
                    }
                    requestAnimationFrame(animate);
                }
                requestAnimationFrame(animate);
            };
        })(canvas, offscreen, imgSrc, imgName);
        imgSrc.src = 'PoolImages/' + imgName;
    }
}

const BIRD_IMAGES = [
    'ComfyUI_02304_.webp', 'ComfyUI_02415_.webp', 'ComfyUI_02418_.webp', 'ComfyUI_02422_.webp',
    'ComfyUI_02423_.webp', 'ComfyUI_02427_.webp', 'ComfyUI_02445_12.webp', 'ComfyUI_02446_.webp',
    'ComfyUI_02449_.webp', 'ComfyUI_02451_.webp', 'ComfyUI_02455_.webp', 'ComfyUI_02457_.webp',
    'ComfyUI_02554_.webp', 'ComfyUI_02555_.webp', 'ComfyUI_02563_.webp', 'ComfyUI_02571_.webp',
    'ComfyUI_02582_.webp', 'ComfyUI_02589_.webp', 'ComfyUI_02607_.webp', 'ComfyUI_02610_.webp',
    'ComfyUI_02621_.webp', 'ComfyUI_02712_.webp', 'ComfyUI_02733_.webp', 'ComfyUI_02736_.webp',
    'ComfyUI_02889_.webp', 'ComfyUI_03053_.webp', 'ComfyUI_03094_.webp', 'ComfyUI_03168_.webp',
    'ComfyUI_03204_.webp', 'ComfyUI_03206_.webp', 'ComfyUI_03274_.webp', 'ComfyUI_03291_.webp',
    'ComfyUI_03294_.webp', 'ComfyUI_03301_.webp', 'ComfyUI_03340_.webp', 'ComfyUI_03358_.webp',
    'ComfyUI_03374_.webp', 'ComfyUI_03391_.webp', 'ComfyUI_03411_.webp', 'ComfyUI_03421_.webp',
    'ComfyUI_03432_.webp', 'ComfyUI_03571_.webp', 'ComfyUI_03575_.webp', 'ComfyUI_03580_.webp',
    'ComfyUI_03585_.webp', 'ComfyUI_03586_.webp', 'ComfyUI_03596_.webp', 'ComfyUI_03679_.webp',
    'ComfyUI_03722_.webp', 'ComfyUI_03740_.webp', 'ComfyUI_03769_.webp', 'ComfyUI_03849_.webp',
    'ComfyUI_03850_.webp', 'ComfyUI_03854_.webp', 'ComfyUI_03857_.webp', 'ComfyUI_03866_.webp',
    'ComfyUI_03900_.webp', 'ComfyUI_03954_.webp', 'ComfyUI_03965_.webp', 'ComfyUI_04022_.webp',
    'ComfyUI_04073_.webp', 'ComfyUI_04076_.webp', 'ComfyUI_04194_.webp', 'ComfyUI_04200_.webp',
    'ComfyUI_04208_.webp', 'ComfyUI_04213_.webp', 'ComfyUI_04272_.webp', 'ComfyUI_04283_.webp',
    'ComfyUI_04306_.webp', 'ComfyUI_04324_.webp', 'ComfyUI_04328_.webp', 'ComfyUI_04369_.webp',
    'ComfyUI_04420_.webp', 'ComfyUI_04490_.webp', 'ComfyUI_04506_.webp', 'ComfyUI_04734_.webp',
    'ComfyUI_04773_.webp', 'ComfyUI_04776_.webp', 'ComfyUI_04780_.webp', 'ComfyUI_04812_.webp',
    'ComfyUI_04889_.webp', 'ComfyUI_04919_.webp', 'ComfyUI_04973_.webp', 'ComfyUI_04977_.webp',
    'ComfyUI_04978_.webp', 'ComfyUI_04986_.webp', 'ComfyUI_04994_.webp', 'ComfyUI_05092_.webp',
    'ComfyUI_05117_.webp', 'ComfyUI_05124_.webp', 'ComfyUI_05277_.webp', 'ComfyUI_05364_.webp',
    'ComfyUI_05406_.webp', 'ComfyUI_05562_.webp', 'ComfyUI_05603_.webp', 'ComfyUI_05654_.webp',
    'ComfyUI_05672_.webp', 'ComfyUI_05734_.webp', 'ComfyUI_05754_.webp', 'ComfyUI_05763_.webp',
    'ComfyUI_05778_.webp', 'ComfyUI_05802_.webp', 'ComfyUI_05806_.webp', 'ComfyUI_05824_.webp',
    'ComfyUI_05945_.webp', 'ComfyUI_06008_.webp', 'ComfyUI_06108_.webp', 'ComfyUI_06112_.webp',
    'ComfyUI_06118_.webp', 'ComfyUI_06144_.webp', 'ComfyUI_06167_.webp', 'ComfyUI_06171_.webp',
    'ComfyUI_06212_.webp', 'ComfyUI_06303_.webp', 'ComfyUI_06446_.webp', 'ComfyUI_06465_.webp',
    'ComfyUI_06474_.webp', 'ComfyUI_06497_.webp', 'ComfyUI_06509_.webp', 'ComfyUI_06580_.webp',
    'ComfyUI_06597_.webp', 'ComfyUI_06607_.webp', 'ComfyUI_06609_.webp', 'ComfyUI_06661_.webp',
    'ComfyUI_06665_.webp', 'ComfyUI_06692_.webp', 'ComfyUI_06693_.webp', 'ComfyUI_06715_.webp',
    'ComfyUI_06724_.webp', 'ComfyUI_06725_.webp', 'ComfyUI_06733_.webp', 'ComfyUI_06740_.webp',
    'ComfyUI_06757_.webp', 'ComfyUI_06816_.webp', 'ComfyUI_06829_.webp', 'ComfyUI_06850_.webp'
];

const FIREFIGHTER_IMAGES = [
    'ComfyUI_00474_.webp',
    'ComfyUI_00479_.webp',
    'ComfyUI_00482_.webp',
    'ComfyUI_00487_.webp',
    'ComfyUI_00488_.webp',
    'ComfyUI_00492_.webp',
    'ComfyUI_00493_.webp',
    'ComfyUI_00501_.webp',
    'ComfyUI_00504_.webp',
    'ComfyUI_00506_.webp',
    'ComfyUI_00507_.webp',
    'ComfyUI_00508_.webp',
    'ComfyUI_00512_.webp',
    'ComfyUI_00515_.webp',
    'ComfyUI_00518_.webp',
    'ComfyUI_00521_.webp',
    'ComfyUI_00526_.webp',
    'ComfyUI_00531_.webp',
    'ComfyUI_00533_.webp',
    'ComfyUI_00535_.webp',
    'ComfyUI_00536_.webp',
    'ComfyUI_00538_.webp',
    'ComfyUI_00543_.webp',
    'ComfyUI_00544_.webp',
    'ComfyUI_00550_.webp',
    'ComfyUI_00553_.webp',
    'ComfyUI_00557_.webp',
    'ComfyUI_00558_.webp',
    'ComfyUI_00559_.webp',
    'ComfyUI_00562_.webp',
    'ComfyUI_00563_.webp',
    'ComfyUI_00568_.webp',
    'ComfyUI_00575_.webp',
    'ComfyUI_00577_.webp',
    'ComfyUI_00581_.webp',
    'ComfyUI_00586_.webp',
    'ComfyUI_00590_.webp',
    'ComfyUI_00598_.webp',
    'ComfyUI_00603_.webp',
    'ComfyUI_00605_.webp',
    'ComfyUI_00616_.webp',
    'ComfyUI_00618_.webp',
    'ComfyUI_00624_.webp',
    'ComfyUI_00649_.webp',
    'ComfyUI_00670_.webp',
    'ComfyUI_00671_.webp',
    'ComfyUI_00698_.webp',
    'ComfyUI_00723_.webp',
    'ComfyUI_00732_.webp',
    'ComfyUI_00737_.webp',
    'ComfyUI_00744_.webp',
    'ComfyUI_00746_.webp',
    'ComfyUI_00754_.webp',
    'ComfyUI_00768_.webp',
    'ComfyUI_00769_.webp',
    'ComfyUI_00771_.webp',
    'ComfyUI_00774_.webp',
    'ComfyUI_00781_.webp',
    'ComfyUI_00790_.webp',
    'ComfyUI_00793_.webp',
    'ComfyUI_00799_.webp',
    'ComfyUI_00805_.webp',
    'ComfyUI_00813_.webp',
    'ComfyUI_00831_.webp',
    'ComfyUI_00855_.webp',
    'ComfyUI_00856_.webp',
    'ComfyUI_00857_.webp',
    'ComfyUI_00858_.webp',
    'ComfyUI_00859_.webp',
    'ComfyUI_00865_.webp',
    'ComfyUI_00868_.webp',
    'ComfyUI_00869_.webp',
    'ComfyUI_00870_.webp',
    'ComfyUI_00871_.webp',
    'ComfyUI_00875_.webp',
    'ComfyUI_00876_.webp',
    'ComfyUI_00877_.webp',
    'ComfyUI_00878_.webp',
    'ComfyUI_00880_.webp',
    'ComfyUI_00881_.webp',
    'ComfyUI_00882_.webp',
    'ComfyUI_00884_.webp',
    'ComfyUI_00887_.webp',
    'ComfyUI_00891_.webp',
    'ComfyUI_00892_.webp',
    'ComfyUI_00893_.webp',
    'ComfyUI_00897_.webp',
    'ComfyUI_00901_.webp',
    'ComfyUI_00919_.webp',
    'ComfyUI_00920_.webp',
    'ComfyUI_00921_.webp',
    'ComfyUI_00927_.webp',
    'ComfyUI_00933_.webp',
    'ComfyUI_00953_.webp',
    'ComfyUI_00954_.webp',
    'ComfyUI_00962_.webp',
    'ComfyUI_00965_.webp',
    'ComfyUI_00966_.webp',
    'ComfyUI_00972_.webp',
    'ComfyUI_00974_.webp',
    'ComfyUI_00977_.webp',
    'ComfyUI_00978_.webp',
    'ComfyUI_00979_.webp',
    'ComfyUI_00982_.webp',
    'ComfyUI_00994_.webp',
    'ComfyUI_00996_.webp',
    'ComfyUI_01002_.webp',
    'ComfyUI_01010_.webp',
    'ComfyUI_01021_.webp',
    'ComfyUI_01040_.webp',
    'ComfyUI_01048_.webp',
    'ComfyUI_01049_.webp',
    'ComfyUI_01050_.webp',
    'ComfyUI_01051_.webp',
    'ComfyUI_01053_.webp',
    'ComfyUI_01059_.webp',
    'ComfyUI_01061_.webp',
    'ComfyUI_01063_.webp',
    'ComfyUI_01065_.webp',
    'ComfyUI_01066_.webp',
    'ComfyUI_01073_.webp',
    'ComfyUI_01077_.webp',
    'ComfyUI_01082_.webp',
    'ComfyUI_01118_.webp',
    'ComfyUI_01145_.webp',
    'ComfyUI_01153_.webp',
    'ComfyUI_01171_.webp',
    'ComfyUI_01176_.webp',
    'ComfyUI_01200_.webp',
    'ComfyUI_01207_.webp',
    'ComfyUI_01212_.webp',
    'ComfyUI_01227_.webp',
    'ComfyUI_01228_.webp',
    'ComfyUI_01230_.webp',
    'ComfyUI_01231_.webp',
    'ComfyUI_01233_.webp',
    'ComfyUI_01235_.webp',
    'ComfyUI_01242_.webp',
    'ComfyUI_01245_.webp',
    'ComfyUI_01253_.webp',
    'ComfyUI_01262_.webp',
    'ComfyUI_01266_.webp',
    'ComfyUI_01267_.webp',
    'ComfyUI_01270_.webp',
    'ComfyUI_01282_.webp',
    'ComfyUI_01286_.webp',
    'ComfyUI_01294_.webp',
    'ComfyUI_01298_.webp',
    'ComfyUI_01308_.webp',
    'ComfyUI_01312_.webp',
    'ComfyUI_01314_.webp',
    'ComfyUI_01322_.webp',
    'ComfyUI_01327_.webp',
    'ComfyUI_01328_.webp',
    'ComfyUI_01335_.webp',
    'ComfyUI_01354_.webp',
    'ComfyUI_01355_.webp',
    'ComfyUI_01365_.webp',
    'ComfyUI_01376_.webp',
    'ComfyUI_01384_.webp',
    'ComfyUI_01385_.webp',
    'ComfyUI_01386_.webp',
    'ComfyUI_01388_.webp',
    'ComfyUI_01406_.webp',
    'ComfyUI_01409_.webp',
    'ComfyUI_01416_.webp',
    'ComfyUI_01420_.webp',
    'ComfyUI_01433_.webp',
    'ComfyUI_01452_.webp',
    'ComfyUI_01458_.webp',
    'ComfyUI_01459_.webp',
    'ComfyUI_01462_.webp',
    'ComfyUI_01465_.webp',
    'ComfyUI_01466_.webp',
    'ComfyUI_01471_.webp',
    'ComfyUI_01473_.webp',
    'ComfyUI_01488_.webp',
    'ComfyUI_01516_.webp',
    'ComfyUI_01542_.webp',
    'ComfyUI_01584_.webp',
    'ComfyUI_01620_.webp',
    'ComfyUI_01633_.webp',
    'ComfyUI_01634_.webp',
    'ComfyUI_01637_.webp',
    'ComfyUI_01640_.webp',
    'ComfyUI_01650_.webp',
    'ComfyUI_01654_.webp',
    'ComfyUI_01657_.webp',
    'ComfyUI_01664_.webp',
    'ComfyUI_01666_.webp',
    'ComfyUI_01672_.webp',
    'ComfyUI_01676_.webp',
    'ComfyUI_01685_.webp',
    'ComfyUI_01687_.webp',
    'ComfyUI_01716_.webp',
    'ComfyUI_01736_.webp',
    'ComfyUI_01749_.webp',
    'ComfyUI_01750_.webp',
    'ComfyUI_01777_.webp',
    'ComfyUI_01778_.webp',
    'ComfyUI_01787_.webp',
    'ComfyUI_01791_.webp',
    'ComfyUI_01819_.webp',
    'ComfyUI_01906_.webp',
    'ComfyUI_01919_.webp',
    'ComfyUI_01921_.webp',
    'ComfyUI_01932_.webp',
    'ComfyUI_01945_.webp',
    'ComfyUI_01961_.webp',
    'ComfyUI_01963_.webp',
    'ComfyUI_01964_.webp',
    'ComfyUI_01965_.webp',
    'ComfyUI_01966_.webp',
    'ComfyUI_01967_.webp',
    'ComfyUI_01975_.webp',
    'ComfyUI_01976_.webp',
    'ComfyUI_01977_.webp',
    'ComfyUI_01982_.webp',
    'ComfyUI_01984_.webp',
    'ComfyUI_01986_.webp',
    'ComfyUI_01990_.webp',
    'ComfyUI_01993_.webp',
    'ComfyUI_01997_.webp',
    'ComfyUI_01998_.webp',
    'ComfyUI_02007_.webp',
    'ComfyUI_02010_.webp',
    'ComfyUI_02011_.webp',
    'ComfyUI_02013_.webp',
    'ComfyUI_02014_.webp',
    'ComfyUI_02015_.webp',
    'ComfyUI_02017_.webp',
    'ComfyUI_02021_.webp',
    'ComfyUI_02022_.webp',
    'ComfyUI_02028_.webp',
    'ComfyUI_02035_.webp',
    'ComfyUI_02036_.webp',
    'ComfyUI_02036_1.webp',
    'ComfyUI_02037_.webp',
    'ComfyUI_02038_.webp',
    'ComfyUI_02038_1.webp',
    'ComfyUI_02055_.webp',
    'ComfyUI_02059_.webp',
    'ComfyUI_02062_.webp',
    'ComfyUI_02073_.webp',
    'ComfyUI_02074_.webp',
    'ComfyUI_02076_.webp',
    'ComfyUI_02080_.webp',
    'ComfyUI_02081_.webp',
    'ComfyUI_02085_.webp',
    'ComfyUI_02086_.webp',
    'ComfyUI_02091_.webp',
    'ComfyUI_02093_.webp',
    'ComfyUI_02095_.webp',
    'ComfyUI_02096_.webp',
    'ComfyUI_02103_.webp',
    'ComfyUI_02104_.webp',
    'ComfyUI_02109_.webp',
    'ComfyUI_02110_.webp',
    'ComfyUI_02117_.webp',
    'ComfyUI_02124_.webp',
    'ComfyUI_02129_.webp',
    'ComfyUI_02132_.webp',
    'ComfyUI_02135_.webp',
    'ComfyUI_02136_.webp',
    'ComfyUI_02138_.webp',
    'ComfyUI_02140_.webp',
    'ComfyUI_02143_.webp',
    'ComfyUI_02153_.webp',
    'ComfyUI_02156_.webp',
    'ComfyUI_02160_.webp',
    'ComfyUI_02164_.webp',
    'ComfyUI_02165_.webp',
    'ComfyUI_02186_.webp',
    'ComfyUI_02192_.webp',
    'ComfyUI_02193_.webp',
    'ComfyUI_02225_.webp',
    'ComfyUI_02269_.webp',
    'ComfyUI_02318_.webp',
    'ComfyUI_02408_.webp',
    'ComfyUI_02417_.webp',
    'ComfyUI_02424_.webp',
    'ComfyUI_02435_.webp',
    'ComfyUI_02465_.webp',
    'ComfyUI_02488_.webp',
    'ComfyUI_02489_.webp',
    'ComfyUI_02513_.webp',
    'ComfyUI_02532_.webp',
    'ComfyUI_02702_.webp',
    'ComfyUI_02719_.webp',
    'ComfyUI_027215_j.webp',
    'ComfyUI_02829_.webp',
    'ComfyUI_02841_.webp',
    'ComfyUI_02850_.webp',
    'ComfyUI_02859_.webp',
    'ComfyUI_02862_.webp',
    'ComfyUI_02863_.webp',
    'ComfyUI_02864_.webp',
    'ComfyUI_02879_.webp',
    'ComfyUI_02887_.webp',
    'ComfyUI_03042_.webp',
    'ComfyUI_03045_.webp',
    'ComfyUI_03048_.webp',
    'ComfyUI_03062_.webp',
    'ComfyUI_03080_.webp',
    'ComfyUI_03123_.webp',
    'ComfyUI_03130_.webp',
    'ComfyUI_03135_.webp',
    'ComfyUI_03153_.webp',
    'ComfyUI_03235_.webp',
    'ComfyUI_03290_.webp',
    'ComfyUI_03330_.webp',
    'ComfyUI_03343_.webp',
    'ComfyUI_03347_.webp',
    'ComfyUI_03350_.webp',
    'ComfyUI_03378_.webp',
    'ComfyUI_03474_.webp',
    'ComfyUI_03501_.webp',
    'ComfyUI_03520_.webp',
    'ComfyUI_03532_.webp',
    'ComfyUI_03565_.webp',
    'ComfyUI_03600_.webp',
    'ComfyUI_03616_.webp',
    'ComfyUI_03677_.webp',
    'ComfyUI_03737_.webp',
    'ComfyUI_03856_.webp',
    'ComfyUI_03879_.webp',
    'ComfyUI_03910_.webp',
    'ComfyUI_03990_.webp',
    'ComfyUI_04076_.webp',
    'ComfyUI_04089_.webp',
    'ComfyUI_04114_.webp',
    'ComfyUI_04158_.webp',
    'ComfyUI_04165_.webp',
    'ComfyUI_04170_.webp',
    'ComfyUI_04171_.webp',
    'ComfyUI_04173_.webp',
    'ComfyUI_04174_.webp',
    'ComfyUI_04182_.webp',
    'ComfyUI_04200_.webp',
    'ComfyUI_04224_.webp',
    'ComfyUI_04267_.webp',
    'ComfyUI_04279_.webp',
    'ComfyUI_04315_.webp',
    'ComfyUI_04321_.webp',
    'ComfyUI_04326_.webp',
    'ComfyUI_04376_.webp',
    'ComfyUI_04383_.webp',
    'ComfyUI_04394_.webp',
    'ComfyUI_04395_.webp',
    'ComfyUI_04399_.webp',
    'ComfyUI_04529_.webp',
    'ComfyUI_04535_.webp',
    'ComfyUI_04540_.webp',
    'ComfyUI_04541_.webp',
    'ComfyUI_04542_.webp',
    'ComfyUI_04554_.webp',
    'ComfyUI_04556_.webp',
    'ComfyUI_04559_.webp',
    'ComfyUI_04568_.webp',
    'ComfyUI_04590_.webp',
    'ComfyUI_04593_.webp',
    'ComfyUI_04610_.webp',
    'ComfyUI_04613_.webp',
    'ComfyUI_04614_.webp',
    'ComfyUI_04627_.webp',
    'ComfyUI_04628_.webp',
    'ComfyUI_04695_.webp',
    'ComfyUI_04711_.webp',
    'ComfyUI_04730_.webp',
    'ComfyUI_04736_.webp',
    'ComfyUI_04752_.webp',
    'ComfyUI_04753_.webp',
    'ComfyUI_04781_.webp',
    'ComfyUI_04784_.webp',
    'ComfyUI_04785_.webp',
    'ComfyUI_04815_.webp',
    'ComfyUI_04816_.webp',
    'ComfyUI_04871_.webp',
    'ComfyUI_04906_.webp',
    'ComfyUI_04929_.webp',
    'ComfyUI_04951_.webp',
    'ComfyUI_04960_.webp',
    'ComfyUI_04963_.webp',
    'ComfyUI_04970_.webp',
    'ComfyUI_05019_.webp',
    'ComfyUI_05023_.webp',
];

// Warm a RAM cache of the Digital Fire Fighters images (the heaviest skin, ~387
// images) so it paints without decode flicker once the cache fills. Delegates to
// the shared preloader, which holds the Image refs resident and counts the images
// toward the startup splash dismissal. Idempotent.
function preloadFirefightersToRAM() {
    preloadSkinImagesToRAM('ff', 'DigitalFireFighterImages/', FIREFIGHTER_IMAGES);
}
// Warm the cache when Digital Fire Fighters is the active skin — either explicitly
// saved, OR not yet chosen, because DFF is now the default skin for new users (see
// _currentSkin in Dashboard.razor / Preferences.razor). Gives its first paint a
// head start without holding ~645MB for users who picked a different skin.
// Switching to the skin at runtime also kicks this off (in paintDigitalFireFighters).
try {
    const savedSkin = localStorage.getItem('dashboard_skin');
    if (savedSkin === 'digital-fire-fighters' || !savedSkin) {
        setTimeout(preloadFirefightersToRAM, 300);
    }
} catch (e) { /* localStorage unavailable */ }

// "Digital Fire Fighters" skin: rows of ~330px firefighter tiles scrolling
// sideways in alternating directions, plus 16 more images orbiting the centre at 22.5°
// intervals on a 20s linear loop (each with a staggered animation-delay
// so the orbit looks like a constant procession rather than a synchronised
// ring). Ported from Blazecoin_MVC_Web_App/Views/Home/Index.cshtml.
function paintDigitalFireFighters(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadFirefightersToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const shuffled = shuffle(FIREFIGHTER_IMAGES.slice());
    const orbitImages = shuffled.slice(0, 16);   // first 16 → the orbit
    const bgPool = shuffled.slice(16);           // the rest → the bg, disjoint from the
                                                 // orbit so no image appears in both

    // Background = horizontal rows of ~330px firefighter tiles that scroll
    // sideways, alternating direction (row 1 right, row 2 left, row 3 right, …).
    // Each row builds one set of tiles then an exact clone of that set, and
    // scrolls by precisely one set width, so the loop is seamless — the incoming
    // clones line up on the outgoing originals (same fix as the Shield skin).
    // Fixed ~330px tiles so the firefighter images keep the SAME apparent size
    // at any window size — maximising adds rows/columns rather than shrinking
    // tiles. Rows divide the height with round() (so rowH stays ≈ TILE and still
    // fills exactly, no black gap); width uses the fixed tile and scrolls, with
    // enough tiles per set to cover the viewport plus a small buffer.
    const TILE = 330;
    const rows = Math.max(3, Math.round(window.innerHeight / TILE));
    const rowH = window.innerHeight / rows;            // ≈ TILE, fills height exactly
    const tileW = TILE;                                 // fixed → consistent image size
    const perSet = Math.ceil(window.innerWidth / tileW) + 2;
    const setW = perSet * tileW;
    const dur = (setW / 32).toFixed(1);                // ~32px/s scroll speed

    const bg = document.createElement('div');
    bg.className = 'firefighter-bg';

    let imgIdx = 0;
    for (let r = 0; r < rows; r++) {
        const row = document.createElement('div');
        row.className = 'firefighter-row';
        row.style.height = rowH + 'px';
        row.style.setProperty('--ff-set', setW + 'px');
        row.style.animationName = (r % 2 === 0) ? 'ffScrollRight' : 'ffScrollLeft';
        row.style.animationDuration = dur + 's';
        for (let c = 0; c < perSet; c++) {
            const cell = document.createElement('div');
            cell.className = 'firefighter-bg-img';
            cell.style.width = tileW + 'px';
            const img = document.createElement('img');
            img.src = 'DigitalFireFighterImages/' + bgPool[(imgIdx++) % bgPool.length];
            img.alt = '';
            cell.appendChild(img);
            row.appendChild(cell);
        }
        // duplicate the set as exact clones so the one-set scroll loops seamlessly
        const set = Array.from(row.children);
        for (const c of set) row.appendChild(c.cloneNode(true));
        bg.appendChild(row);
    }
    host.appendChild(bg);

    const orbit = document.createElement('div');
    orbit.className = 'firefighter-orbit';
    for (let i = 0; i < orbitImages.length; i++) {
        const img = document.createElement('img');
        img.src = 'DigitalFireFighterImages/' + orbitImages[i];
        img.className = 'orbit';
        img.alt = '';
        orbit.appendChild(img);
    }
    host.appendChild(orbit);
}

const WAVES_IMAGES = [
    'ComfyUI_09077_.webp', 'ComfyUI_09105_.webp', 'ComfyUI_09327_.webp', 'ComfyUI_09776_.webp',
    'ComfyUI_09975_.webp', 'ComfyUI_10113_.webp', 'ComfyUI_10374_.webp', 'ComfyUI_10463_.webp',
    'ComfyUI_10468_.webp', 'ComfyUI_10504_.webp', 'ComfyUI_10505_.webp', 'ComfyUI_10555_.webp',
    'ComfyUI_10564_.webp', 'ComfyUI_10720_.webp', 'ComfyUI_11011_.webp', 'ComfyUI_11202_.webp',
    'ComfyUI_11212_.webp', 'ComfyUI_11344_.webp', 'ComfyUI_11355_.webp', 'ComfyUI_11356_.webp',
    'ComfyUI_11371_.webp', 'ComfyUI_11372_.webp', 'ComfyUI_11403_.webp', 'ComfyUI_11425_.webp',
    'ComfyUI_11455_.webp', 'ComfyUI_11471_.webp', 'ComfyUI_11474_.webp', 'ComfyUI_11505_.webp',
    'ComfyUI_11513_.webp', 'ComfyUI_11519_.webp', 'ComfyUI_11531_.webp', 'ComfyUI_11544_.webp',
    'ComfyUI_11572_.webp'
];

// Warm the HODL Waves skin's image set. Idempotent.
function preloadWavesToRAM() {
    preloadSkinImagesToRAM('waves', 'WavesImages/', WAVES_IMAGES);
}
// Only warm the cache when the HODL Waves skin is the saved dashboard choice;
// a runtime switch warms it via the paint function below.
try {
    if (localStorage.getItem('dashboard_skin') === 'hodl-waves') {
        setTimeout(preloadWavesToRAM, 300);
    }
} catch (e) { /* localStorage unavailable */ }

// "HODL Waves" skin: 4x3 grid of 12 WavesImages tiles with a VHS-style
// intro — cyan neon glow that pulses ~9 times, horizontal scanlines, and
// a tracking-line glitch sliding down each tile. All the VHS effects fade
// out over 36s, leaving a clean image grid. Ported from
// Blazecoin_MVC_Web_App/Views/Hodl/Index.cshtml.
function paintHodlWavesTiles(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadWavesToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const shuffled = shuffle(WAVES_IMAGES.slice());
    for (let k = 0; k < 12; k++) {
        const tile = document.createElement('div');
        tile.className = 'vhs-tile';
        const img = document.createElement('img');
        img.src = 'WavesImages/' + shuffled[k];
        img.alt = '';
        tile.appendChild(img);
        host.appendChild(tile);
    }
}

const NETWORK_IMAGES = [
    'ComfyUI_02733_.webp','ComfyUI_03100_.webp','ComfyUI_03116_.webp','ComfyUI_03283_.webp',
    'ComfyUI_03426_.webp','ComfyUI_03468_.webp','ComfyUI_03583_.webp','ComfyUI_03683_.webp',
    'ComfyUI_03693_.webp','ComfyUI_03707_.webp','ComfyUI_03751_.webp','ComfyUI_03752_.webp',
    'ComfyUI_03827_.webp','ComfyUI_03946_.webp','ComfyUI_04099_.webp','ComfyUI_04125_.webp',
    'ComfyUI_04168_.webp','ComfyUI_04172_.webp','ComfyUI_04261_.webp','ComfyUI_04357_.webp',
    'ComfyUI_04385_.webp','ComfyUI_04488_.webp','ComfyUI_04550_.webp','ComfyUI_04703_.webp',
    'ComfyUI_04771_.webp','ComfyUI_04848_.webp','ComfyUI_04867_.webp','ComfyUI_04868_.webp',
    'ComfyUI_04891_.webp','ComfyUI_04896_.webp','ComfyUI_04957_.webp','ComfyUI_04973_.webp',
    'ComfyUI_05066_.webp','ComfyUI_05104_.webp','ComfyUI_05140_.webp','ComfyUI_05162_.webp',
    'ComfyUI_05207_.webp','ComfyUI_05211_.webp','ComfyUI_05259_.webp','ComfyUI_05260_.webp',
    'ComfyUI_05274_.webp','ComfyUI_05343_.webp','ComfyUI_05344_.webp','ComfyUI_05347_.webp',
    'ComfyUI_05352_.webp','ComfyUI_05402_.webp','ComfyUI_05403_.webp','ComfyUI_05404_.webp',
    'ComfyUI_05414_.webp','ComfyUI_05427_.webp','ComfyUI_05431_.webp','ComfyUI_05538_.webp',
    'ComfyUI_05655_.webp','ComfyUI_05704_.webp','ComfyUI_05769_.webp','ComfyUI_05771_.webp',
    'ComfyUI_05779_.webp','ComfyUI_05788_.webp','ComfyUI_05848_.webp','ComfyUI_05863_.webp',
    'ComfyUI_05866_.webp','ComfyUI_05872_.webp','ComfyUI_05904_.webp','ComfyUI_05928_.webp',
    'ComfyUI_05929_.webp','ComfyUI_05944_.webp','ComfyUI_05992_.webp','ComfyUI_06016_.webp',
    'ComfyUI_06061_.webp','ComfyUI_06074_.webp','ComfyUI_06081_.webp','ComfyUI_06138_.webp',
    'ComfyUI_06206_.webp','ComfyUI_06305_.webp','ComfyUI_06306_.webp','ComfyUI_06349_.webp',
    'ComfyUI_06402_.webp','ComfyUI_06421_.webp','ComfyUI_06425_.webp','ComfyUI_06458_.webp',
    'ComfyUI_06483_.webp','ComfyUI_06484_.webp','ComfyUI_06507_.webp','ComfyUI_06514_.webp',
    'ComfyUI_06516_.webp','ComfyUI_06521_.webp','ComfyUI_06588_.webp','ComfyUI_06621_.webp',
    'ComfyUI_06629_.webp','ComfyUI_06639_.webp','ComfyUI_06642_.webp','ComfyUI_06643_.webp',
    'ComfyUI_06666_.webp','ComfyUI_06669_.webp','ComfyUI_06670_.webp','ComfyUI_06675_.webp',
    'ComfyUI_06686_.webp','ComfyUI_06690_.webp','ComfyUI_06765_.webp','ComfyUI_06771_.webp',
    'ComfyUI_06774_.webp','ComfyUI_06802_.webp','ComfyUI_06803_.webp','ComfyUI_06847_.webp',
    'ComfyUI_06905_.webp','ComfyUI_06906_.webp','ComfyUI_06921_.webp','ComfyUI_06922_.webp',
    'ComfyUI_06931_.webp'
];

// Generic RAM preloader — generalises preloadFirefightersToRAM so any
// image-heavy skin can warm a background cache of its images and paint without
// first-paint decode flicker. Holding the Image refs keeps the bytes resident
// in memory; decode() warms the bitmap cache. A few per tick avoids blocking
// the main thread. Idempotent per cacheKey (re-calls after the first are no-ops).
// Gate each caller so only the active skin's images are held (see the network
// gate below) — don't warm every skin's set at once or memory balloons.
function preloadSkinImagesToRAM(cacheKey, basePath, images) {
    const startedKey = '__preloadStarted_' + cacheKey;
    const cacheRef = '__preloadCache_' + cacheKey;
    if (window[startedKey]) return;
    window[startedKey] = true;
    window[cacheRef] = window[cacheRef] || [];
    const cache = window[cacheRef];
    splashTrackTotal(images.length);   // count toward the startup splash dismissal
    let i = 0;
    (function next() {
        for (let k = 0; k < 6 && i < images.length; k++, i++) {
            const im = new Image();
            im.src = basePath + images[i];
            cache.push(im);             // keep a ref so it stays in RAM
            im.decode().then(splashTrackOne, splashTrackOne);  // report decode (ok or fail)
        }
        if (i < images.length) setTimeout(next, 25);
    })();
}

// Warm the Network skin's image set. Idempotent.
function preloadNetworkToRAM() {
    preloadSkinImagesToRAM('net', 'NetworkImages/', NETWORK_IMAGES);
}
// Network is the Console page's permanent background and Console is a sidebar
// destination hit most sessions, so warm it UNCONDITIONALLY at startup (not gated
// on the saved dashboard skin) and keep the cache constantly resident. Staggered
// a little after the active dashboard skin's own warm (300ms) so the visible skin
// paints first. Idempotent, so the paintNetworkColumns call below is a no-op once
// this has run. (~186MB of image bytes held for the session — accepted because
// the Console background is used every session.)
setTimeout(preloadNetworkToRAM, 1000);

// "Network" skin: 8 vertical columns of NetworkImages scrolling up/down on
// alternating columns (20s linear loop). A handful of tiles in each column
// have rare glitch jitters (3 variants, mostly idle then a single-frame
// blink). The container has a scanline overlay and a horizontal green
// "radar" sweep. Ported from Blazecoin_MVC_Web_App/Views/Network/Index.cshtml.
function paintNetworkColumns(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadNetworkToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const shuffled = shuffle(NETWORK_IMAGES.slice());
    const picked = shuffled.slice(0, 32); // 8 cols * 4 unique per col
    const cols = 8;
    const perCol = 4;

    for (let c = 0; c < cols; c++) {
        const col = document.createElement('div');
        col.className = 'tile-col';
        const colImages = picked.slice(c * perCol, c * perCol + perCol);
        // Original set + duplicate of the same set so the scroll loops seamlessly.
        for (let r = 0; r < 2; r++) {
            for (let i = 0; i < colImages.length; i++) {
                const img = document.createElement('img');
                img.src = 'NetworkImages/' + colImages[i];
                img.alt = '';
                col.appendChild(img);
            }
        }
        host.appendChild(col);
    }
}

const GLASS_CHAINS_IMAGES = [
    'ComfyUI_02591_.webp', 'ComfyUI_02592_.webp', 'ComfyUI_02594_.webp', 'ComfyUI_02599_.webp',
    'ComfyUI_02601_.webp', 'ComfyUI_02617_.webp', 'ComfyUI_02619_.webp', 'ComfyUI_02620_.webp',
    'ComfyUI_02622_.webp', 'ComfyUI_02628_.webp', 'ComfyUI_02634_.webp', 'ComfyUI_02635_.webp',
    'ComfyUI_02648_.webp', 'ComfyUI_02654_.webp', 'ComfyUI_02657_.webp', 'ComfyUI_02658_.webp',
    'ComfyUI_02662_.webp', 'ComfyUI_03215_.webp', 'ComfyUI_03220_.webp', 'ComfyUI_03343_.webp',
    'ComfyUI_03540_.webp', 'ComfyUI_03761_.webp', 'ComfyUI_03934_.webp', 'ComfyUI_05379_.webp',
    'ComfyUI_05382_.webp', 'ComfyUI_05448_.webp', 'ComfyUI_05548_.webp', 'ComfyUI_05565_.webp',
    'ComfyUI_05570_.webp', 'ComfyUI_05571_.webp', 'ComfyUI_05574_.webp', 'ComfyUI_05595_.webp',
    'ComfyUI_05597_.webp', 'ComfyUI_05657_.webp', 'ComfyUI_05708_.webp', 'ComfyUI_06043_.webp',
    'ComfyUI_06123_.webp', 'ComfyUI_06325_.webp', 'ComfyUI_06405_.webp', 'ComfyUI_06423_.webp',
    'ComfyUI_06427_.webp', 'ComfyUI_06432_.webp', 'ComfyUI_06452_.webp', 'ComfyUI_06457_.webp',
    'ComfyUI_06496_.webp', 'ComfyUI_06503_.webp', 'ComfyUI_06544_.webp', 'ComfyUI_06568_.webp',
    'ComfyUI_06592_.webp', 'ComfyUI_06602_.webp', 'ComfyUI_06614_.webp', 'ComfyUI_06621_.webp',
    'ComfyUI_06639_.webp', 'ComfyUI_06656_.webp', 'ComfyUI_06657_.webp', 'ComfyUI_06664_.webp',
    'ComfyUI_06688_.webp', 'ComfyUI_06710_.webp', 'ComfyUI_06736_.webp', 'ComfyUI_06739_.webp',
    'ComfyUI_06761_.webp'
];

// Warm the Blockchain skin's image set. Idempotent. (These load as CSS
// background-images; preloading still warms the disk/decode cache so the
// quadrants paint without first-show flicker.)
function preloadBlockchainToRAM() {
    preloadSkinImagesToRAM('glasschains', 'GlassChainsImage/', GLASS_CHAINS_IMAGES);
}
// Only warm the cache when the Blockchain skin is the saved dashboard choice;
// a runtime switch warms it via the paint function below.
try {
    if (localStorage.getItem('dashboard_skin') === 'blockchain') {
        setTimeout(preloadBlockchainToRAM, 300);
    }
} catch (e) { /* localStorage unavailable */ }

// "Blockchain" skin: 3x2 grid of quadrants, each with a static under-image
// (dimmed) and a slowly-rotating over-image. The 6 over-images are
// staggered through a 60s rotation cycle (-0s, -10s, -20s, -30s, -40s, -50s)
// so each quadrant is at a different angle at any moment. Ported from
// Blazecoin_MVC_Web_App/Views/BlazecoinExplorer/Index.cshtml.
function paintBlockchainGrid(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadBlockchainToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const shuffled = shuffle(GLASS_CHAINS_IMAGES.slice());
    // Need 12 (6 rotating + 6 static); pool has 61 so this is comfortably unique.
    const positions = [
        { top: '0',     left: '0' },
        { top: '0',     left: '33.33%' },
        { top: '0',     left: '66.66%' },
        { top: '50%',   left: '0' },
        { top: '50%',   left: '33.33%' },
        { top: '50%',   left: '66.66%' }
    ];

    for (let i = 0; i < 6; i++) {
        const quadrant = document.createElement('div');
        quadrant.className = 'explorer-bg-quadrant';
        quadrant.style.top = positions[i].top;
        quadrant.style.left = positions[i].left;

        const staticLayer = document.createElement('div');
        staticLayer.className = 'explorer-bg-static';
        staticLayer.style.backgroundImage = `url('GlassChainsImage/${shuffled[6 + i]}')`;
        quadrant.appendChild(staticLayer);

        const rotatingLayer = document.createElement('div');
        rotatingLayer.className = 'explorer-bg-image';
        rotatingLayer.style.backgroundImage = `url('GlassChainsImage/${shuffled[i]}')`;
        // 60s cycle, -0s..-50s offsets so the six quadrants spread across it.
        rotatingLayer.style.animationDelay = (-i * 10) + 's';
        quadrant.appendChild(rotatingLayer);

        host.appendChild(quadrant);
    }
}

// Warm the Phoenix skin's image set. Idempotent. (Largest set — ~136 images —
// so the head start matters most here.)
function preloadPhoenixToRAM() {
    preloadSkinImagesToRAM('bird', 'BlazecoinBird/', BIRD_IMAGES);
}
// Only warm the cache when the Phoenix skin is the saved dashboard choice;
// a runtime switch warms it via the paint function below.
try {
    if (localStorage.getItem('dashboard_skin') === 'phoenix') {
        setTimeout(preloadPhoenixToRAM, 300);
    }
} catch (e) { /* localStorage unavailable */ }

// "Phoenix" skin: 8x8 grid of BlazecoinBird images cascading diagonally
// (fade-in, hold, fade-out, pause on an 8s loop with row+col staggering)
// while each image shimmers (brightness/saturate pulse on a 6s loop with
// per-tile delay). Ported from Blazecoin_MVC_Web_App/Views/Home/About.cshtml,
// but dimmed so wallet content stays legible above the background.
function paintCascadeGrid(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadPhoenixToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    // Shuffle the 136 available images, take 64 unique for the 8x8 grid.
    // If the pool is ever smaller than 64, shuffle-and-repeat to fill.
    let selected = shuffle(BIRD_IMAGES.slice());
    while (selected.length < 64) {
        selected = selected.concat(shuffle(BIRD_IMAGES.slice()));
    }
    selected = selected.slice(0, 64);

    for (let i = 0; i < 64; i++) {
        const row = Math.floor(i / 8);
        const col = i % 8;
        const staggerDelay = ((row + col) * 0.15).toFixed(2);
        const shimmerDelay = ((i * 0.47) % 6.0).toFixed(2);

        const cell = document.createElement('div');
        cell.className = 'bg-img';
        cell.style.animationDelay = staggerDelay + 's';
        cell.style.setProperty('--shimmer-delay', shimmerDelay + 's');

        const img = document.createElement('img');
        img.src = 'BlazecoinBird/' + selected[i];
        img.alt = '';
        cell.appendChild(img);
        host.appendChild(cell);
    }
}

// "413" skin: brick-pattern grid where every tile is the same `413 Vert.png`,
// scrolling sideways with alternating row direction. Bigger tiles than the
// Mining page (cols=6, tileW=50vw vs cols=10, tileW=25vw) per
// Blazecoin_MVC_Web_App/Views/Supply/Index.cshtml.
function paint413Tiles(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    const cols = 6;
    const tileW = window.innerWidth / 2;
    const tileH = tileW * (1024 / 1536);
    const rows = Math.ceil(window.innerHeight / tileH) + 2;

    for (let r = 0; r < rows; r++) {
        const row = document.createElement('div');
        row.className = 'tile-row';
        for (let c = 0; c < cols; c++) {
            const img = document.createElement('img');
            img.src = 'Images/413/413-Vert.png';
            img.alt = '';
            row.appendChild(img);
        }
        host.appendChild(row);
    }
}

// "Shield" skin: brick-pattern grid where every tile is the BlazeCoin shield
// emblem (wwwroot/shield-tile.png — the splash image with the "BlazeCoin"
// wordmark cropped off; the splash page still uses the full splash-shield.png).
// Tiles scroll sideways with alternating row direction and ~1 in 4 do an
// occasional 3D flip — see .skin-shield in Dashboard.razor.css.
function paintShieldTiles(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    const cols = 8;                          // 8 per row covers ~5.5 visible + scroll/brick buffer
    const tileW = window.innerWidth * 0.18;  // 18vw tiles — same shield size as 25vw, tighter L/R spacing
    const tileH = tileW * (400 / 368);       // shield-tile.png is 368x400
    const rows = Math.ceil(window.innerHeight / tileH) + 2;

    for (let r = 0; r < rows; r++) {
        const row = document.createElement('div');
        row.className = 'tile-row';
        for (let c = 0; c < cols; c++) {
            const tile = document.createElement('div');
            tile.className = 'shield-tile';
            // ~1 in 4 tiles does an occasional 3D flip, each on a random
            // phase (negative delay) so the flips fire sporadically rather
            // than in unison. 16s matches the shieldFlip cycle in the CSS.
            if (Math.random() < 0.25) {
                tile.classList.add('flip');
                tile.style.animationDelay = (-Math.random() * 16).toFixed(2) + 's';
            }
            const img = document.createElement('img');
            img.src = 'shield-tile.png';
            img.alt = '';
            // Independently of the flip, ~1 in 5 tiles slowly pulse their
            // opacity (a slow ~18s fade out and back), each on a random phase
            // so they breathe out of sync. The opacity pulse rides the inner
            // <img> (opacity isn't a transform, so it won't fight the flip on
            // the wrapper or the scale-breathe on the other tiles). The delay
            // range matches the 18s cycle so phases spread across the loop.
            if (Math.random() < 0.2) {
                tile.classList.add('opulse');
                img.style.animationDelay = (-Math.random() * 18).toFixed(2) + 's';
            }
            tile.appendChild(img);
            row.appendChild(tile);
        }
        // Duplicate the row's tile set as exact clones — cloneNode(true) copies
        // each tile's .flip/.opulse classes AND its inline animation-delay, so a
        // clone stays in the same flip/opacity phase as its original forever.
        // The scroll translates by one FULL set width (see scrollRightShield:
        // 160vw = cols(8) × 20vw pitch), so at the loop reset the incoming clones
        // line up exactly with the outgoing originals → seamless. Translating a
        // single tile pitch instead shifts every tile one position at the reset,
        // and since tiles carry distinct states that shift shows as a global jar.
        const set = Array.from(row.children);
        for (const t of set) row.appendChild(t.cloneNode(true));
        host.appendChild(row);
    }
}

function paintCoinGrid(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    for (let i = 0; i < 100; i++) {
        const row = Math.floor(i / 10);
        const col = i % 10;
        const cell = document.createElement('div');
        cell.className = 'coin-cell';
        if (row % 2 === 1) cell.classList.add('stagger');
        if ((row + col) % 2 === 1) cell.classList.add('flip');
        const canvas = document.createElement('canvas');
        canvas.className = 'pixel-coin';
        canvas.width = 32;
        canvas.height = 32;
        cell.appendChild(canvas);
        host.appendChild(cell);
    }

    // Mirror horizontally so the bird faces right rather than left.
    // We mirror in the canvas (not via CSS) because the .coin-cell canvas
    // has a rotateY animation that would clobber any CSS scaleX(-1).
    const SIZE = 48;
    const img = new Image();
    img.src = 'BlazecoinBird/ComfyUI_02889_.webp';
    img.onload = function () {
        host.querySelectorAll('.pixel-coin').forEach(c => drawMirroredCoin(c, img, SIZE));
    };
}

function drawMirroredCoin(canvas, img, size) {
    canvas.width = size;
    canvas.height = size;
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = false;
    ctx.save();
    ctx.scale(-1, 1);
    ctx.drawImage(img, -size, 0, size, size);
    ctx.restore();
}

// Reveals the Dashboard banner text one character at a time on mount.
// Idempotent: navigating away and back re-plays the typing (the full
// string is stashed in dataset so we can reset). The caret blinks while
// typing and fades out a moment after the last char lands.
window.typeBlazecoinBanner = function () {
    const el = document.querySelector('.banner-text');
    if (!el) return;
    const caret = document.querySelector('.banner-caret');
    const fullText = el.dataset.fullText || el.textContent.trim();
    el.dataset.fullText = fullText;
    el.textContent = '';
    if (caret) {
        caret.style.opacity = '1';
        caret.style.animation = 'banner-caret-blink 0.7s steps(1) infinite';
    }

    if (window.__bannerTypeTimer) clearInterval(window.__bannerTypeTimer);
    let i = 0;
    window.__bannerTypeTimer = setInterval(() => {
        if (i >= fullText.length) {
            clearInterval(window.__bannerTypeTimer);
            window.__bannerTypeTimer = null;
            if (caret) {
                setTimeout(() => {
                    caret.style.animation = 'none';
                    caret.style.opacity = '0';
                }, 800);
            }
            return;
        }
        el.textContent += fullText[i++];
    }, 95);
};

// The PROVENANCE banner: an odometer that spins the chain height up from zero
// to the current tip, holds on the arrival figure, then becomes the page title.
// Ties the header to the "attested to block N" line below it — the page's whole
// job is saying which blocks minted these coins.
//
// Leading zeros are deliberate: they hold the string at its final digit count
// from the first frame, so the flanking marks don't slide inward and outward
// while the number grows. A tip of 0 (daemon unreachable) skips straight to the
// title rather than counting to nothing.
//
// Called repeatedly — the page re-invokes it on a timer with a freshly read tip,
// which is why the first thing it does is cancel whatever pass is still running.
window.countProvenanceBanner = function (tip) {
    const el = document.querySelector('.banner-text');
    if (!el) return;
    const title = el.dataset.fullText || el.textContent.trim();
    el.dataset.fullText = title;

    if (window.__provCountRaf) { cancelAnimationFrame(window.__provCountRaf); window.__provCountRaf = null; }
    if (window.__provCountHold) { clearTimeout(window.__provCountHold); window.__provCountHold = null; }
    el.classList.remove('banner-fading', 'banner-arrive');   // re-entry: never start mid-handover

    // The handover: the digits blur away, then the title resolves in out of a
    // soft focus. Transform-only (no letter-spacing), so nothing about the
    // arrival changes the string's LAYOUT width and the flanking marks hold
    // still through it. Unanimated when there was no count to hand over from.
    const showTitle = () => {
        el.classList.remove('banner-counting', 'banner-fading');
        el.textContent = title;
    };
    const settle = () => {
        el.classList.add('banner-fading');
        window.__provCountHold = setTimeout(() => {
            showTitle();
            el.classList.add('banner-arrive');
            window.__provCountHold = setTimeout(() => el.classList.remove('banner-arrive'), 1000);
        }, 320);
    };

    tip = Math.max(0, Math.floor(Number(tip) || 0));
    if (!tip) { showTitle(); return; }

    const digits = String(tip).length;
    const show = n => String(n).padStart(digits, '0').replace(/\B(?=(\d{3})+(?!\d))/g, ',');

    el.classList.add('banner-counting');
    el.textContent = show(0);

    const DURATION = 4500;   // long enough to read the height climbing, not just blur past
    let start = null;
    const step = ts => {
        if (start === null) start = ts;
        const t = Math.min(1, (ts - start) / DURATION);
        const eased = 1 - Math.pow(1 - t, 3);   // ease-out: races away, then settles onto the tip
        el.textContent = show(Math.round(tip * eased));
        if (t < 1) { window.__provCountRaf = requestAnimationFrame(step); return; }
        window.__provCountRaf = null;
        window.__provCountHold = setTimeout(settle, 700);
    };
    window.__provCountRaf = requestAnimationFrame(step);
};

// Paints the two .header-coin canvases with the BlazecoinBird image,
// independent of which Dashboard skin is active. Without this, only the
// Pixelated Coins skin would draw them (as a side effect of paintCoinGrid)
// and switching to any other skin would leave the headers blank.
window.drawHeaderCoins = function () {
    const coins = document.querySelectorAll('.header-coin');
    if (coins.length === 0) return;
    if (coins[0].dataset.drawn === '1') return; // idempotent

    const SIZE = 48;
    const img = new Image();
    img.src = 'BlazecoinBird/ComfyUI_02889_.webp';
    img.onload = function () {
        document.querySelectorAll('.header-coin').forEach(c => {
            drawMirroredCoin(c, img, SIZE);
            c.dataset.drawn = '1';
        });
    };
};

// ---------- Dashboard skin registry ----------

// Each skin paints into the host element (`#dashboardBackground`). The
// host's class controls the layout/positioning rules (see
// Dashboard.razor.css `.skin-*` selectors). cleanup is handled centrally
// in applyDashboardSkin — skins only need to populate.

window.DashboardSkins = {
    'pixelated-coins': {
        id: 'pixelated-coins',
        label: 'Pixelated Coins',
        init(host) {
            host.className = 'skin-pixelated-coins';
            paintCoinGrid(host);
        }
    },
    'mining-pools': {
        id: 'mining-pools',
        label: 'Mining Pools',
        init(host) {
            host.className = 'skin-mining-pools';
            paintPoolGrid(host);
        }
    },
    '413': {
        id: '413',
        label: '413',
        init(host) {
            host.className = 'skin-413';
            paint413Tiles(host);
        }
    },
    'shield': {
        id: 'shield',
        label: 'Shield',
        init(host) {
            host.className = 'skin-shield';
            paintShieldTiles(host);
        }
    },
    'phoenix': {
        id: 'phoenix',
        label: 'Phoenix',
        init(host) {
            host.className = 'skin-phoenix';
            paintCascadeGrid(host);
        }
    },
    'blockchain': {
        id: 'blockchain',
        label: 'Blockchain',
        init(host) {
            host.className = 'skin-blockchain';
            paintBlockchainGrid(host);
        }
    },
    'network': {
        id: 'network',
        label: 'Network',
        init(host) {
            host.className = 'skin-network';
            paintNetworkColumns(host);
        }
    },
    'hodl-waves': {
        id: 'hodl-waves',
        label: 'HODL Waves',
        init(host) {
            host.className = 'skin-hodl-waves';
            paintHodlWavesTiles(host);
        }
    },
    'mining': {
        id: 'mining',
        label: 'Mining',
        init(host) {
            host.className = 'skin-mining';
            paintTileRows(host);
        }
    },
    'digital-fire-fighters': {
        id: 'digital-fire-fighters',
        label: 'Digital Fire Fighters',
        init(host) {
            host.className = 'skin-digital-fire-fighters';
            paintDigitalFireFighters(host);
        }
    },
    // The Send / Receive / Transactions page backgrounds, as dashboard skins.
    // They reuse the same painters + shared CoinPilesImages set.
    'coinpile-send': {
        id: 'coinpile-send',
        label: 'Coin Send',
        init(host) {
            host.className = 'skin-coinpile-send';
            paintCoinPileTiles(host);
        }
    },
    'coinpile-receive': {
        id: 'coinpile-receive',
        label: 'Coin Receive',
        init(host) {
            host.className = 'skin-coinpile-receive';
            paintCoinPileMosaic(host);
        }
    },
    'coinpile-transactions': {
        id: 'coinpile-transactions',
        label: 'Coin Transactions',
        init(host) {
            host.className = 'skin-coinpile-transactions';
            paintCoinPileDiagonal(host);
        }
    },
    'addressbook': {
        id: 'addressbook',
        label: 'Address Book',
        init(host) {
            host.className = 'skin-addressbook';
            paintAddressBookColumns(host);
        }
    },
    'security': {
        id: 'security',
        label: 'Security',
        init(host) {
            host.className = 'skin-security';
            paintSecurityGrid(host);
        }
    },
    'backup': {
        id: 'backup',
        label: 'Backup',
        init(host) {
            host.className = 'skin-backup';
            paintBackupDiagonal(host);
        }
    },
    'import': {
        id: 'import',
        label: 'Import',
        init(host) {
            host.className = 'skin-import';
            paintImportRings(host);
        }
    },
    'plain': {
        id: 'plain',
        label: 'Plain',
        init(host) {
            host.className = 'skin-plain';
        }
    }
};

let _activeSkinId = null;
let _activeHostId = null;
let _skinResizeTimer = null;

window.applyDashboardSkin = function (skinId, hostId) {
    const host = document.getElementById(hostId || 'dashboardBackground');
    if (!host) return;

    // Remember what's painted where, so a resize can repaint it (below).
    _activeSkinId = skinId;
    _activeHostId = host.id;

    // Tear down previous skin's contents and reset state. Clear the grid inline
    // styles the mosaic (coinpile-receive) painter sets on the host, so they
    // don't leak into the next skin (e.g. pixelated-coins' own grid).
    host.innerHTML = '';
    host.dataset.populated = '';
    host.className = '';
    host.style.gridTemplateColumns = '';
    host.style.gridAutoRows = '';

    const skin = window.DashboardSkins[skinId] || window.DashboardSkins['pixelated-coins'];
    skin.init(host);
};

// Repaint the active skin on resize (debounced). Skins size themselves to the
// window at paint time, so without this, maximising the window leaves the new
// area black (e.g. the firefighter rows didn't extend down to fill it). The
// repaint recomputes row/tile counts for the current size. Registered once.
window.addEventListener('resize', function () {
    if (!_activeSkinId) return;
    clearTimeout(_skinResizeTimer);
    _skinResizeTimer = setTimeout(function () {
        if (document.getElementById(_activeHostId)) {
            window.applyDashboardSkin(_activeSkinId, _activeHostId);
        }
    }, 150);
});

// Circular typographic scramble header: each character position cycles through
// random glyphs (rendered as inline red spans) then resolves to the target text,
// staggered per character, looping forever. Re-inits on return via the dataset
// guard; the loop exits when the element leaves the DOM.
window.initScrambleTitle = function (selector, finalText, tickMs, swap, holdMs) {
    const el = document.querySelector(selector);
    if (!el || el.dataset.scramble === '1') return;
    el.dataset.scramble = '1';
    // Lock the box to the resolved-text width so flanking content (e.g. the
    // Security keys) doesn't shift as variable-width glyphs cycle. inline-flex +
    // justify-content:center keeps the word centred even when scramble glyphs are
    // wider than the resolved text (text-align:center would shove it off to one
    // side instead of overflowing symmetrically).
    el.style.display = 'inline-flex';
    el.style.justifyContent = 'center';
    el.style.alignItems = 'baseline';
    el.style.width = el.offsetWidth + 'px';
    const glyphs = '!<>-_\\/[]{}=+*^?#%&@$0123456789';
    const dim = '#ff3b3b';   // theme red for the scrambling glyphs
    const TICK_MS = tickMs || 85;                            // ms between ticks (higher = slower)
    const SWAP = (typeof swap === 'number') ? swap : 0.45;   // glyph re-roll chance per tick
    const HOLD_MS = holdMs || 2800;                          // ms to hold the resolved word
    let queue = [], frame = 0, onDone = null;

    function scrambleTo(newText) {
        const oldText = el.textContent || '';
        const len = Math.max(oldText.length, newText.length);
        queue = [];
        for (let i = 0; i < len; i++) {
            const start = Math.floor(Math.random() * 12);
            const end = start + 6 + Math.floor(Math.random() * 14);   // scramble 6–19 ticks
            queue.push({ from: oldText[i] || '', to: newText[i] || '', char: '', start, end });
        }
        frame = 0;
        return new Promise(res => { onDone = res; tick(); });
    }
    function tick() {
        if (!document.body.contains(el)) return;
        let html = '', done = 0;
        for (const q of queue) {
            if (frame >= q.end) { done++; html += q.to; }
            else if (frame >= q.start) {
                if (!q.char || Math.random() < SWAP) q.char = glyphs[Math.floor(Math.random() * glyphs.length)];
                html += '<span style="color:' + dim + '">' + q.char + '</span>';
            } else { html += q.from; }
        }
        el.innerHTML = html;
        if (done === queue.length) { if (onDone) onDone(); }
        else { frame++; setTimeout(tick, TICK_MS); }
    }
    const wait = ms => new Promise(r => setTimeout(r, ms));
    (async function loop() {
        while (document.body.contains(el)) {
            await scrambleTo(finalText);   // glitch-decode to the word
            await wait(HOLD_MS);           // hold the resolved word, then re-scramble
        }
        el.dataset.scramble = '';
    })();
};

// Backwards-compat shim: anything still calling initDashboardBackground()
// reads the stored skin choice and applies it.
window.initDashboardBackground = function () {
    let id = null;
    try { id = localStorage.getItem('dashboard_skin'); } catch { /* SSR/privacy */ }
    window.applyDashboardSkin(id || 'digital-fire-fighters');
};

// ---------- Send page background (coin piles) ----------

const COINPILES_IMAGES = [
    'ComfyUI_11594_.webp', 'ComfyUI_11599_.webp', 'ComfyUI_11600_.webp', 'ComfyUI_11602_.webp',
    'ComfyUI_11608_.webp', 'ComfyUI_11611_.webp', 'ComfyUI_11615_.webp', 'ComfyUI_11627_.webp',
    'ComfyUI_11642_.webp', 'ComfyUI_11652_.webp', 'ComfyUI_11655_.webp', 'ComfyUI_11667_.webp',
    'ComfyUI_11672_.webp', 'ComfyUI_11674_.webp', 'ComfyUI_11686_.webp', 'ComfyUI_11696_.webp',
    'ComfyUI_11697_.webp', 'ComfyUI_11701_.webp', 'ComfyUI_11705_.webp', 'ComfyUI_11717_.webp',
    'ComfyUI_11729_.webp', 'ComfyUI_11732_.webp', 'ComfyUI_11736_.webp', 'ComfyUI_11740_.webp',
    'ComfyUI_11750_.webp', 'ComfyUI_11769_.webp', 'ComfyUI_11790_.webp', 'ComfyUI_11802_.webp',
    'ComfyUI_11803_.webp', 'ComfyUI_11804_.webp', 'ComfyUI_11829_.webp', 'ComfyUI_11855_.webp',
    'ComfyUI_11867_.webp', 'ComfyUI_11886_.webp', 'ComfyUI_11901_.webp', 'ComfyUI_11923_.webp',
    'ComfyUI_11976_.webp', 'ComfyUI_11978_.webp', 'ComfyUI_11980_.webp', 'ComfyUI_11984_.webp',
    'ComfyUI_11991_.webp', 'ComfyUI_11992_.webp', 'ComfyUI_12024_.webp', 'ComfyUI_12030_.webp',
    'ComfyUI_12046_.webp'
];

// Warm the coin-pile image set shared by the Send, Receive and Transactions
// pages. Idempotent.
function preloadCoinPilesToRAM() {
    preloadSkinImagesToRAM('coinpiles', 'CoinPilesImages/', COINPILES_IMAGES);
}
// Send / Receive / Transactions are all sidebar destinations hit most sessions
// and share this one image set, so warm it UNCONDITIONALLY at startup and keep
// it resident the whole session (not gated on a page being open). Staggered
// after the Network/Mining warms so the visible dashboard skin paints first.
// ~69MB held for the session.
setTimeout(preloadCoinPilesToRAM, 2000);

// "Coin Piles" tiled background for the Send page — rows of coin-pile images
// scrolling sideways with alternating direction, reusing the Mining page's
// brick-tile treatment (.tile-bg / .tile-row, styled in Send.razor.css). Source
// images are square 1024x1024.
function paintCoinPileTiles(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadCoinPilesToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    let shuffled = shuffle(COINPILES_IMAGES.slice());
    let idx = 0;
    function nextImage() {
        if (idx >= shuffled.length) { shuffled = shuffle(COINPILES_IMAGES.slice()); idx = 0; }
        return 'CoinPilesImages/' + shuffled[idx++];
    }

    const tileW = window.innerWidth / 4;   // 25vw, matches the CSS tile width
    const tileH = tileW;                   // square source images
    const rows = Math.ceil(window.innerHeight / tileH) + 2;
    // Each row builds one set of unique tiles + an exact clone, and scrolls
    // by exactly the set width — so the wrap from end to start lines up on
    // identical content (no visible jump). Set count fits the viewport plus
    // a buffer; the scroll keyframes read the inline --cp-set custom prop.
    const perSet = Math.ceil(window.innerWidth / tileW) + 1;
    const setW = perSet * tileW;
    // Pin the scroll speed to a constant px/s rate (was 35s hardcoded, which
    // sped up after the set+clone change because setW > 50vw). Slightly slower
    // than the original feel.
    const rowDur = (setW / 30).toFixed(1) + 's';

    for (let r = 0; r < rows; r++) {
        const row = document.createElement('div');
        row.className = 'tile-row';
        row.style.setProperty('--cp-set', setW + 'px');
        row.style.animationDuration = rowDur;

        const tiles = [];
        for (let c = 0; c < perSet; c++) {
            const img = document.createElement('img');
            img.src = nextImage();
            img.alt = '';
            tiles.push(img);
            row.appendChild(img);
        }
        // Duplicate the set as exact clones so the one-set scroll loops seamlessly.
        for (const t of tiles) row.appendChild(t.cloneNode(true));
        host.appendChild(row);
    }
}

// Paints the coin-pile tiled background into the Send page host (#sendTileBg).
window.initSendBackground = function () {
    paintCoinPileTiles(document.getElementById('sendTileBg'));
    repaintOnResize('sendTileBg', paintCoinPileTiles);
};

// "Coin Piles" dense mosaic background for the Receive page — a tightly-packed
// grid of coin-pile tiles that twinkle (staggered opacity) and slowly zoom in
// place, rather than scrolling. Smaller tiles than the Send page → many more
// images on screen, and a distinctly different animation. Reuses the same
// coin-pile image set and RAM cache.
function paintCoinPileMosaic(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadCoinPilesToRAM();   // shares the Send page's coin-pile cache (idempotent)

    const tile = 150;  // small tiles → many more images on screen than Send's 25vw
    const cols = Math.ceil(window.innerWidth / tile) + 1;
    const rows = Math.ceil(window.innerHeight / tile) + 1;
    // Grid track sizes set inline so the grid fits the viewport exactly.
    host.style.gridTemplateColumns = 'repeat(' + cols + ', ' + tile + 'px)';
    host.style.gridAutoRows = tile + 'px';

    let shuffled = shuffle(COINPILES_IMAGES.slice());
    let idx = 0;
    const nextImage = function () {
        if (idx >= shuffled.length) { shuffled = shuffle(COINPILES_IMAGES.slice()); idx = 0; }
        return 'CoinPilesImages/' + shuffled[idx++];
    };

    const total = cols * rows;
    const maxDiag = ((cols - 1) + (rows - 1)) || 1;
    const SWEEP_PERIOD = 9;   // seconds; MUST match the CSS coinTwinkle/coinZoom duration

    for (let i = 0; i < total; i++) {
        const r = Math.floor(i / cols), c = i % cols;
        // One diagonal wave sweeping top-left → bottom-right, repeating. The phase
        // ramps with (r+c) across the whole grid — one full period corner to corner,
        // so exactly one wave is on the grid at a time. Negative delays start every
        // tile already mid-animation, so the wave is continuous from the first frame.
        const frac = (r + c) / maxDiag;             // 0 at top-left → 1 at bottom-right
        const delay = ((frac - 1) * SWEEP_PERIOD).toFixed(2) + 's';
        const cell = document.createElement('div');
        cell.className = 'mosaic-cell';
        cell.style.animationDelay = delay;
        const img = document.createElement('img');
        img.src = nextImage();
        img.alt = '';
        img.style.animationDelay = delay;
        cell.appendChild(img);
        host.appendChild(cell);
    }
}

// Paints the dense coin-pile mosaic into the Receive page host (#receiveTileBg).
window.initReceiveBackground = function () {
    paintCoinPileMosaic(document.getElementById('receiveTileBg'));
    repaintOnResize('receiveTileBg', paintCoinPileMosaic);
};

// The Provenance page wears the PIXELATED COINS skin (#provenanceTileBg) — the same
// drifting, flipping pixel-coin grid the Dashboard offers, fitting for a page about
// where each coin came from. Reuses the global .skin-pixelated-coins CSS and
// paintCoinGrid; the painter is idempotent per host, and drawHeaderCoins keeps the
// banner coins painted the way every other page's header does.
window.initProvenanceBackground = function () {
    const host = document.getElementById('provenanceTileBg');
    if (!host) return;
    host.className = 'skin-pixelated-coins';
    paintCoinGrid(host);
};

// "Coin Piles" diagonal-drift background for the Transactions page — a tiled
// grid 2x the viewport whose image pattern repeats every viewport, scrolling
// diagonally and looping seamlessly (it translates by exactly one viewport-block,
// and because the pattern repeats every block the wrap is invisible). Medium
// density (6 columns: more than Send's ~4, fewer than Receive's ~9). Reuses the
// coin-pile image set + RAM cache.
function paintCoinPileDiagonal(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadCoinPilesToRAM();   // shares the Send/Receive coin-pile cache (idempotent)

    const colsView = 6;                                  // medium density
    const tile = window.innerWidth / colsView;
    const rowsView = Math.ceil(window.innerHeight / tile);

    // One viewport-block of unique images; the grid repeats this block so a
    // diagonal scroll of exactly one block loops seamlessly.
    const baseCount = colsView * rowsView;
    let pool = shuffle(COINPILES_IMAGES.slice());
    while (pool.length < baseCount) pool = pool.concat(shuffle(COINPILES_IMAGES.slice()));
    const base = pool.slice(0, baseCount);

    const totalCols = colsView * 2 + 1;   // 2 blocks + 1 buffer so edges never gap
    const totalRows = rowsView * 2 + 1;

    const grid = document.createElement('div');
    grid.className = 'diag-grid';
    grid.style.gridTemplateColumns = 'repeat(' + totalCols + ', ' + tile + 'px)';
    grid.style.gridAutoRows = tile + 'px';
    grid.style.setProperty('--diag-x', (colsView * tile) + 'px');  // one block = the loop distance
    grid.style.setProperty('--diag-y', (rowsView * tile) + 'px');

    for (let r = 0; r < totalRows; r++) {
        for (let c = 0; c < totalCols; c++) {
            const img = document.createElement('img');
            // Periodic indexing: the tile at (r,c) repeats every block, so the
            // diagonal scroll wraps with no visible seam.
            img.src = 'CoinPilesImages/' + base[(r % rowsView) * colsView + (c % colsView)];
            img.alt = '';
            // Per-tile breathing pulse (like Send/Receive), staggered. Delay is
            // periodic per block (r%rowsView, c%colsView) so it stays seamless
            // across the diagonal wrap; negative → already mid-pulse at load.
            const pulsePhase = ((r % rowsView) + (c % colsView)) % 8;
            img.style.animationDelay = (-pulsePhase * 0.75).toFixed(2) + 's';
            grid.appendChild(img);
        }
    }
    host.appendChild(grid);
}

// Shared: re-paint a "paint-once" background host when the window resizes
// (debounced), so a larger viewport (maximise / full screen) never reveals black
// past a grid that was sized to the old, smaller window. The painters derive their
// tile/grid size from innerWidth/innerHeight at paint time, so they must be re-run.
// Bound once PER host id; no-ops when that host isn't mounted. paintFn(host) paints.
const _bgResizeTimers = {};
function repaintOnResize(hostId, paintFn) {
    if (!window._bgResizeBound) window._bgResizeBound = {};
    if (window._bgResizeBound[hostId]) return;
    window._bgResizeBound[hostId] = true;
    window.addEventListener('resize', function () {
        clearTimeout(_bgResizeTimers[hostId]);
        _bgResizeTimers[hostId] = setTimeout(function () {
            const h = document.getElementById(hostId);
            if (!h) return;                 // page not mounted
            h.innerHTML = '';               // drop the old grid
            h.dataset.populated = '';       // allow a fresh paint at the new size
            paintFn(h);
        }, 200);
    });
}

// Paints the diagonally-scrolling coin-pile grid into the Transactions host.
window.initTransactionsBackground = function () {
    paintCoinPileDiagonal(document.getElementById('txTileBg'));
    repaintOnResize('txTileBg', paintCoinPileDiagonal);
};

// ---------- Address Book page background ----------

const ADDRESSBOOK_IMAGES = [
    'ComfyUI_12232_.webp', 'ComfyUI_12248_.webp', 'ComfyUI_12294_.webp', 'ComfyUI_12314_.webp',
    'ComfyUI_12319_.webp', 'ComfyUI_12347_.webp', 'ComfyUI_12359_.webp', 'ComfyUI_12361_.webp',
    'ComfyUI_12372_.webp', 'ComfyUI_12375_.webp', 'ComfyUI_12379_.webp', 'ComfyUI_12382_.webp',
    'ComfyUI_12384_.webp', 'ComfyUI_12400_.webp', 'ComfyUI_12416_.webp', 'ComfyUI_12421_.webp',
    'ComfyUI_12435_.webp', 'ComfyUI_12444_.webp', 'ComfyUI_12455_.webp', 'ComfyUI_12460_.webp',
    'ComfyUI_12464_.webp', 'ComfyUI_12467_.webp', 'ComfyUI_12469_.webp', 'ComfyUI_12471_.webp',
    'ComfyUI_12485_.webp', 'ComfyUI_12495_.webp', 'ComfyUI_12500_.webp', 'ComfyUI_12501_.webp',
    'ComfyUI_12505_.webp', 'ComfyUI_12512_.webp', 'ComfyUI_12517_.webp', 'ComfyUI_12520_.webp',
    'ComfyUI_12522_.webp', 'ComfyUI_12532_.webp', 'ComfyUI_12539_.webp', 'ComfyUI_12547_.webp',
    'ComfyUI_12568_.webp', 'ComfyUI_12577_.webp', 'ComfyUI_12581_.webp', 'ComfyUI_12587_.webp',
    'ComfyUI_12595_.webp', 'ComfyUI_12598_.webp', 'ComfyUI_12599_.webp', 'ComfyUI_12601_.webp',
    'ComfyUI_12607_.webp', 'ComfyUI_12609_.webp', 'ComfyUI_12611_.webp', 'ComfyUI_12615_.webp',
    'ComfyUI_12616_.webp', 'ComfyUI_12617_.webp', 'ComfyUI_12620_.webp', 'ComfyUI_12625_.webp'
];

// Warm the Address Book image set. Idempotent.
function preloadAddressBookToRAM() {
    preloadSkinImagesToRAM('addressbook', 'AddressBookImages/', ADDRESSBOOK_IMAGES);
}
// Address Book is a sidebar destination needed every session, so warm it
// UNCONDITIONALLY at startup and keep it resident (like Network/Mining/CoinPiles).
// Staggered after CoinPiles (2000ms) and before the splash's 2500ms readiness
// floor, so it's counted in the startup splash wait. ~84MB held for the session.
setTimeout(preloadAddressBookToRAM, 2300);

// Address Book background — vertical columns of tiles scrolling up/down on
// alternating columns (a fourth distinct motion vs. Send's horizontal rows,
// Receive's mosaic sweep and Transactions' diagonal drift), each tile with a
// gentle staggered opacity pulse. Square 1024x1024 source images.
function paintAddressBookColumns(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadAddressBookToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const cols = Math.max(5, Math.round(window.innerWidth / 220));
    const colW = window.innerWidth / cols;
    const perCol = Math.ceil(window.innerHeight / colW) + 2;  // square tiles to fill height + buffer
    host.style.setProperty('--abcol-w', colW + 'px');

    let shuffled = shuffle(ADDRESSBOOK_IMAGES.slice());
    let idx = 0;
    const nextImage = function () {
        if (idx >= shuffled.length) { shuffled = shuffle(ADDRESSBOOK_IMAGES.slice()); idx = 0; }
        return 'AddressBookImages/' + shuffled[idx++];
    };

    for (let c = 0; c < cols; c++) {
        const col = document.createElement('div');
        col.className = 'ab-col';
        // One set of tiles, then an exact clone, so the vertical scroll loops
        // seamlessly (the CSS animates by exactly one set = -50% of column height).
        const set = [];
        for (let i = 0; i < perCol; i++) set.push(nextImage());
        for (let dup = 0; dup < 2; dup++) {
            for (let i = 0; i < perCol; i++) {
                const img = document.createElement('img');
                img.src = set[i];
                img.alt = '';
                // Stagger the pulse; periodic per set (uses i) so the duplicate
                // matches and the scroll wrap stays seamless.
                img.style.animationDelay = (-((i + c) % 7) * 0.6).toFixed(2) + 's';
                col.appendChild(img);
            }
        }
        host.appendChild(col);
    }
}

// Paints the vertical scrolling columns into the Address Book page host.
window.initAddressBookBackground = function () {
    paintAddressBookColumns(document.getElementById('abTileBg'));
    repaintOnResize('abTileBg', paintAddressBookColumns);
};

// ---------- Security page background ----------

const SECURITY_IMAGES = [
    'ComfyUI_12715_.webp', 'ComfyUI_12723_.webp', 'ComfyUI_12727_.webp', 'ComfyUI_12730_.webp',
    'ComfyUI_12735_.webp', 'ComfyUI_12739_.webp', 'ComfyUI_12741_.webp', 'ComfyUI_12743_.webp',
    'ComfyUI_12747_.webp', 'ComfyUI_12761_.webp', 'ComfyUI_12765_.webp', 'ComfyUI_12767_.webp',
    'ComfyUI_12768_.webp', 'ComfyUI_12769_.webp', 'ComfyUI_12772_.webp', 'ComfyUI_12775_.webp',
    'ComfyUI_12776_.webp', 'ComfyUI_12805_.webp', 'ComfyUI_12810_.webp', 'ComfyUI_12819_.webp',
    'ComfyUI_12880_.webp', 'ComfyUI_12882_.webp', 'ComfyUI_12885_.webp', 'ComfyUI_12918_.webp',
    'ComfyUI_12945_.webp', 'ComfyUI_12948_.webp', 'ComfyUI_12966_.webp', 'ComfyUI_12969_.webp',
    'ComfyUI_12974_.webp', 'ComfyUI_13022_.webp', 'ComfyUI_13032_.webp', 'ComfyUI_13043_.webp',
    'ComfyUI_13100_.webp', 'ComfyUI_13123_.webp', 'ComfyUI_13300_.webp', 'ComfyUI_13313_.webp',
    'ComfyUI_13326_.webp', 'ComfyUI_13362_.webp', 'ComfyUI_13404_.webp', 'ComfyUI_13436_.webp',
    'ComfyUI_13502_.webp', 'ComfyUI_13507_.webp', 'ComfyUI_13508_.webp', 'ComfyUI_13509_.webp',
    'ComfyUI_13557_.webp', 'ComfyUI_13562_.webp', 'ComfyUI_13571_.webp', 'ComfyUI_13578_.webp',
    'ComfyUI_13579_.webp', 'ComfyUI_13586_.webp'
];

// Warm the Security image set. Idempotent.
function preloadSecurityToRAM() {
    preloadSkinImagesToRAM('security', 'SecurityImages/', SECURITY_IMAGES);
}
// Security is a page background AND a dashboard skin, and its tiles flip/pixelate
// to random images at runtime, so warm the whole set UNCONDITIONALLY at startup
// and keep it resident. Staggered after AddressBook (2300ms) and before the
// splash's 2500ms readiness floor. ~72MB held for the session.
setTimeout(preloadSecurityToRAM, 2400);

// Security background — a dense grid of tiles, each slowly panning + zooming
// (Ken Burns), staggered so the wall gently drifts and breathes rather than
// scrolling. A distinct motion from the other page backgrounds. Square 1024x1024
// source; cells clip the overscaled image so the pan never reveals an edge.
function paintSecurityGrid(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';
    host.dataset.paintedAt = String(Math.round(performance.now()));  // for Ken Burns sync

    preloadSecurityToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const tile = 200;
    const cols = Math.ceil(window.innerWidth / tile) + 1;
    const rows = Math.ceil(window.innerHeight / tile) + 1;
    host.style.gridTemplateColumns = 'repeat(' + cols + ', ' + tile + 'px)';
    host.style.gridAutoRows = tile + 'px';

    let shuffled = shuffle(SECURITY_IMAGES.slice());
    let idx = 0;
    const nextImage = function () {
        if (idx >= shuffled.length) { shuffled = shuffle(SECURITY_IMAGES.slice()); idx = 0; }
        return 'SecurityImages/' + shuffled[idx++];
    };

    const total = cols * rows;
    for (let i = 0; i < total; i++) {
        const cell = document.createElement('div');
        cell.className = 'security-cell';
        const img = document.createElement('img');
        img.src = nextImage();
        img.alt = '';
        // Stagger the Ken Burns loop so neighbouring tiles don't pan in unison.
        img.style.animationDelay = (-(i % 12) * 1.5).toFixed(2) + 's';
        cell.appendChild(img);
        host.appendChild(cell);
    }

    startSecurityEffects(host);   // sporadically flip / pixelate random tiles
}

// Flip one Security tile to a different image: rotate the cell edge-on, swap the
// image while it's hidden, then rotate back in from the far side (so the new
// image is never shown mirrored). The flip is on the CELL, leaving the img's Ken
// Burns intact; swapped images are already RAM-resident so the swap is instant.
function flipSecurityCell(cell) {
    if (!cell || cell.dataset.busy === '1') return;
    cell.dataset.busy = '1';
    cell.style.transition = 'transform 1.2s ease-in';
    cell.style.transform = 'perspective(900px) rotateY(90deg)';
    setTimeout(function () {
        const img = cell.querySelector('img');
        if (img) img.src = 'SecurityImages/' + SECURITY_IMAGES[Math.floor(Math.random() * SECURITY_IMAGES.length)];
        cell.style.transition = 'none';
        cell.style.transform = 'perspective(900px) rotateY(-90deg)';
        void cell.offsetWidth;                 // force reflow so the jump isn't animated
        cell.style.transition = 'transform 1.2s ease-out';
        cell.style.transform = 'perspective(900px) rotateY(0deg)';
        setTimeout(function () { cell.dataset.busy = '0'; }, 1220);
    }, 1200);
}

// Pixelate one Security tile out and back in with a "matrix green" tint, via a
// canvas overlay: the source image is drawn at a resolution that drops to chunky
// blocks then returns to full, tinted green strongest at the most-pixelated point.
function pixelateSecurityCell(cell) {
    if (!cell || cell.dataset.busy === '1') return;
    const img = cell.querySelector('img');
    if (!img || !img.complete) return;          // need a decoded source to draw
    cell.dataset.busy = '1';

    const RES = 256;                            // canvas internal resolution
    const MIN_RES = 6;                          // chunkiest (largest pixel blocks)
    const BLOCK_MAX = RES / MIN_RES;            // max pixel-block size (~43px)
    const DURATION = 4500;
    const canvas = document.createElement('canvas');
    canvas.width = RES; canvas.height = RES;
    canvas.className = 'sec-pixel';
    const ctx = canvas.getContext('2d');
    const offscreen = document.createElement('canvas');
    const offCtx = offscreen.getContext('2d');

    // Give the canvas the SAME Ken Burns as the image, phase-synced, so when it
    // replaces (and later releases) the image there's no size jump on handoff.
    const host = cell.parentElement;
    const paintedAt = (host && Number(host.dataset.paintedAt)) || performance.now();
    const elapsed = (performance.now() - paintedAt) / 1000;
    const imgDelay = parseFloat(img.style.animationDelay) || 0;
    canvas.style.animation = 'secKenBurns 20s ease-in-out infinite';
    canvas.style.animationDelay = (imgDelay - elapsed).toFixed(2) + 's';

    cell.appendChild(canvas);
    img.style.visibility = 'hidden';

    const start = performance.now();
    function cleanup() {
        if (canvas.parentNode) canvas.parentNode.removeChild(canvas);
        img.style.visibility = '';
        cell.dataset.busy = '0';
    }
    function frame(now) {
        if (!cell.isConnected) { cleanup(); return; }
        const p = Math.min(1, (now - start) / DURATION);
        // Linear triangle (uniform rate, no ease) + exponential resolution so the
        // perceived pixelation changes steadily instead of stalling at the peak.
        const phase = p < 0.5 ? p * 2 : (1 - p) * 2;   // 0 -> 1 -> 0, equal time each way
        // Pixel-block size grows/shrinks linearly, so the un-pixelation mirrors
        // the pixelation exactly — same pace both directions, steady throughout.
        const res = Math.max(MIN_RES, Math.round(RES / (1 + phase * (BLOCK_MAX - 1))));
        offscreen.width = res; offscreen.height = res;
        offCtx.drawImage(img, 0, 0, res, res);  // downscale to res
        ctx.imageSmoothingEnabled = false;
        ctx.clearRect(0, 0, RES, RES);
        ctx.drawImage(offscreen, 0, 0, res, res, 0, 0, RES, RES);  // upscale = blocky
        // Matrix green ramps in fast then holds near full through the pixelated
        // middle, so it reads clearly green rather than only at the instant peak.
        const tint = Math.min(0.85, phase * 1.6);
        if (tint > 0.01) {
            ctx.globalCompositeOperation = 'multiply';
            ctx.fillStyle = 'rgba(0, 255, 65, ' + tint.toFixed(3) + ')';
            ctx.fillRect(0, 0, RES, RES);
            ctx.globalCompositeOperation = 'lighter';
            ctx.fillStyle = 'rgba(0, 255, 65, ' + (tint * 0.18).toFixed(3) + ')';
            ctx.fillRect(0, 0, RES, RES);
            ctx.globalCompositeOperation = 'source-over';
        }
        if (p < 1) requestAnimationFrame(frame);
        else cleanup();
    }
    requestAnimationFrame(frame);
}

// Sporadically apply an effect to a random tile, one at a time at random
// intervals — mostly flips, ~40% a matrix-green pixelate.
function startSecurityEffects(host) {
    const cells = Array.from(host.querySelectorAll('.security-cell'));
    if (!cells.length) return;
    function tick() {
        // Stop when these cells are gone — covers navigating away from the page
        // AND switching the dashboard to another skin (applyDashboardSkin clears
        // the host's children, detaching them).
        if (!cells[0].isConnected) return;
        const cell = cells[Math.floor(Math.random() * cells.length)];
        if (Math.random() < 0.4) pixelateSecurityCell(cell);
        else flipSecurityCell(cell);
        setTimeout(tick, 700 + Math.random() * 1600);
    }
    setTimeout(tick, 1200);                      // settle, then begin
}

// Paints the Ken Burns grid into the Security page host.
window.initSecurityBackground = function () {
    paintSecurityGrid(document.getElementById('securityTileBg'));
    repaintOnResize('securityTileBg', paintSecurityGrid);
};

// ---------- Backup page background ----------

const BACKUP_IMAGES = [
    "ComfyUI_14012_.webp", "ComfyUI_14018_.webp", "ComfyUI_14020_.webp", "ComfyUI_14025_.webp",
    "ComfyUI_14026_.webp", "ComfyUI_14033_.webp", "ComfyUI_14046_.webp", "ComfyUI_14064_.webp",
    "ComfyUI_14065_.webp", "ComfyUI_14066_.webp", "ComfyUI_14070_.webp", "ComfyUI_14071_.webp",
    "ComfyUI_14072_.webp", "ComfyUI_14073_.webp", "ComfyUI_14074_.webp", "ComfyUI_14075_.webp",
    "ComfyUI_14076_.webp", "ComfyUI_14079_.webp", "ComfyUI_14080_.webp", "ComfyUI_14081_.webp",
    "ComfyUI_14086_.webp", "ComfyUI_14090_.webp", "ComfyUI_14092_.webp", "ComfyUI_14093_.webp",
    "ComfyUI_14094_.webp", "ComfyUI_14096_.webp", "ComfyUI_14097_.webp", "ComfyUI_14100_.webp",
    "ComfyUI_14105_.webp", "ComfyUI_14107_.webp", "ComfyUI_14108_.webp", "ComfyUI_14113_.webp",
    "ComfyUI_14117_.webp", "ComfyUI_14118_.webp", "ComfyUI_14128_.webp", "ComfyUI_14135_.webp",
    "ComfyUI_14176_.webp", "ComfyUI_14178_.webp", "ComfyUI_14180_.webp", "ComfyUI_14181_.webp",
    "ComfyUI_14199_.webp", "ComfyUI_14200_.webp", "ComfyUI_14208_.webp", "ComfyUI_14216_.webp",
    "ComfyUI_14219_.webp", "ComfyUI_14220_.webp", "ComfyUI_14227_.webp", "ComfyUI_14237_.webp",
    "ComfyUI_14244_.webp", "ComfyUI_14261_.webp", "ComfyUI_14264_.webp", "ComfyUI_14273_.webp",
    "ComfyUI_14274_.webp", "ComfyUI_14283_.webp", "ComfyUI_14284_.webp"
];

// Warm the Backup image set. Idempotent.
function preloadBackupToRAM() {
    preloadSkinImagesToRAM('backup', 'BackupImages/', BACKUP_IMAGES);
}
// Backup is a sidebar destination needed every session, so warm it
// UNCONDITIONALLY at startup and keep it resident (like Network/Mining/CoinPiles/
// AddressBook/Security). Staggered after Security (2400ms) and before the splash's
// 2500ms readiness floor, so it's counted in the startup splash wait.
// ~88MB held for the session.
setTimeout(preloadBackupToRAM, 2450);

// Backup background — diagonal-drift tile grid. Like the Transactions diagonal
// scroll (tiled grid 2x the viewport, pattern repeats every viewport block,
// translates by exactly one block so the wrap is seamless), but drifts in the
// OPPOSITE direction (top-right → bottom-left, where Transactions goes
// bottom-right) and at slightly higher density so lots of tiles are visible.
// Tiles have a soft frame and a staggered breathing pulse.
function paintBackupDiagonal(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadBackupToRAM();   // warm the RAM cache for flicker-free repaints (idempotent)

    const colsView = 5;                                  // bigger tiles — fewer per row
    const tile = window.innerWidth / colsView;
    const rowsView = Math.ceil(window.innerHeight / tile);

    // One viewport-block of unique images; the grid repeats this block so a
    // diagonal scroll of exactly one block loops seamlessly.
    const baseCount = colsView * rowsView;
    let pool = shuffle(BACKUP_IMAGES.slice());
    while (pool.length < baseCount) pool = pool.concat(shuffle(BACKUP_IMAGES.slice()));
    const base = pool.slice(0, baseCount);

    const totalCols = colsView * 2 + 1;   // 2 blocks + 1 buffer so edges never gap
    const totalRows = rowsView * 2 + 1;

    const grid = document.createElement('div');
    grid.className = 'bk-diag-grid';
    grid.style.gridTemplateColumns = 'repeat(' + totalCols + ', ' + tile + 'px)';
    grid.style.gridAutoRows = tile + 'px';
    grid.style.setProperty('--bk-diag-x', (colsView * tile) + 'px');  // one block = the loop distance
    grid.style.setProperty('--bk-diag-y', (rowsView * tile) + 'px');

    for (let r = 0; r < totalRows; r++) {
        for (let c = 0; c < totalCols; c++) {
            const cell = document.createElement('div');
            cell.className = 'bk-cell';
            const img = document.createElement('img');
            // Periodic indexing: the tile at (r,c) repeats every block, so the
            // diagonal scroll wraps with no visible seam.
            img.src = 'BackupImages/' + base[(r % rowsView) * colsView + (c % colsView)];
            img.alt = '';
            // Per-tile breathing pulse, staggered. Delay is periodic per block
            // (r%rowsView, c%colsView) so it stays seamless across the diagonal
            // wrap; negative → already mid-pulse at load.
            const pulsePhase = ((r % rowsView) + (c % colsView)) % 8;
            img.style.animationDelay = (-pulsePhase * 0.75).toFixed(2) + 's';
            cell.appendChild(img);
            grid.appendChild(cell);
        }
    }
    host.appendChild(grid);
}

// Paints the diagonally-scrolling tile grid into the Backup page host.
window.initBackupBackground = function () {
    paintBackupDiagonal(document.getElementById('backupTileBg'));
    repaintOnResize('backupTileBg', paintBackupDiagonal);
};

// ---------- Import page background ----------

// Import reuses the same BackupImages set (already RAM-preloaded
// unconditionally for the Backup page) but a totally different motion: three
// concentric rings of tiles orbiting the viewport center, counter-rotating
// (outer CW, middle CCW, inner CW) at staggered speeds. Distinct from every
// other page (rows / columns / mosaic / grid / diagonal-drift) and reads as
// "data converging on the wallet".
function paintImportRings(host) {
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';

    preloadBackupToRAM();   // shares the Backup page's cache (idempotent)

    const shuffled = shuffle(BACKUP_IMAGES.slice());
    let idx = 0;
    const nextImage = function () {
        if (idx >= shuffled.length) { shuffle(shuffled); idx = 0; }
        return 'BackupImages/' + shuffled[idx++];
    };

    // Background field — dimmed rows of tiles filling the viewport beneath
    // the rings; alternate rows scroll left vs. right. Each row builds one
    // set + an exact clone, scrolling by precisely one set width, so the loop
    // is seamless. Painted FIRST so it sits below the rings in DOM order.
    const fieldTile = 160;
    const fieldCols = Math.ceil(window.innerWidth / fieldTile) + 2;   // buffer so edges never gap
    const fieldRows = Math.ceil(window.innerHeight / fieldTile);
    const setW = fieldCols * fieldTile;
    const rowDur = (setW / 40).toFixed(1);                            // ~40 px/s scroll

    const field = document.createElement('div');
    field.className = 'im-field';

    for (let r = 0; r < fieldRows; r++) {
        const row = document.createElement('div');
        row.className = 'im-field-row';
        row.style.height = fieldTile + 'px';
        row.style.setProperty('--field-set', setW + 'px');
        row.style.animationName = (r % 2 === 0) ? 'imFieldScrollLeft' : 'imFieldScrollRight';
        row.style.animationDuration = rowDur + 's';

        const tiles = [];
        for (let c = 0; c < fieldCols; c++) {
            const img = document.createElement('img');
            img.src = nextImage();
            img.alt = '';
            img.style.width = fieldTile + 'px';
            img.style.height = fieldTile + 'px';
            // Stagger the breathing pulse so neighbours aren't in sync; periodic
            // per (r%4, c%9) so the clone matches and the scroll wrap is seamless.
            img.style.animationDelay = (-((c % 9) + (r % 4) * 0.5) * 1.1).toFixed(2) + 's';
            tiles.push(img);
            row.appendChild(img);
        }
        // Duplicate the set as exact clones so the one-set scroll loops seamlessly.
        for (const t of tiles) row.appendChild(t.cloneNode(true));
        field.appendChild(row);
    }
    host.appendChild(field);

    // Ring radii scale with the smaller viewport dimension so the rings stay
    // proportionally placed at any window size. Tile counts chosen so each
    // ring's tiles roughly touch their neighbours along the circumference.
    // Radii are pushed beyond the centered .wallet-page form (which sits over
    // viewport centre and would otherwise hide everything closer than ~35%).
    // Outer clips slightly top/bottom on short windows but stays in the corners.
    // Radial offsets compensate for tile-size differences so the visible edge
    // gap between consecutive rings stays uniform — without this, the outer
    // ring (bigger tiles) looks closer to the middle than the middle does to
    // the inner. Math: gap difference = (tile_outer - tile_inner) / 2 = ~12.5px.
    const base = Math.min(window.innerWidth, window.innerHeight);
    const ringSpecs = [
        { cls: 'im-ring-outer',  radius: base * 0.64, count: 20, tile: 140, dur: 90 },
        { cls: 'im-ring-middle', radius: base * 0.48, count: 16, tile: 125, dur: 70 },
        { cls: 'im-ring-inner',  radius: base * 0.34, count: 12, tile: 115, dur: 50 },
    ];

    for (const spec of ringSpecs) {
        const ring = document.createElement('div');
        ring.className = 'im-ring ' + spec.cls;
        ring.style.setProperty('--radius', spec.radius + 'px');
        ring.style.setProperty('--ring-dur', spec.dur + 's');
        for (let i = 0; i < spec.count; i++) {
            const tile = document.createElement('div');
            tile.className = 'im-tile';
            tile.style.setProperty('--angle', (360 / spec.count * i).toFixed(2) + 'deg');
            tile.style.setProperty('--tile', spec.tile + 'px');
            const img = document.createElement('img');
            img.src = nextImage();
            img.alt = '';
            tile.appendChild(img);
            ring.appendChild(tile);
        }
        host.appendChild(ring);
    }
}

// Paints the three counter-rotating rings into the Import page host.
window.initImportBackground = function () {
    paintImportRings(document.getElementById('importBg'));
};

// Repaint on window resize so the field rows extend to fill a now-larger
// viewport (and ring radii recompute). Debounced. Registered once; the
// no-host guard makes it a no-op when the Import page isn't mounted.
(function () {
    let timer = null;
    window.addEventListener('resize', function () {
        clearTimeout(timer);
        timer = setTimeout(function () {
            const h = document.getElementById('importBg');
            if (!h) return;
            h.innerHTML = '';
            h.dataset.populated = '';
            paintImportRings(h);
        }, 150);
    });
})();

// ---------- Mining page background ----------

window.initMiningBackground = function () {
    paintTileRows(document.getElementById('tileBg'));
    repaintOnResize('tileBg', paintTileRows);
};

// Paints the Network skin into an arbitrary host id. Used as the permanent
// background of the Console page, independent of the Dashboard skin picker.
window.initNetworkBackground = function (id) {
    // Mirrors initMiningBackground: paints columns into the host WITHOUT
    // touching its class, so the host keeps the .net-bg class set in markup.
    paintNetworkColumns(document.getElementById(id));
    repaintOnResize(id, paintNetworkColumns);
};

// Types the Console banner title one character at a time, then holds, clears,
// and retypes — looping forever. The blinking caret is CSS (see .banner-caret
// in RpcConsole.razor.css).
window.typeConsoleBanner = function () {
    const el = document.getElementById('consoleTitle');
    if (!el) return;
    const fullText = 'Console';
    if (window.__consoleTypeTimer) clearInterval(window.__consoleTypeTimer);
    let i = 0;
    let hold = 0;
    el.textContent = '';
    window.__consoleTypeTimer = setInterval(() => {
        if (i < fullText.length) {
            el.textContent += fullText[i++];
        } else if (++hold > 60) {   // hold ~12s on the full word, then restart
            i = 0;
            hold = 0;
            el.textContent = '';
        }
    }, 200);
};

// ---------- Header coin sync ----------

window.startHeaderCoinSync = function () {
    if (window.__headerCoinSyncRunning) return; // idempotent
    window.__headerCoinSyncRunning = true;

    // Alternating flip matching the grid coins' timing: 12s total cycle =
    // 6s spinning + 6s holding, with the second coin offset by half the
    // cycle so the two header coins take turns.
    const DURATION = 12000;
    const SPIN_FRACTION = 0.5;
    const PHASE_STEP = 0.5;

    function easeInOut(x) {
        // CSS `ease-in-out` = cubic-bezier(0.42, 0, 0.58, 1). Solve
        // Bx(s) = x via Newton's method, then return By(s) = 3s² − 2s³.
        const p1x = 0.42, p2x = 0.58;
        let s = x;
        for (let i = 0; i < 6; i++) {
            const om = 1 - s;
            const bx = 3 * om * om * s * p1x + 3 * om * s * s * p2x + s * s * s;
            const dbx = 3 * p1x * om * (1 - 3 * s) + 3 * p2x * s * (2 - 3 * s) + 3 * s * s;
            const err = bx - x;
            if (Math.abs(err) < 1e-5 || Math.abs(dbx) < 1e-9) break;
            s -= err / dbx;
            if (s < 0) s = 0; else if (s > 1) s = 1;
        }
        return 3 * s * s - 2 * s * s * s;
    }

    function frame(now) {
        if (!window.__headerCoinSyncRunning) return;

        // Read the live CSS animation phase fresh each frame. If no page
        // coin is in the DOM (Dashboard not mounted, or the active skin
        // isn't Pixelated Coins) fall back to performance.now() so the
        // header coins keep animating on their own clock — they'll snap
        // back to the page coins as soon as those reappear.
        let elapsedMs = now;
        const ref = document.querySelector('.coin-cell:not(.flip) canvas');
        if (ref && typeof ref.getAnimations === 'function') {
            const anims = ref.getAnimations();
            if (anims.length > 0 && anims[0].currentTime !== null) {
                const ct = typeof anims[0].currentTime === 'number'
                    ? anims[0].currentTime
                    : Number(anims[0].currentTime);
                if (!Number.isNaN(ct)) elapsedMs = ct;
            }
        }

        const coins = document.querySelectorAll('.header-coin');
        if (coins.length > 0) {
            const baseT = (elapsedMs % DURATION) / DURATION;
            coins.forEach((c, i) => {
                const t = (baseT + i * PHASE_STEP) % 1;
                let deg;
                if (t < SPIN_FRACTION) {
                    deg = -easeInOut(t / SPIN_FRACTION) * 360;
                } else {
                    deg = -360;
                }
                c.style.transform = `rotateY(${deg.toFixed(2)}deg)`;
            });
        }
        requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
};

// ---------- Quantum Exposure page ----------

// The QUANTUM page's skin (#quantumTileBg): a solid black ground; a sheet of 240px tiles with crimson grout and a
// pixel-art atom in every cell (wwwroot/QuantumImages/atom-tile-1/2.png, the electrons ticking between the two
// frames once a second) scrolling bottom-left to top-right one tile every 12s; a soft sheen sweeping the same way;
// and, on a canvas above the tiles, drawn atoms - glowing nucleus, three tilted orbits, electrons travelling round
// them - streaming up the same diagonal at their own speeds. Idempotent per host; the canvas loop stops itself once
// the host leaves the document (navigating away), so nothing keeps drawing behind another page.
window.initQuantumBackground = function () {
    const host = document.getElementById('quantumTileBg');
    if (!host) return;
    if (host.dataset.populated === '1') return;
    host.dataset.populated = '1';
    host.className = 'skin-quantum-tiles';

    const icons = document.createElement('div'); icons.className = 'qx-tile-icons';
    const sheen = document.createElement('div'); sheen.className = 'qx-tile-sheen';
    const canvas = document.createElement('canvas'); canvas.className = 'qx-atom-canvas';
    host.appendChild(icons); host.appendChild(sheen); host.appendChild(canvas);

    const ctx = canvas.getContext('2d');
    const reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let atoms = [], W = 0, H = 0;
    const seed = i => { const x = Math.sin(i * 9301 + 49297) * 233280; return x - Math.floor(x); };
    function layout() {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        W = window.innerWidth; H = window.innerHeight;
        canvas.width = W * dpr; canvas.height = H * dpr; ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        const count = Math.max(10, Math.round((W * H) / 70000));
        atoms = [];
        for (let i = 0; i < count; i++) {
            atoms.push({
                x: seed(i * 3 + 1) * W, y: seed(i * 3 + 2) * H,
                r: 26 + seed(i * 3 + 3) * 44,
                tilt: seed(i * 7 + 4) * Math.PI,
                speed: (0.4 + seed(i * 7 + 5) * 0.9) * (seed(i * 7 + 6) > 0.5 ? 1 : -1),
                phase: seed(i * 7 + 7) * Math.PI * 2,
                v: 36 + seed(i * 11 + 8) * 50,          // px per second along the diagonal
                wob: seed(i * 11 + 9) * Math.PI * 2
            });
        }
    }
    function drawAtom(a, t) {
        const s = t * a.v, m = a.r + 12;
        const x = ((a.x + s + m) % (W + 2 * m)) - m;
        const y = H - (((H - a.y) + s + m) % (H + 2 * m)) + m;
        const wob = Math.sin(t * 0.7 + a.wob) * 6;
        ctx.save(); ctx.translate(x + wob, y + wob); ctx.rotate(a.tilt + t * 0.03 * a.speed);
        for (let k = 0; k < 3; k++) {
            ctx.save(); ctx.rotate(k * Math.PI / 3);
            ctx.beginPath(); ctx.ellipse(0, 0, a.r, a.r * 0.38, 0, 0, Math.PI * 2);
            ctx.strokeStyle = 'rgba(255, 90, 90, 0.22)'; ctx.lineWidth = 1; ctx.stroke();
            const ang = a.phase + t * a.speed * 1.6 + k * 2.1;
            ctx.beginPath(); ctx.arc(Math.cos(ang) * a.r, Math.sin(ang) * a.r * 0.38, 2.2, 0, Math.PI * 2);
            ctx.fillStyle = '#ff6a5a'; ctx.shadowColor = 'rgba(255, 90, 90, 0.9)'; ctx.shadowBlur = 8; ctx.fill();
            ctx.restore();
        }
        ctx.shadowColor = 'rgba(207, 32, 19, 0.9)'; ctx.shadowBlur = 14;
        const n = [[0, 0], [3.2, -1.8], [-2.6, 2.4], [1.4, 3.4]];
        for (let j = 0; j < n.length; j++) {
            ctx.beginPath(); ctx.arc(n[j][0], n[j][1], 3, 0, Math.PI * 2);
            ctx.fillStyle = j % 2 ? '#ffd9d4' : '#cf2013'; ctx.fill();
        }
        ctx.restore();
    }
    function frame(ts) {
        if (!document.body.contains(host)) { window.removeEventListener('resize', layout); return; }
        const t = ts / 1000;
        ctx.clearRect(0, 0, W, H);
        for (let i = 0; i < atoms.length; i++) drawAtom(atoms[i], t);
        if (!reduce) requestAnimationFrame(frame);
    }
    layout(); window.addEventListener('resize', layout);
    requestAnimationFrame(frame);
};

// The QUANTUM banner: the title decrypts. Each letter sits in a fixed-width cell measured once from the final word
// in the banner's own face, so the word never changes width while the glyphs inside the cells scramble through
// cipher characters and resolve left to right; the settled title flares once, rests HOLD_MS, and runs again.
// Cancels any pass still running (re-entry), stops itself once the element leaves the document, and shows the
// plain title under prefers-reduced-motion. Click the title to restart it.
window.decryptQuantumBanner = function () {
    const el = document.getElementById('quantumBannerText');
    if (!el) return;
    const GLYPHS = '0123456789ABCDEF#$%&@*+=<>?/';
    const HOLD_MS = 6000;
    const reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const target = el.dataset.fullText || el.textContent.trim();
    el.dataset.fullText = target;

    window.stopQuantumBanner();
    if (reduce) { el.textContent = target; return; }

    // one fixed-width cell per letter, measured in the banner face
    let html = '';
    for (let i = 0; i < target.length; i++) {
        const ch = target[i];
        html += '<span class="ch">' + (ch === '&' ? '&amp;' : ch === '<' ? '&lt;' : ch === '>' ? '&gt;' : ch) + '</span>';
    }
    el.innerHTML = html;
    const cells = Array.prototype.slice.call(el.querySelectorAll('.ch'));
    cells.forEach(c => { c.style.width = c.getBoundingClientRect().width + 'px'; });

    const run = () => {
        if (!document.body.contains(el)) return;
        el.classList.remove('banner-landed');
        const n = target.length, perChar = 4;
        let start = null;
        const tick = ts => {
            if (!document.body.contains(el)) return;
            if (start === null) start = ts;
            const resolved = Math.floor(((ts - start) / 45) / perChar);
            for (let i = 0; i < n; i++) {
                const cell = cells[i], ch = target[i];
                if (ch === ' ') continue;
                if (i < resolved) { if (cell.classList.contains('cipher')) { cell.classList.remove('cipher'); cell.textContent = ch; } }
                else { cell.classList.add('cipher'); cell.textContent = GLYPHS[Math.floor(Math.random() * GLYPHS.length)]; }
            }
            if (resolved < n) { window.__qxBannerRaf = requestAnimationFrame(tick); return; }
            window.__qxBannerRaf = null;
            el.classList.add('banner-landed');
            window.__qxBannerHold = setTimeout(run, HOLD_MS);
        };
        window.__qxBannerRaf = requestAnimationFrame(tick);
    };
    if (!el.dataset.clickWired) {
        el.dataset.clickWired = '1';
        el.addEventListener('click', () => { if (window.__qxBannerRaf) return; window.stopQuantumBanner(); run(); });
    }
    run();
};

// Quantum page (2026-09-14): the sweep panel renders BELOW the whole address table, so on a wallet with
// many rows a click on a row's "Sweep" looked like nothing happened. Bring the panel into view.
window.scrollQuantumPanel = function (id) {
    const el = document.getElementById(id);
    if (!el) return;
    const reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
};

window.stopQuantumBanner = function () {
    if (window.__qxBannerRaf) { cancelAnimationFrame(window.__qxBannerRaf); window.__qxBannerRaf = null; }
    if (window.__qxBannerHold) { clearTimeout(window.__qxBannerHold); window.__qxBannerHold = null; }
};
