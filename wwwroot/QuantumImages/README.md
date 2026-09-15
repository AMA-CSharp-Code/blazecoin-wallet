# Quantum Exposure tile sprites

`atom-tile-1.png` / `atom-tile-2.png` are the two frames of the pixel-art atom the Quantum page
tiles behind itself (40x40, drawn up to 240px cells with `image-rendering: pixelated`; the
electrons differ between frames and the page ticks between them once a second).

Swap in new art by replacing these two files with same-named PNGs. Any size works: the CSS
scales each frame to the 240px cell (`.skin-quantum-tiles .qx-tile-icons` in `css/wallet.css`).
Drop `image-rendering: pixelated` there if the replacement is smooth art rather than pixel art.
