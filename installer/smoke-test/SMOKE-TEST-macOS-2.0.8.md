# Smoke test — BlazecoinWalletV2-2.0.8-macOS-universal.zip

Run 2026-09-22 on macOS 27.0 (26A428), Apple M1 Pro, built with Xcode 26.4.
sha256 d2fcb9112f03d871bb4d51fcf91f90979f3edba24f8259e557609c8022d96202  (re-issued 2026-09-24 #5: x64 JIT, dark theme, caret)

## Passed
- Universal: app executable, blazecoind and blazecoin-cli all x86_64 + arm64.
- Installs to /Applications; ad-hoc signature verifies (`codesign --verify --deep --strict`).
- Entitlements embedded: app-sandbox=false, network.client=true, network.server=true.
- **6/6 consecutive launches** boot the Blazor UI and start the bundled node (0–4s).
- Node syncs; RPC listening on 127.0.0.1:55413 and [::1]:55413; connects to live
  peers (protocol 75000, network tip ~4,268,600).
- `blazecoin-cli getblockchaininfo` responds (chain "main").
- Quit (Cmd-Q) → node shuts down GRACEFULLY in 2–6s: mempool + fee estimates
  flushed, "Shutdown: done". No force-kill. Repeatable.
- Relaunch — including IMMEDIATELY after quit — brings the node back.
- Bundle carries REAL image assets (0 Git LFS pointer stubs; wwwroot 53 MB) — the
  script now refuses to package otherwise.
- V1.5 wallet IMPORT via the Import page (Andrew, 2026-09-24): real V1.5 wallet.dat →
  direct migration → 111 keys / 11 txs / 4 labels, 15 s migrate + 17 s rescan, no crash.
- Wallet ADOPTS an already-running node (relaunched app attached to the existing
  blazecoind, same pid, sync uninterrupted; that session's Cmd-Q leaves it running).
- PDF export works (Andrew, 2026-09-24): QuestPDF runs under the interpreter on Mac
  Catalyst; CSV/JSON/XLSX/PDF all land in ~/Downloads. Bundle is 192 MB zip / ~620 MB
  installed because of it (see README build notes).
- Initializer reaches the FULL DASHBOARD (~8s after launch) once a wallet exists:
  getnetworkinfo → listwallets → loadwallet("Primary") → Ready. RPC over cleartext
  http://127.0.0.1:55413 works from inside the Catalyst app (no ATS exemption needed).

## First launch on a fresh Mac — expected, not a bug
A new install has no wallet file, so the app shows "No wallet found" on a card that
uses the same shield art as the splash (easy to mistake for a stuck splash). It tells
the user to run `blazecoin-cli -named createwallet wallet_name="Primary"` — but on
macOS the CLI is inside the bundle, not on PATH. The command that actually works:

    "/Applications/Blazecoin Wallet V2.app/Contents/MacOS/blazecoin-cli" -named createwallet wallet_name="Primary"

then click "Check again". As of this build the card prints that full bundle path itself on macOS.

## Fixed during this test cycle (see git log on BlazecoinWallet.Maui main)
- App crashed 0.6s into launch: QuestPDF native dylibs missing for the maccatalyst
  RID → now lipo'd from the osx-arm64/osx-x64 slices into Contents/MonoBundle.
- App ran sandboxed: codesign dropped the entitlements → now signed with them and
  the script refuses to package without them.
- UI hung on ~half of launches on "Loading...": blazor.webview.js autostart raced
  MAUI's bridge injection → `autostart="false"` on the script tag (template default).
- Every skin image was a broken box: the clone had no git-lfs, so 1,000 LFS pointer
  stubs were bundled instead of images → git lfs pull + a pointer-stub guard in the
  script.

## Legacy (V1.5) wallet import — fixed in this build
The Import page failed on macOS for two reasons that are not the file: the daemon is
built --without-bdb so `restorewallet` cannot load a legacy wallet (-18, shown as
"wrong format or corrupted"), and a V1.5 written by the macOS port's BDB 18 is
btree version 10, which Core's BDB-less reader rejects (only 9). The import service
now copies the file into the daemon's wallet dir with its meta-page versions
normalised to 9 and runs `migratewallet` directly (BerkeleyRO). Verified on the real
V1.5 wallet: 111 keys / 11 txs / 4 labels reproduced exactly; BDB 4.8's db_verify and
db_dump agree with the original record-for-record. 8 unit tests cover the path.

## Re-issue 2026-09-24 (same tag v2.0.8-macos, assets replaced)
- App icon: Info.plist XSAppIconAssets now names the generated blazecoin_appicon.appiconset;
  Assets.car + CFBundleIconName present (the first upload showed the generic white icon).
- Startup no longer touches QuestPDF (its native lib needs macOS 15+); PDF export fails
  alone with a clear message on older Macs instead of the app quitting during load.

## Re-issue #2, 2026-09-24 (same tag, assets replaced)
- Intel Macs: the x86_64 slice now interprets everything (the static registrar jumped to
  uninitialised AOT entry points → SIGSEGV at launch). Found/verified on the Intel Mac;
  arm64 unchanged.
- Node launched with -txindex=1 — Provenance traces foreign ancestry (verified on Primary);
  default blazecoin.conf laid into the datadir on first run (Windows-installer parity).

## Re-issue #3, 2026-09-24
- First-run card has a one-click Create wallet "Primary" button (createwallet RPC on the node
  endpoint; Terminal command kept as fallback). Click path not exercised by me — needs a fresh
  datadir; RPC verified live.
- iOS status-bar spacer forced off: it painted a thin white bar above the nav on the Intel Mac.

## Re-issue #4, 2026-09-24
- Sync status shows Core's headers PRESYNC ("Pre-syncing headers · N of ~tip") and the headers
  download instead of "0 / 0 blocks · 0.00%" — on a fresh datadir that phase lasts ~20 min on
  an M1 and about an hour on an old Intel Mac, and read as "not syncing". Derived from
  getpeerinfo presynced_headers / startingheight; 3 tests.

## Re-issue #5, 2026-09-24
- Intel (x86_64) now runs the SDK default — Mono JIT, no AOT images, no interpreter — instead of
  interpreting everything (sluggish). QuestPDF's callback works under the JIT. arm64 unchanged.
  runtimeconfig.bin is architecture-specific (.xamarin/<rid>/) so the slices can differ and still
  merge into one universal app. Launched on both Macs.
- Dark theme forced (UserAppTheme) + black page/webview ground: the thin white bar above the nav
  on a LIGHT-mode Mac was the window background showing where the native title bar is taller
  than the 32px custom one.
- Dashboard typewriter caret hidden by default, shown only while typing (class-driven, idempotent).

## Still NOT verified
- INTEL SLICE at app level. macOS 27 removed Rosetta, so the x86_64 half cannot be
  exercised on this Mac. Binaries are universal and the Intel daemon slice ran
  before the OS upgrade, but the .app has only been run on arm64.
- Not notarized (no Apple Developer Program) → first launch needs right-click > Open.

## Re-issue #6 (2026-09-24, same tag) — Retina coins
- Dashboard header coins painted into a 48 × devicePixelRatio backing store (nearest-neighbour, pixel art kept); balance coin unchanged in style. Andrew chose this over smooth downsampling in a side-by-side and confirmed "That looks better".
- Built, hot-swapped on the M1 (node adopted) and the Intel Mac over SSH (node adopted), no crash reports on either.
- Assets replaced on v2.0.8-macos: sha256 41c785adcc348316868d410810a8266cea3e0057d5a7f3b67740bb6ce6928299 (132,216,663 bytes), public size = local.

## Re-issue #7 (2026-09-24, same tag) — Import/Backup page stalls on Intel
- Sampled on the Intel Mac: wallet main thread idle (98%); WebKit content process hot in per-frame style resolution, GPU process hot in software blur (vConvolveCore) and circle-mask rasterisation. Causes: Import sonar (width/height 0→250vmax + drop-shadow filter, x2) and Backup scanner beam (screen blend + blur/drop-shadow filter).
- Fix: sonar = fixed 50vmax ring, transform scale 0→5 + opacity, no filter; beam = gradient only; Import field per-image pulses removed. Same on the Dashboard skins.
- Built, hot-swapped on both Macs (nodes adopted, no crash reports). Assets replaced: sha256 225774b6d53fb0e0cfc9b6a44850698262c6260c68f8594bb21323d5ca126341 (132,217,356 bytes), public size = local.
- Andrew to confirm: Import/Backup page changes smooth on Intel, sonar pulse draws correctly. Not touched: Mining/Send tile grids animate filter: brightness per tile (~100 tiles) — next candidates if other pages still stall.

## Re-issue #8 (2026-09-24, same tag) — per-frame blur behind every page
- Andrew: "still sluggish but a lot better" after #7. Second WebKit sample: GPU process still in software blur (vConvolveCore), content process re-resolving animated styles. Cause: `.card { backdrop-filter: blur(10px) }` on every page over animated backgrounds, Mining `.stat` blur(4px), and tile pulses animating `filter: brightness` + scale (Mining, Send, 413/Mining/CoinPile skins, Backup grid).
- Fix: cards = solid translucent panel rgba(12,12,14,0.72) (look changes slightly: dark glass instead of frosted); stats 0.65 black; tile pulses opacity-only; Phoenix no-op shimmer removed.
- Built, hot-swapped on both Macs (nodes adopted, no crash reports). Assets replaced: sha256 0d24f59783c22cefb8dee742283ce5dfaddd364c2e9443ffb97d563ecf7c1ff1 (132,217,721 bytes), public size = local.
- Note: Intel node at 94% CPU in IBD (87%) at deploy time — remaining sluggishness partly that until it reaches the tip.

## Re-issue #9 (2026-09-24, same tag) — revert of #8
- Andrew: #8 "made no performance difference" on the Intel Mac and changed the card look → commit 9693a0d reverted (1660539). Frosted-glass cards, Mining stat blur, brightness/scale tile pulses and the Phoenix shimmer are back exactly as before. The #7 Import/Backup sonar/scanner fixes stay.
- Built, hot-swapped on both Macs (nodes adopted, no crash reports). Assets replaced: sha256 d430f6d0705e6b59181c7ceabb2473d1a209ea49cc18e9cb6c845b871b3b2489, public size = local.
- Lesson: the sample-level win (software blur gone from WebKit.GPU) did not translate into felt responsiveness while the node is in IBD; the node's CPU is the dominant factor on that machine.

## Re-issue #10 (2026-09-24, same tag) — images slow on Intel: cacheable asset scheme
- Andrew: "even the images are very slow loading" on Intel (node already synced). WebKit sample: content process dominated by WebP decode + resample; app process idle. MAUI's app:// handler sends `Cache-Control: no-store` (confirmed in the dll), so every page visit re-fetched and re-decoded every tile and the startup preloads were wasted.
- Fix: `blz://assets/` WKURLSchemeHandler (Catalyst only) serving wwwroot with max-age=1y; tile painters prefix via `window.__assetBase`; tiles `decoding='async'`; preloads fetch-only (no forced decode). Frosted cards etc. untouched.
- Verified via the new localStorage `asset_probe` on BOTH Macs: base blz://assets/, total 735, done 735, errors 0. No crashes. Assets replaced: sha256 52bb68f3ac605826a9c9b5be58d86f85e62abd755e63493de8b82da7704d68be.
- Andrew to confirm image loading / page changes on Intel.

## Re-issue #11 (2026-09-24, same tag) — ROOT CAUSE of the Intel sluggishness: integrated GPU
- Andrew: turning off "Automatic graphics switching" in System Settings "fixed it". nav_probe (new localStorage diagnostic) on the Intel Mac: Blazor page swap 3–56 ms every time; WebKit worst frame stall 3,916 ms / 710 ms before the setting, ≤139 ms (settled ~350 ms) after.
- Tested after a reboot with switching ON: Info.plist NSSupportsAutomaticGraphicsSwitching=false → no switch; Metal device held on the Radeon → no switch; OpenGL context requiring an online accelerated renderer (CGLChoosePixelFormat {kCGLPFAAccelerated, kCGLPFANoRecovery}) → display on Radeon within 10 s of launch, back to Intel GPU within 10 s of quit, again on relaunch. x86_64 + >1 GPU only. M1 unaffected.
- Built, hot-swapped on both Macs (no crash reports). Assets replaced: sha256 61d5b517dbefecfc61aa48e7b8237bbdca6bf6059db2f44cdb7a13d5e3739cea. Andrew can leave automatic switching on.

## Intel Mac: V1.5 wallet import VERIFIED (2026-09-24 14:44 local)
- Source `~/Library/Application Support/BlazecoinV1.5/wallet.dat` (BDB meta version 10 → direct migration path). Andrew picked a Desktop copy (`BlazecoinV1.5-wallet.dat`) because the Library folder is hidden in the picker.
- Result: wallet `V1.5_Import`, format sqlite/descriptors, balance 4.73954300 BLZ, 132 transactions, rescan of the last 79,754 blocks completed in 24 s, no crash. Core's pre-migration backup `V1.5_Import_<ts>.legacy.bak` left in the wallet dir (normal).

## Re-issue #12 (2026-09-24, same tag) — Quantum Exposure atoms jarring on Intel
- Cause: full-screen Retina canvas redrawn every frame with Canvas 2D shadowBlur on 7 fills per atom (software Gaussian) → low frame rate, time-based motion jumps.
- Fix: per-atom glow sprites rendered once at the canvas dpr (same radii/colours/blur), drawImage in the loop; ellipse strokes unchanged. Render-equivalent by construction; Andrew to confirm on Intel.
- Built, hot-swapped on both Macs (no crash reports; Intel display on the Radeon). Assets replaced: sha256 0430c70b5878d1352a3265d482296332aad8a4ec92c04afefbbb405c23ba30f8.

## Re-issue #13 (2026-09-24, same tag) — Quantum atoms: composited sprites (supersedes #12)
- #12 (per-atom sprite canvases) was WORSE on Intel: small canvases are software-backed, each stamp = upload. Phased probe attributed the cost: pausing the full-screen atom canvas took the page from 40.8 ms/frame (41 stalls >50 ms) to 18.7 ms.
- Fix: no full-screen canvas; per-atom body canvas rendered once + 3 electron sprites; frame loop writes CSS transforms only. Probe after: 18.9 ms avg, 2 stalls of 264. Scramble vs hold buckets: 18.5 vs 18.3 ms — the title decrypt is not costing frames (its 60 Hz glyph flicker is what reads as jitter).
- MISTAKE during this work: an edit anchored on a non-unique line cut ~1,400 lines of mining-background.js; that build reached the Intel Mac for a few minutes before being restored from git. Lesson: anchor edits inside the target function (search from its index) and always run the node parse check BEFORE building.
- Published sha256 3dfbbd36a29f7837a316204f56de30f8a1a0248a95910bbd3e89fc584d20bf29 (Intel + M1 installed, no crash reports).

## Re-issue #14 (2026-09-24, same tag) — FINAL for today
- Title decrypt: cipher glyphs re-randomised every 50 ms instead of every frame. Andrew: "it has calmed the jitter down, might be the best we can do". Residual: ~54 fps with 3–5% of frames at 33–54 ms on the Intel Mac, mostly the 195-tile drifting sheet (phased probe: pausing it gave the best figures). Left as is.
- Published sha256 51c57a6c852311cb6d06245c133278a1bed939465041c45d0691a4d6c4c51f09; installed on both Macs, no crash reports.
- State at end of day: Intel Mac fully working (discrete GPU requested in-app, cacheable assets, V1.5 import verified 4.7395 BLZ); M1 unchanged in behaviour. Diagnostics shipped: localStorage keys asset_probe, nav_probe, qx_probe (all passive).
