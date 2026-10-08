#!/usr/bin/env bash
# Build the macOS release of the Blazecoin V2 desktop wallet — ON A MAC.
#
#   1. blazecoind + blazecoin-cli as UNIVERSAL binaries (Apple Silicon + Intel) from the sibling
#      Blazecoin_Wallet_V2_Core tree via its own depends/ system (boost/libevent/sqlite built from source
#      and linked statically, so the shipped node needs no Homebrew at runtime), one build per arch,
#      joined with lipo. Same flags as the superproject's Tools/Build-LinuxDaemon.sh.
#   2. dotnet publish of the MAUI head for maccatalyst-x64;maccatalyst-arm64 — one universal .app.
#   3. The daemon/cli are placed INSIDE the bundle (Contents/MacOS, where DaemonProcessManager looks),
#      the bundle is renamed "Blazecoin Wallet V2.app", ad-hoc signed (no Apple Developer account, exactly
#      like the V1.5 macOS release), zipped with ditto (keeps bundle metadata), SHA256'd, release notes
#      drafted. NOTHING is uploaded — the `gh release create` command is printed at the end.
#
# This is the macOS twin of installer/Build-Installer.ps1 (Windows). It cannot run on Windows: Mac Catalyst
# bundling needs Xcode, and the daemon needs Apple's clang. Written 2026-09-22 on the Windows box and
# syntax-checked only (bash -n) — expect to fix small things on the first Mac run and commit them.
#
# One-time prerequisites on the Mac:
#   xcode-select --install
#   brew install automake libtool pkg-config cmake git-lfs   # cmake: libevent's depends; git-lfs: see below
#   git lfs install && (cd BlazecoinWallet.Maui && git lfs pull)   # the image sets live in Git LFS — a clone
#                                                                   # without it checks out 130-byte pointer stubs
#   .NET 10 SDK  (https://dotnet.microsoft.com)  then:  sudo dotnet workload install maui
#   both repos cloned side by side, e.g. ~/source/repos/BlazecoinWallet.Maui and ~/source/repos/Blazecoin_Wallet_V2_Core
#
# Usage (from the wallet repo root or anywhere):
#   installer/Build-MacRelease.sh                       # everything, version read from the csproj
#   installer/Build-MacRelease.sh --skip-daemon --daemon-dir ~/blz-daemon   # reuse prebuilt blazecoind/blazecoin-cli
#   installer/Build-MacRelease.sh --skip-publish        # reuse the last dotnet publish output
#   --version X.Y.Z   --core <path to Blazecoin_Wallet_V2_Core>   --out <dir>   --jobs N
#   --xcode <Xcode.app|Developer dir>   # override the auto-detected Xcode for the MAUI head
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WALLET_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CORE="${CORE:-$(cd "$WALLET_ROOT/.." && pwd)/Blazecoin_Wallet_V2_Core}"
VER=""
OUT=""
DAEMON_DIR=""
SKIP_DAEMON=0
SKIP_PUBLISH=0
XCODE=""            # Developer dir or Xcode.app to build the MAUI head with; auto-detected when empty
JOBS="$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

while [ $# -gt 0 ]; do
  case "$1" in
    --version)      VER="$2"; shift 2 ;;
    --core)         CORE="$2"; shift 2 ;;
    --out)          OUT="$2"; shift 2 ;;
    --daemon-dir)   DAEMON_DIR="$2"; shift 2 ;;
    --skip-daemon)  SKIP_DAEMON=1; shift ;;
    --skip-publish) SKIP_PUBLISH=1; shift ;;
    --xcode)        XCODE="$2"; shift 2 ;;
    --jobs)         JOBS="$2"; shift 2 ;;
    -h|--help)      sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[ "$(uname -s)" = "Darwin" ] || { echo "This script must run on macOS." >&2; exit 1; }
