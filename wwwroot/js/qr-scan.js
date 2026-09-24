// QR scanning for the Send page. Decoding is done entirely here in JS (jsQR
// runs on raw canvas pixel data), so the C# side only ever receives the final
// decoded string via the DotNet ref. Two inputs share one decode path:
//   * live webcam  — getUserMedia -> <video> -> per-frame grab -> jsQR
//   * image file   — chosen picture -> <img> -> canvas -> jsQR  (camera-less fallback)
// One active scanner at a time (the single Send page); state is module-local.
window.blzQr = (function () {
    let stream = null;       // active MediaStream (camera), if any
    let raf = null;          // requestAnimationFrame handle for the grab loop
    let stopped = true;      // loop guard
    let ref = null;          // DotNetObjectReference<Send>
    let fileInput = null;    // the "choose image" <input type=file>
    let fileHandler = null;  // its change listener (kept so we can detach it)

    function decode(ctx, w, h) {
        const img = ctx.getImageData(0, 0, w, h);
        const code = window.jsQR ? window.jsQR(img.data, w, h, { inversionAttempts: "attemptBoth" }) : null;
        return code && code.data ? code.data : null;
    }

    // ---- Live camera ----
    async function startCamera(videoId, canvasId) {
        stopCamera();
        stopped = false;
        const video = document.getElementById(videoId);
        const canvas = document.getElementById(canvasId);
        if (!video || !canvas) return "no-elements";
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return "no-camera";

        try {
            stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
        } catch (e) {
            // NotAllowedError = blocked, NotFoundError = no device, etc.
            return (e && e.name === "NotFoundError") ? "no-camera" : "camera-denied";
        }

        video.srcObject = stream;
        video.muted = true;
        video.setAttribute("playsinline", "true");
        try { await video.play(); } catch { /* autoplay quirk — frames still arrive */ }

        const ctx = canvas.getContext("2d", { willReadFrequently: true });
        const tick = () => {
            if (stopped) return;
            if (video.readyState >= video.HAVE_CURRENT_DATA && video.videoWidth) {
                canvas.width = video.videoWidth;
                canvas.height = video.videoHeight;
                ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                const text = decode(ctx, canvas.width, canvas.height);
                if (text) {
                    stopCamera();
                    if (ref) ref.invokeMethodAsync("OnQrDecoded", text);
                    return;
                }
            }
            raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
        return "ok";
    }

    function stopCamera() {
        stopped = true;
        if (raf) { cancelAnimationFrame(raf); raf = null; }
        if (stream) { stream.getTracks().forEach(t => t.stop()); stream = null; }
    }

    // ---- Image-file fallback ----
    function decodeSelectedFile() {
        if (!fileInput || !fileInput.files || !fileInput.files[0]) return;
        const file = fileInput.files[0];
        const url = URL.createObjectURL(file);
        const image = new Image();
        image.onload = () => {
            const canvas = document.createElement("canvas");
            canvas.width = image.naturalWidth;
            canvas.height = image.naturalHeight;
            const ctx = canvas.getContext("2d", { willReadFrequently: true });
            ctx.drawImage(image, 0, 0);
            const text = decode(ctx, canvas.width, canvas.height);
            URL.revokeObjectURL(url);
            fileInput.value = "";   // allow re-picking the same file
            if (ref) ref.invokeMethodAsync(text ? "OnQrDecoded" : "OnQrError",
                text || "No QR code found in that image.");
        };
        image.onerror = () => {
            URL.revokeObjectURL(url);
            fileInput.value = "";
            if (ref) ref.invokeMethodAsync("OnQrError", "Couldn't read that image file.");
        };
        image.src = url;
    }

    // ---- Lifecycle ----
    function attach(dotnetRef, fileInputId) {
        ref = dotnetRef;
        fileInput = document.getElementById(fileInputId);
        if (fileInput) {
            fileHandler = () => decodeSelectedFile();
            fileInput.addEventListener("change", fileHandler);
        }
    }

    function detach() {
        stopCamera();
        if (fileInput && fileHandler) fileInput.removeEventListener("change", fileHandler);
        fileInput = null;
        fileHandler = null;
        ref = null;
    }

    return { attach, detach, startCamera, stopCamera };
})();
