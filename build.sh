#!/usr/bin/env bash
#
# Build, smoke-test and deploy Supervertaler for memoQ.
#
#   bash build.sh            build + smoke test + deploy
#   bash build.sh --no-deploy build + smoke test only
#
# Deployment differs from the Trados plugin in one way that matters: memoQ probes
# for add-ins in its own install directory (memoQ.exe.config declares
# privatePath="DocConverters;Addins;CefSharp;x64"), which lives under Program
# Files. There is no per-user add-in folder. So this script needs an elevated
# shell to copy, and the path is version-stamped — a memoQ 13 upgrade means a new
# directory and a fresh deploy.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$ROOT/Supervertaler.MemoQ.sln"
CONFIG="Release"
DEPLOY=1

for arg in "$@"; do
    case "$arg" in
        --no-deploy) DEPLOY=0 ;;
        --debug)     CONFIG="Debug" ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

# --- locate memoQ -----------------------------------------------------------
# Highest version number wins, so a machine with memoQ-12 and memoQ-13 side by
# side builds against the newer one.
MEMOQ_ROOT="/c/Program Files/memoQ"
MEMOQ_DIR="$(ls -d "$MEMOQ_ROOT"/memoQ-* 2>/dev/null | sort -V | tail -1 || true)"

if [[ -z "$MEMOQ_DIR" ]]; then
    echo "ERROR: no memoQ installation found under $MEMOQ_ROOT" >&2
    exit 1
fi

MEMOQ_WIN="$(cygpath -w "$MEMOQ_DIR")"
echo "memoQ:  $MEMOQ_WIN"

# --- memoQ must be closed ---------------------------------------------------
# A loaded add-in DLL is locked by memoQ.exe; copying over it fails with a
# confusing sharing violation rather than anything that names the cause.
if tasklist.exe //FI "IMAGENAME eq memoQ.exe" 2>/dev/null | grep -qi "memoQ.exe"; then
    echo "ERROR: memoQ is running. Close it before building — it locks the add-in DLL." >&2
    exit 1
fi

# --- build ------------------------------------------------------------------
echo
# Two Git Bash quirks in one line:
#   -p: not /p:   — a leading slash is rewritten into a Windows path, so
#                   /p:MemoQPath=... arrives as a second "project" argument.
#   MSYS2_ARG_CONV_EXCL — stops the property VALUE being mangled, but it also
#                   stops $PROJECT being converted, so pass that as Windows.
MSYS2_ARG_CONV_EXCL="-p:" dotnet build "$(cygpath -w "$SOLUTION")"     -c "$CONFIG" -v minimal "-p:MemoQPath=$MEMOQ_WIN"

# Two assemblies, because memoQ loads one module per DLL: the MT engine and the
# terminology provider cannot share one.
OUTPUT="$ROOT/src/Supervertaler.MemoQ/bin/$CONFIG/Supervertaler.MemoQ.dll"
OUTPUT_TB="$ROOT/src/Supervertaler.MemoQ.Terms/bin/$CONFIG/Supervertaler.MemoQ.Terms.dll"

for f in "$OUTPUT" "$OUTPUT_TB"; do
    [[ -f "$f" ]] || { echo "ERROR: build produced no DLL at $f" >&2; exit 1; }
done

# --- smoke test -------------------------------------------------------------
# Catches the silent failure mode: memoQ lists nothing and says nothing when a
# plugin type cannot be discovered or constructed.
echo
powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File "$(cygpath -w "$ROOT/tools/smoketest.ps1")" \
    -MemoQPath "$MEMOQ_WIN" \
    -PluginDll "$(cygpath -w "$OUTPUT")"

if [[ "$DEPLOY" -eq 0 ]]; then
    echo
    echo "Built (not deployed): $OUTPUT"
    exit 0
fi

ADDINS="$MEMOQ_DIR/Addins"

# --- deploy -----------------------------------------------------------------
# The Addins folder is under Program Files and has no per-user equivalent, so a
# plain copy fails unless this shell is already elevated. Rather than demand an
# elevated shell for every build, stage into a space-free path and re-launch
# tools/deploy.ps1 through UAC from there.
#
# The staging is not superstition: Start-Process -ArgumentList mangles a path
# containing spaces badly enough that the elevated process dies before it can
# report why (exit -196608, empty log). "D:\Google Drive\..." is such a path.

STAGE="/c/Temp/sv-deploy"
mkdir -p "$STAGE"
cp "$OUTPUT" "$OUTPUT_TB" "$STAGE/"
cp "$ROOT/tools/deploy.ps1" "$STAGE/"
rm -f "$STAGE/deploy.log"

echo
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "
\$p = Start-Process powershell.exe -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File C:\Temp\sv-deploy\deploy.ps1 -PluginDll C:\Temp\sv-deploy\Supervertaler.MemoQ.dll -LogFile C:\Temp\sv-deploy\deploy.log' -Verb RunAs -Wait -PassThru -WindowStyle Hidden
exit \$p.ExitCode" >/dev/null 2>&1 || true

DEPLOY_LOG=""
[[ -f "$STAGE/deploy.log" ]] && DEPLOY_LOG="$(cat "$STAGE/deploy.log")"
[[ -n "$DEPLOY_LOG" ]] && echo "$DEPLOY_LOG"

# Substring, not anchored: deploy.ps1 writes plain UTF-8, but a leading byte from
# any future logging change must not turn a successful deploy into a failure.
if [[ "$DEPLOY_LOG" == *"OK  "* ]]; then
    echo
    echo "Next: start memoQ, then Resources > Settings > MT > Supervertaler."
    echo "memoQ warns once that the plugin is unsigned — expected until it is signed"
    echo "(DLL + PublicKey.xml + .KGSIGN submitted to memoQ)."
    echo
    echo "Log: %LocalAppData%\Supervertaler.memoQ\plugin.log"
else
    echo "Deploy failed. Copy by hand from an elevated prompt:" >&2
    echo "  copy \"$(cygpath -w "$OUTPUT")\" \"$(cygpath -w "$ADDINS")\"" >&2
    exit 1
fi