[ -f "$WALLET_ROOT/BlazecoinWallet.Maui.csproj" ] || { echo "wallet csproj not found under $WALLET_ROOT" >&2; exit 1; }

# Version = the csproj's ApplicationDisplayVersion unless overridden (same source of truth as Build-Installer.ps1).
if [ -z "$VER" ]; then
  VER="$(sed -n 's/.*<ApplicationDisplayVersion>\([^<]*\)<.*/\1/p' "$WALLET_ROOT/BlazecoinWallet.Maui.csproj" | head -1)"
fi
[ -n "$VER" ] || { echo "could not determine the version" >&2; exit 1; }

OUT="${OUT:-$HOME/blz-mac-release/$VER}"
WORK="$HOME/blz-mac-build-$VER"
# Shared across arches AND across runs — survives the per-arch rm -rf. Safe to delete to force a clean rebuild.
DEPENDS_CACHE="${DEPENDS_CACHE:-$HOME/blz-depends-cache}"
mkdir -p "$DEPENDS_CACHE/sources" "$DEPENDS_CACHE/built"
LOG="$WORK/build.log"
APP_NAME="Blazecoin Wallet V2"
ZIP="$OUT/BlazecoinWalletV2-$VER-macOS-universal.zip"
mkdir -p "$WORK" "$OUT"
: > "$LOG"
log() { echo "[$(date +%T)] $*" | tee -a "$LOG"; }
log "Blazecoin Wallet V2 macOS release $VER — work $WORK, out $OUT"

# ---------------------------------------------------------------------------------------------------------
# 1. Daemon — universal blazecoind + blazecoin-cli
# ---------------------------------------------------------------------------------------------------------
build_daemon_arch() {   # $1 = HOST triplet, builds in its own source copy, leaves src/<binary> there
  local host="$1" dir="$WORK/src-$1"
  log "[$host] export tracked tip of $CORE -> $dir"
  rm -rf "$dir"; mkdir -p "$dir"
  git -C "$CORE" archive --format=tar HEAD | tar -x -C "$dir"
  (
    cd "$dir"
    log "[$host] depends (boost, sqlite, libevent; no qt/zmq/upnp/bdb) -j$JOBS"
    # Downloads and built packages live OUTSIDE the per-arch source copy, which is wiped and re-exported
    # on every run: without this the second arch re-fetches boost (133 MB) and a re-run rebuilds from zero.
    # BASE_CACHE keys its entries by host, so both arches share one directory safely.
    make -C depends -j"$JOBS" HOST="$host" NO_QT=1 NO_ZMQ=1 NO_UPNP=1 NO_NATPMP=1 NO_BDB=1 NO_USDT=1 \
      SOURCES_PATH="$DEPENDS_CACHE/sources" BASE_CACHE="$DEPENDS_CACHE/built" >>"$LOG" 2>&1 \
      || { tail -40 "$LOG"; exit 1; }
    log "[$host] autogen + configure (daemon + cli only)"
    ./autogen.sh >>"$LOG" 2>&1
    CONFIG_SITE="$PWD/depends/$host/share/config.site" ./configure --without-gui --disable-tests --disable-bench \
      --disable-fuzz-binary --without-bdb --disable-zmq --without-miniupnpc --without-natpmp --with-sqlite=yes \
      --disable-wallet-tool --enable-reduce-exports >>"$LOG" 2>&1 || { tail -30 "$LOG"; exit 1; }
    log "[$host] make -j$JOBS"
    make -j"$JOBS" >>"$LOG" 2>&1 || { tail -40 "$LOG"; exit 1; }
  )
}

if [ "$SKIP_DAEMON" = "1" ]; then
  [ -n "$DAEMON_DIR" ] || DAEMON_DIR="$WORK/daemon"
  log "daemon build SKIPPED — using $DAEMON_DIR"
