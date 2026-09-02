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

# The prompt editor. Not an add-in — memoQ never loads it — but it ships beside
# them because the options dialog launches it by looking next to its own
# assembly, and that is the only UI surface a memoQ add-in has to offer it from.
OUTPUT_ED="$ROOT/src/Supervertaler.PromptEditor/bin/$CONFIG/Supervertaler.PromptEditor.exe"

for f in "$OUTPUT" "$OUTPUT_TB" "$OUTPUT_ED"; do
    [[ -f "$f" ]] || { echo "ERROR: build produced no output at $f" >&2; exit 1; }
done

# Assert the build actually rebuilt. MSBuild happily prints "Build succeeded"
# without producing anything when it thinks a project is up to date, or when the
# output landed somewhere else — which is exactly what happened when the solution
# started emitting to bin/x64/Release while this script read bin/Release. The
# result was over an hour of deploying a stale DLL and testing the wrong build.
# -print -quit rather than a pipeline: piping find into head closes the pipe
# early, and under `set -o pipefail` that SIGPIPE takes the whole script down
# silently — which it duly did the first time this guard was written.
# obj/ is excluded: MSBuild writes AssemblyAttributes.cs there during every
# build, so it is always newer than the output and would trip this on principle.
# The two standalone exes are pruned too: their sources never enter the DLL,
# so an edit to the preview tool must not read as "the plugin is stale".
STALE="$(find "$ROOT/src" \( -path '*/obj' -o -path '*/Supervertaler.PromptEditor' -o -path '*/Supervertaler.MemoQ.Preview' \) -prune -o     \( -name '*.cs' -o -name '*.csproj' \) -newer "$OUTPUT" -print -quit)"
if [[ -n "$STALE" ]]; then
    echo "ERROR: $(basename "$OUTPUT") is older than $STALE" >&2
    echo "       The build produced no fresh output — refusing to deploy a stale DLL." >&2
    exit 1
fi

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
cp "$OUTPUT" "$OUTPUT_TB" "$OUTPUT_ED" "$STAGE/"
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
    # The preview tool. A separate process memoQ launches itself, so it does
    # NOT go into Addins: it carries its own Newtonsoft.Json and System.Web.Http,
    # and memoQ probes Addins for its own assemblies — a copy of either there
    # could shadow memoQ's. It lives under the user's profile instead, needs no
    # elevation, and memoQ finds it by the AutoStartupCommand it registered.
    PREVIEW_SRC="$ROOT/src/Supervertaler.MemoQ.Preview/bin/$CONFIG"
    # Into the shared Supervertaler data folder (D:\Supervertaler here), NOT
    # %LocalAppData%. Claude Code runs as a packaged (MSIX) app, and packaged
    # apps get file-system virtualisation: anything written under AppData from
    # this shell lands in AppData\Local\Packages\Claude_…\LocalCache instead,
    # visible to this shell and to nothing else. An hour was spent on a folder
    # that was "empty" in Explorer and full in bash. The data folder is on a
    # real path, and memoQ, the tool and the user all see the same files.
    SV_CFG="$APPDATA/Supervertaler/config.json"
    SV_ROOT="$(python -c "import json,sys;print(json.load(open(sys.argv[1]))['user_data_path'])" "$SV_CFG" 2>/dev/null || true)"
    [[ -n "$SV_ROOT" ]] || SV_ROOT="$USERPROFILE/Supervertaler"
    PREVIEW_DST="$(cygpath -u "$SV_ROOT")/memoq/preview"
    if [[ -f "$PREVIEW_SRC/Supervertaler.MemoQ.Preview.exe" ]]; then
        if tasklist.exe //FI "IMAGENAME eq Supervertaler.MemoQ.Preview.exe" 2>/dev/null | grep -qi "Supervertaler.MemoQ.Preview"; then
            echo "WARN  preview tool is running; not replaced (quit it from the tray and rerun)"
        else
            mkdir -p "$PREVIEW_DST"
            cp "$PREVIEW_SRC"/*.dll "$PREVIEW_SRC"/*.exe "$PREVIEW_SRC"/*.config "$PREVIEW_DST/" 2>/dev/null || true
            echo "OK  $(cygpath -w "$PREVIEW_DST")\Supervertaler.MemoQ.Preview.exe"
        fi
    fi
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
