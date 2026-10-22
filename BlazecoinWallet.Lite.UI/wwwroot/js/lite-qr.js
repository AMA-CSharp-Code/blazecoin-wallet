// Self-contained QR scanner for the lite wallet's Send page. Decoding runs entirely here
// (jsQR over raw canvas pixels), so the C# IQrScanner only ever receives the final decoded
// string. `scan()` builds its OWN fullscreen overlay and resolves a Promise with the decoded
// text (a bare address or a blazecoin:/BIP21 URI — the Send page parses either) or null when
// cancelled. Works in any WebView/browser with a camera (so it's testable in the dev host);
// an "Upload image" fallback decodes a chosen picture when the camera is denied or absent.
// jsQR is vendored alongside and loaded on demand (it's a ~250 KB UMD global).

let _jsqr = null;
function ensureJsQR() {
    if (window.jsQR) return Promise.resolve();
    if (!_jsqr) {
        _jsqr = new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = '_content/BlazecoinWallet.Lite.UI/js/jsqr.js';
            s.onload = resolve;
            s.onerror = () => reject(new Error('jsQR failed to load'));
            document.head.appendChild(s);
        });
    }
    return _jsqr;
}

function decodeCanvas(ctx, w, h) {
    const img = ctx.getImageData(0, 0, w, h);
    const code = window.jsQR ? window.jsQR(img.data, w, h, { inversionAttempts: 'attemptBoth' }) : null;
    return code && code.data ? code.data : null;
}

export async function scan() {
    try { await ensureJsQR(); } catch { return null; }

    // ── Overlay chrome (created here so no page markup is needed) ──────────────────────────
    const overlay = document.createElement('div');
    overlay.className = 'lite-qr-overlay';

    const video = document.createElement('video');
    video.className = 'lite-qr-video';
    video.setAttribute('playsinline', 'true');
    video.muted = true;

    const reticle = document.createElement('div'); reticle.className = 'lite-qr-reticle';
    const hint = document.createElement('div'); hint.className = 'lite-qr-hint';
    hint.textContent = 'Point the camera at a Blazecoin QR code';

    const bar = document.createElement('div'); bar.className = 'lite-qr-bar';
    const uploadBtn = document.createElement('button'); uploadBtn.className = 'lite-btn lite-btn-ghost'; uploadBtn.textContent = '🖼 Upload image';
    const cancelBtn = document.createElement('button'); cancelBtn.className = 'lite-btn'; cancelBtn.textContent = 'Cancel';
    bar.append(uploadBtn, cancelBtn);

    const file = document.createElement('input');
    file.type = 'file'; file.accept = 'image/*'; file.style.display = 'none';

    overlay.append(video, reticle, hint, bar, file);
    document.body.appendChild(overlay);

    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });

    let stream = null, raf = null, done = false;

    return new Promise((resolve) => {
        const finish = (result) => {
            if (done) return;
            done = true;
            if (raf) cancelAnimationFrame(raf);
            if (stream) stream.getTracks().forEach(t => t.stop());
            overlay.remove();
            resolve(result);
        };

        cancelBtn.addEventListener('click', () => finish(null));
        uploadBtn.addEventListener('click', () => file.click());
        file.addEventListener('change', () => {
            const f = file.files && file.files[0];
            if (!f) return;
            const url = URL.createObjectURL(f);
            const image = new Image();
            image.onload = () => {
                canvas.width = image.naturalWidth; canvas.height = image.naturalHeight;
                ctx.drawImage(image, 0, 0);
                URL.revokeObjectURL(url);
                const text = decodeCanvas(ctx, canvas.width, canvas.height);
                if (text) finish(text);
                else { hint.textContent = 'No QR code found in that image — try another.'; file.value = ''; }
            };
            image.onerror = () => { URL.revokeObjectURL(url); hint.textContent = "Couldn't read that image."; file.value = ''; };
            image.src = url;
        });

        // ── Live camera (best-effort; the upload fallback covers denial/no-camera) ──────────
        (async () => {
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                hint.textContent = 'No camera here — upload a QR image instead.';
                return;
            }
            try {
                stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
            } catch (err) {
                // Name the failure: NotAllowedError = permission layer, NotFoundError /
                // NotReadableError = device layer — invaluable when diagnosing a head.
                const reason = err && err.name ? ' (' + err.name + ')' : '';
                hint.textContent = 'Camera unavailable' + reason + ' — upload a QR image instead.';
                return;
            }
            video.srcObject = stream;
            try { await video.play(); } catch { /* autoplay quirk — frames still arrive */ }
            const tick = () => {
                if (done) return;
                if (video.readyState >= video.HAVE_CURRENT_DATA && video.videoWidth) {
                    canvas.width = video.videoWidth; canvas.height = video.videoHeight;
                    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                    const text = decodeCanvas(ctx, canvas.width, canvas.height);
                    if (text) { finish(text); return; }
                }
                raf = requestAnimationFrame(tick);
            };
            raf = requestAnimationFrame(tick);
        })();
    });
}