else
  [ -d "$CORE/depends" ] || { echo "core tree not found at $CORE (pass --core)" >&2; exit 1; }
  build_daemon_arch arm64-apple-darwin
  build_daemon_arch x86_64-apple-darwin
  DAEMON_DIR="$WORK/daemon"; rm -rf "$DAEMON_DIR"; mkdir -p "$DAEMON_DIR"
  # The autotools build keeps upstream's binary names (bitcoind / bitcoin-cli); the release ships them as
  # blazecoind / blazecoin-cli, exactly like the Windows and Linux bundles.
  for pair in "bitcoind:blazecoind" "bitcoin-cli:blazecoin-cli"; do
    up="${pair%%:*}"; ours="${pair##*:}"
    a="$WORK/src-arm64-apple-darwin/src/$ours";  [ -x "$a" ] || a="$WORK/src-arm64-apple-darwin/src/$up"
    x="$WORK/src-x86_64-apple-darwin/src/$ours"; [ -x "$x" ] || x="$WORK/src-x86_64-apple-darwin/src/$up"
    lipo -create "$a" "$x" -output "$DAEMON_DIR/$ours"
    strip "$DAEMON_DIR/$ours"
    log "universal $ours: $(lipo -info "$DAEMON_DIR/$ours" | sed 's/.*are: //')"
  done
fi
for f in blazecoind blazecoin-cli; do
  [ -x "$DAEMON_DIR/$f" ] || { echo "$f missing or not executable in $DAEMON_DIR" >&2; exit 1; }
done
"$DAEMON_DIR/blazecoind" --version | head -1 | tee -a "$LOG"
DAEMON_VER="$("$DAEMON_DIR/blazecoind" --version | head -1 | sed 's/.*version //')"

# The Xcode major.minor that .NET for MacCatalyst demands, read from the installed SDK pack name
# (e.g. Microsoft.MacCatalyst.Sdk.net10.0_26.4 -> 26.4). Highest installed pack wins.
DOTNET_ROOT_DIR="$(dirname "$(readlink -f "$(command -v dotnet)" 2>/dev/null || command -v dotnet)")"
XCODE_WANT="$(ls -d "$DOTNET_ROOT_DIR"/packs/Microsoft.MacCatalyst.Sdk.net*_* 2>/dev/null | sed 's/.*_//' | sort -V | tail -1)"
[ -n "$XCODE_WANT" ] || XCODE_WANT="26.4"

find_matching_xcode() {   # $1 = wanted major.minor; echoes a Developer dir
  local want="$1" app v
  for app in /Applications/Xcode*.app "$HOME"/Applications/Xcode*.app; do
    [ -d "$app" ] || continue
    v="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist" 2>/dev/null)"
    case "$v" in "$want"|"$want".*) echo "$app/Contents/Developer"; return 0 ;; esac
  done
  return 1
}

# ---------------------------------------------------------------------------------------------------------
# 2. Wallet — one universal .app
# ---------------------------------------------------------------------------------------------------------
PUB_ROOT="$WALLET_ROOT/bin/Release/net10.0-maccatalyst"
STAMP="$WORK/publish.stamp"
if [ "$SKIP_PUBLISH" = "1" ]; then
  log "dotnet publish SKIPPED — reusing $PUB_ROOT"
