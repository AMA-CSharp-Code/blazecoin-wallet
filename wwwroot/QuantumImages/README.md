# Quantum Exposure page background art

`atoms/atom-01.webp` to `atoms/atom-30.webp` are the rendered atoms the Quantum page lays out as a mosaic of
240px tiles behind itself (crimson grout between cells, the whole sheet drifting bottom-left to top-right, a
canvas of drawn atoms streaming over it). Source: Andrew's ComfyUI set `ATOM FINAL` (2026-09-12 to 14), 1024x1024
PNGs resized to 480x480 WebP (quality 82, about 58 KB each) so they stay sharp on high-DPI screens without
bloating the installer. Four light-ground images from that set (17257, 18198, 19850, 20152) are deliberately not
used: they flash white on the dark page.

- **Swap an image:** replace a file with a same-named 480x480 WebP.
- **Add or remove images:** change the count and `QX_TILE_ORDER` (36 slots, a 6x6 repeating pattern; keep each
  index away from its own neighbours, wrap included) in `initQuantumBackground` in `js/mining-background.js`.
- **Look:** tile opacity, grout and drift speed live in `.skin-quantum-tiles` in `css/wallet.css`. The drift must
  move exactly one pattern period (6 tiles = 1440px) per loop or the loop jumps.

## Originals

All 34 full-size masters from the `ATOM FINAL` set (1024x1024 PNG, about 59 MB) are kept beside the WebP copies
in `atoms/` under their ComfyUI names, stored with Git LFS (`.gitattributes`) and excluded from the build
(`<Content Remove>` in `BlazecoinWallet.Maui.csproj`), the same arrangement as the other skin folders. The four
light-ground masters (17257, 18198, 19850, 20152) are kept too, although no WebP is made from them.

To regenerate a WebP: resize the master to 480x480 (Lanczos) and save as WebP quality 82.

| Shipped file | Master |
|---|---|
| `atom-01.webp` | `ComfyUI_16984_.png` |
| `atom-02.webp` | `ComfyUI_17091_.png` |
| `atom-03.webp` | `ComfyUI_17099_.png` |
| `atom-04.webp` | `ComfyUI_17168_.png` |
| `atom-05.webp` | `ComfyUI_17173_.png` |
| `atom-06.webp` | `ComfyUI_17227_.png` |
| `atom-07.webp` | `ComfyUI_17228_.png` |
| `atom-08.webp` | `ComfyUI_17268_.png` |
| `atom-09.webp` | `ComfyUI_17302_.png` |
| `atom-10.webp` | `ComfyUI_17516_.png` |
| `atom-11.webp` | `ComfyUI_17739_.png` |
| `atom-12.webp` | `ComfyUI_17776_.png` |
| `atom-13.webp` | `ComfyUI_17783_.png` |
| `atom-14.webp` | `ComfyUI_17819_.png` |
| `atom-15.webp` | `ComfyUI_17944_.png` |
| `atom-16.webp` | `ComfyUI_18308_.png` |
| `atom-17.webp` | `ComfyUI_18346_.png` |
| `atom-18.webp` | `ComfyUI_18385_.png` |
| `atom-19.webp` | `ComfyUI_18585_.png` |
| `atom-20.webp` | `ComfyUI_18827_.png` |
| `atom-21.webp` | `ComfyUI_18844_.png` |
| `atom-22.webp` | `ComfyUI_18876_.png` |
| `atom-23.webp` | `ComfyUI_18889_.png` |
| `atom-24.webp` | `ComfyUI_18954_.png` |
| `atom-25.webp` | `ComfyUI_19435_.png` |
| `atom-26.webp` | `ComfyUI_19653_.png` |
| `atom-27.webp` | `ComfyUI_19656_.png` |
| `atom-28.webp` | `ComfyUI_19869_.png` |
| `atom-29.webp` | `ComfyUI_19875_.png` |
| `atom-30.webp` | `ComfyUI_19934_.png` |