else
  touch "$STAMP"
  # ALWAYS publish from clean. An incremental publish after a change in a REFERENCED project
  # (BlazecoinWallet.Core) shipped the new DLL next to stale *.aotdata, and the app aborted at
  # launch inside Mono's load_aot_module (2026-09-23). A release build is not the place to save
  # a minute; correctness of the AOT pairing is only guaranteed from a clean output.
  rm -rf "$WALLET_ROOT/bin/Release/net10.0-maccatalyst" "$WALLET_ROOT/obj/Release/net10.0-maccatalyst"
  log "dotnet publish (maccatalyst-x64;maccatalyst-arm64, Release, CLEAN, no signing — ad-hoc signed below)"
  # RuntimeIdentifiers is NOT passed here: on the command line it is a global property that also reaches the
  # referenced net10.0 libraries, which reject a Mac Catalyst RID (NETSDK1083). The csproj declares it for
  # the maccatalyst head instead, so both arches still land in one universal .app.
  # DO NOT "fix" an Xcode mismatch with -p:ValidateXcodeVersion=false. That check is load-bearing: on
  # 2026-09-22 the bypass built, linked, signed and packaged perfectly against Xcode 27, and the resulting
  # app died ~0.6s into launch (EXC_BREAKPOINT in __UIApplicationEvaluateRuntimeIssueForNoSceneLifecycle-
  # Adoption) because SDK macosx27.0 enforces UIKit scene-lifecycle adoption and MAUI 26.4 emits no
  # UIApplicationSceneManifest. A clean build proves nothing here — only launching the bundle does.
  # So: find the Xcode whose major.minor matches the installed MacCatalyst pack and point DEVELOPER_DIR at
  # it for this publish only. No sudo, and the machine's global xcode-select is left alone.
  if [ -z "$XCODE" ]; then
    XCODE="$(find_matching_xcode "$XCODE_WANT" || true)"
    [ -n "$XCODE" ] || { echo "No Xcode $XCODE_WANT found (looked in /Applications and ~/Applications).
.NET for MacCatalyst $XCODE_WANT requires it; get it from https://developer.apple.com/download/all and
install alongside your current Xcode, or pass --xcode /path/to/Xcode-$XCODE_WANT.app" >&2; exit 1; }
  fi
  case "$XCODE" in *.app) XCODE="$XCODE/Contents/Developer" ;; esac
  [ -x "$XCODE/usr/bin/xcodebuild" ] || { echo "not an Xcode developer dir: $XCODE" >&2; exit 1; }
  log "building the MAUI head with Xcode $("$XCODE/usr/bin/xcodebuild" -version 2>/dev/null | head -1 | sed 's/Xcode //') at $XCODE"
  ( cd "$WALLET_ROOT" && DEVELOPER_DIR="$XCODE" dotnet publish BlazecoinWallet.Maui.csproj -f net10.0-maccatalyst -c Release \
      -p:CreatePackage=false \
      -p:EnableCodeSigning=false -p:UseHardenedRuntime=false --nologo -v:minimal >>"$LOG" 2>&1 ) \
    || { tail -40 "$LOG"; exit 1; }
fi
# Pick the .app to ship. A multi-RID publish writes the UNIVERSAL bundle at the ROOT of $PUB_ROOT and
# single-arch copies under maccatalyst-x64/ and maccatalyst-arm64/ — a bare -maxdepth 4 search can return
# one of those instead, which would ship a half-architecture app labelled universal. So: root first.
# The -newer "$STAMP" test alone is also wrong: an incremental publish that rewrites nothing leaves every
# bundle older than the stamp and the run aborted with "no .app bundle found" (2026-09-22, first Mac run).
APP="$(find "$PUB_ROOT" -maxdepth 1 -type d -name '*.app' | head -1)"
[ -n "$APP" ] || APP="$(find "$PUB_ROOT" -maxdepth 4 -type d -name '*.app' -path '*publish*' | head -1)"
[ -n "$APP" ] || APP="$(find "$PUB_ROOT" -maxdepth 4 -type d -name '*.app' | head -1)"
[ -n "$APP" ] && [ -d "$APP" ] || { echo "no .app bundle found under $PUB_ROOT" >&2; exit 1; }
log "publish output: $APP"

# Belt and braces: this script exists to produce a universal build, so refuse to package anything else.
APP_EXE_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist" 2>/dev/null)"
APP_EXE="$APP/Contents/MacOS/$APP_EXE_NAME"
APP_ARCHS="$(lipo -info "$APP_EXE" 2>/dev/null | sed -e 's/.*are: //' -e 's/.*is architecture: //')"
log "app executable architectures: $APP_ARCHS"
case " $APP_ARCHS " in
  *" x86_64 "*) case " $APP_ARCHS " in *" arm64 "*) : ;; *) APP_ARCHS_BAD=1 ;; esac ;;
  *) APP_ARCHS_BAD=1 ;;
esac
[ -z "${APP_ARCHS_BAD:-}" ] || { echo "REFUSING to package: $APP_EXE is not universal (found: $APP_ARCHS)" >&2; exit 1; }

# Git LFS pointer stubs. This repo keeps every image set in LFS; a clone made without git-lfs checks out
# ~130-byte pointer TEXT files, dotnet publish bundles them as if they were images, and every skin renders
# as a broken-image box (2026-09-22, first Mac run: 1,000 stubs shipped). Content check, not just arch.
# (grep exits 1 on NO match — under set -e/pipefail that would abort the script silently in the healthy case.)
STUBS="$({ grep -rl 'git-lfs.github.com/spec' "$APP/Contents/Resources" 2>/dev/null || true; } | wc -l | tr -d ' ')"
if [ "$STUBS" != "0" ]; then
  echo "REFUSING to package: $STUBS Git LFS pointer stub(s) in the bundle instead of real files." >&2
  echo "Fix: brew install git-lfs && git lfs install && (cd \"$WALLET_ROOT\" && git lfs pull), then re-run." >&2
  { grep -rl 'git-lfs.github.com/spec' "$APP/Contents/Resources" 2>/dev/null || true; } | head -5 | sed 's/^/  e.g. /' >&2 || true
  exit 1
fi
log "bundle content check: no Git LFS pointer stubs"

# ---------------------------------------------------------------------------------------------------------
# 3. Assemble, sign, zip
# ---------------------------------------------------------------------------------------------------------
STAGE="$WORK/stage"; rm -rf "$STAGE"; mkdir -p "$STAGE"
BUNDLE="$STAGE/$APP_NAME.app"
ditto "$APP" "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp "$DAEMON_DIR/blazecoind" "$DAEMON_DIR/blazecoin-cli" "$BUNDLE/Contents/MacOS/"
chmod 755 "$BUNDLE/Contents/MacOS/blazecoind" "$BUNDLE/Contents/MacOS/blazecoin-cli"

# QuestPDF's native libraries. The package ships them per-RID for osx-arm64/osx-x64 but NOT for the
# maccatalyst RIDs, so NuGet copies nothing and only the managed QuestPDF.dll lands in the bundle.
# MauiProgram sets QuestPDF.Settings.License at startup, whose static ctor runs a native-dependency
# check — with no dylib that throws DllNotFoundException("QuestPdfSkia"), surfacing as
# TypeInitializationException and killing the app ~0.6s into launch, before any UI (2026-09-22).
# Mac Catalyst is macOS underneath and loads these platform-1 dylibs happily, so lipo the two osx
# slices into universal ones and drop them beside the managed assembly. Must happen BEFORE codesign.
QPDF_VER="$(sed -n 's/.*PackageReference Include="QuestPDF" Version="\([^"]*\)".*/\1/p' "$WALLET_ROOT/BlazecoinWallet.Maui.csproj" | head -1)"
QPDF_RT="$HOME/.nuget/packages/questpdf/$QPDF_VER/runtimes"
if [ -n "$QPDF_VER" ] && [ -d "$QPDF_RT" ]; then
  for lib in libQuestPdfSkia libqpdf; do
    a="$QPDF_RT/osx-arm64/native/$lib.dylib"; x="$QPDF_RT/osx-x64/native/$lib.dylib"
    if [ -f "$a" ] && [ -f "$x" ]; then
      lipo -create "$a" "$x" -output "$BUNDLE/Contents/MonoBundle/$lib.dylib"
      chmod 755 "$BUNDLE/Contents/MonoBundle/$lib.dylib"
      log "QuestPDF native $lib.dylib: $(lipo -info "$BUNDLE/Contents/MonoBundle/$lib.dylib" | sed 's/.*are: //')"
    else
      echo "QuestPDF $lib.dylib missing for osx-arm64/osx-x64 under $QPDF_RT" >&2; exit 1
    fi
  done
else
  echo "could not locate the QuestPDF package (version '$QPDF_VER') under ~/.nuget — the app WILL crash at launch" >&2
  exit 1
fi
# The default conf is a convenience, not a requirement: the seed nodes are baked into chainparams and the
# wallet talks to the node over the datadir .cookie. Users who want one copy it to the datadir.
cp "$CORE/share/examples/blazecoin.conf" "$BUNDLE/Contents/Resources/blazecoin.conf.example" 2>/dev/null \
  || log "note: $CORE/share/examples/blazecoin.conf not found, no conf example bundled"

# --entitlements is NOT optional. Mac Catalyst apps are SANDBOXED BY DEFAULT, and signing without the
# entitlements plist silently drops com.apple.security.app-sandbox=false. A sandboxed wallet cannot spawn
# blazecoind against ~/Library/Application Support/BlazecoinV2.0 or bind 55413/55414, and the Blazor
# WebView never boots — the app just sits on its splash showing "Loading..." with no node, while the log
# fills with "sandbox_extension_issue_file ... Operation not permitted" (2026-09-22, first Mac run).
ENTITLEMENTS="$WALLET_ROOT/Platforms/MacCatalyst/Entitlements.plist"
[ -f "$ENTITLEMENTS" ] || { echo "entitlements not found: $ENTITLEMENTS" >&2; exit 1; }
log "ad-hoc codesign (deep — covers the nested node binaries) with $ENTITLEMENTS"
# Sign nested binaries first (they take no entitlements), then the bundle WITH them: --deep does not
# apply --entitlements to nested code, and re-signing the outer bundle last keeps the seal valid.
for nested in "$BUNDLE/Contents/MacOS/blazecoind" "$BUNDLE/Contents/MacOS/blazecoin-cli" "$BUNDLE/Contents/MonoBundle/"*.dylib; do
  [ -f "$nested" ] && codesign --force --sign - "$nested" >>"$LOG" 2>&1
done
codesign --force --deep --sign - --entitlements "$ENTITLEMENTS" "$BUNDLE" >>"$LOG" 2>&1
codesign --verify --deep --strict "$BUNDLE" >>"$LOG" 2>&1 && log "codesign verify: OK"
# Prove the entitlements actually stuck — an empty result here means a sandboxed, non-working wallet.
if codesign -d --entitlements - --xml "$BUNDLE" 2>/dev/null | grep -q "app-sandbox"; then
  log "entitlements embedded: app-sandbox present"
else
  echo "REFUSING to package: no entitlements embedded — the app would run sandboxed and fail to start its node" >&2
  exit 1
fi

rm -f "$ZIP"
ditto -c -k --sequesterRsrc --keepParent "$BUNDLE" "$ZIP"
( cd "$OUT" && shasum -a 256 "$(basename "$ZIP")" > SHA256SUMS )
SHA="$(cut -d' ' -f1 "$OUT/SHA256SUMS")"
log "packaged $(du -h "$ZIP" | cut -f1) -> $ZIP"
log "sha256 $SHA"

# ---------------------------------------------------------------------------------------------------------
# 4. Release notes draft (same voice as the Windows notes + the V1.5 macOS notes; edit before publishing)
# ---------------------------------------------------------------------------------------------------------
cat > "$OUT/RELEASE_NOTES.md" <<EOF
# Blazecoin Wallet $VER (macOS)

**What this is:** the first macOS build of the V2 desktop wallet — the same wallet as the Windows release,
as a **universal** app (Apple Silicon and Intel), **bundling Blazecoin Core $DAEMON_VER**, the post-quantum
fork node. It runs a full node on your Mac; the wallet talks to it locally, nothing leaves your machine
except normal peer-to-peer traffic.

### Download & verify

- **\`$(basename "$ZIP")\`** — the wallet application

Verify the download is intact:

\`\`\`
shasum -a 256 $(basename "$ZIP")
\`\`\`

Expected:

\`\`\`
$SHA
\`\`\`

### Install & first launch

This build is ad-hoc signed (not notarized — there is no Apple Developer account behind it), so macOS
Gatekeeper blocks the first launch. Unzip the download and move **$APP_NAME.app** into **/Applications**,
then get past Gatekeeper one of two ways:

**macOS 15 (Sequoia) and later** — double-click the app, click **Done** on the "Apple could not verify…"
dialog, open **System Settings → Privacy & Security**, scroll down to **Security**, click **Open Anyway**
next to the app and confirm. (On macOS 12–14: right-click the app → **Open** → **Open**.)

**Any version, one Terminal command** — removes the download's quarantine flag:

\`\`\`
xattr -dr com.apple.quarantine "/Applications/$APP_NAME.app"
\`\`\`

### Notes for users

- Wallet and chain data live at \`~/Library/Application Support/BlazecoinV2.0/\`. The wallet starts the
  bundled node on launch and stops it cleanly when you quit; a node you started yourself is left alone
  (and then keeps running after you quit — stop it with the command below if you need to).
- First launch downloads and verifies the full chain — allow a few hours. While headers are still
  loading the dashboard shows \`0 / <headers so far>\` blocks; the block count starts moving once all
  headers are in.
- A brand-new install has no wallet yet, so the first screen says **No wallet found** — click
  **Create wallet "Primary"** and the app makes one. (The card also shows the equivalent Terminal
  command; the node's tools live inside the app bundle, which is why it carries the full path:

  \`\`\`
  "/Applications/$APP_NAME.app/Contents/MacOS/blazecoin-cli" -named createwallet wallet_name="Primary"
  \`\`\`

  Then click **Check again**. (\`… blazecoin-cli stop\` stops the node the same way.)
- **Bringing a V1.5 wallet across:** Import page → choose your V1.5 \`wallet.dat\` (on a Mac it is at
  \`~/Library/Application Support/BlazecoinV1.5/wallet.dat\`; in the file dialog press ⌘⇧G and paste the
  path). It is converted to the V2 descriptor format and rescanned from where V1.5 left off; your V1.5
  file is not modified and a \`.legacy.bak\` copy is kept beside the new wallet.
- Back up your wallet (Backup page) after you receive coins.

### Known limitations

- Requires macOS 12 (Monterey) or newer.
- The Backup page has no folder picker on macOS — type or paste the destination path.
- Transaction exports save straight to \`~/Downloads\`.
EOF

cat <<EOF

Done. Release folder: $OUT
  $(basename "$ZIP")   ($SHA)
  SHA256SUMS
  RELEASE_NOTES.md      <- read and edit before publishing

Smoke test BEFORE publishing (clean user account or a second Mac if possible):
  1. unzip, right-click > Open; the wallet should launch and start the node (Activity Monitor: blazecoind)
  2. ~/Library/Application Support/BlazecoinV2.0/debug.log appears and the status bar starts syncing
  3. quit with Cmd-Q; blazecoind should exit within ~15 s (check Activity Monitor)
  4. relaunch: the node comes back and the wallet reconnects

Publish (from a machine with gh authed as AMA-CSharp-Code; tag on the PUBLIC mirror's main, see wallet-release-kit):
  gh release create v$VER-macos "$ZIP" "$OUT/SHA256SUMS" \\
    -R AMA-CSharp-Code/blazecoin-wallet --target main \\
    --title "Blazecoin Wallet $VER (macOS)" --notes-file "$OUT/RELEASE_NOTES.md"
EOF
