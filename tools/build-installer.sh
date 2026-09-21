#!/usr/bin/env bash
# Builds the distributable: an installer, plus the loose signed files beside it,
# zipped the way Lara ships theirs - so an IT department deploying centrally, or
# anyone whose installer run fails, can place the add-in by hand.
#
# Does NOT build the product. Run build.sh first; this packages what is there,
# and refuses if it is not.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ISCC="/c/Users/$USERNAME/AppData/Local/Programs/Inno Setup 6/ISCC.exe"

[[ -x "$ISCC" ]] || { echo "ERROR: Inno Setup not found at $ISCC" >&2
                      echo "       winget install --id JRSoftware.InnoSetup" >&2; exit 1; }

PLUGIN="$ROOT/src/Supervertaler.MemoQ/bin/Release/Supervertaler.MemoQ.dll"
TERMS="$ROOT/src/Supervertaler.MemoQ.Terms/bin/Release/Supervertaler.MemoQ.Terms.dll"
EDITOR="$ROOT/src/Supervertaler.PromptEditor/bin/Release/Supervertaler.PromptEditor.exe"
PREVIEW="$ROOT/src/Supervertaler.MemoQ.Preview/bin/Release/Supervertaler.MemoQ.Preview.exe"

for f in "$PLUGIN" "$TERMS" "$EDITOR" "$PREVIEW"; do
    [[ -f "$f" ]] || { echo "ERROR: not built: $f" >&2; echo "       Run: bash build.sh --no-deploy" >&2; exit 1; }
done

# The version comes from the assembly rather than from a number typed here, so
# the installer and the plugin can never disagree about what this is.
VERSION="$(powershell.exe -NoProfile -Command \
    "[Diagnostics.FileVersionInfo]::GetVersionInfo('$(cygpath -w "$PLUGIN")').FileVersion" \
    | tr -d '\r')"
[[ -n "$VERSION" ]] || { echo "ERROR: could not read a version from the plugin" >&2; exit 1; }

echo "version: $VERSION"

# --- the wizard images ------------------------------------------------------
# Generated from the product's own mark rather than committed, so there is one
# source of truth for it and no bitmap to go stale when the icon changes.
powershell.exe -NoProfile -File "$(cygpath -w "$ROOT/tools/make-wizard-images.ps1")"

# --- the installer ----------------------------------------------------------
mkdir -p "$ROOT/dist"
MSYS2_ARG_CONV_EXCL="/D" "$ISCC" \
    "/DAppVersion=$VERSION" \
    "$(cygpath -w "$ROOT/installer/Supervertaler-for-memoQ.iss")" \
    | grep -E "^Successful|error|Error" || true

SETUP="$ROOT/dist/Supervertaler-for-memoQ-$VERSION.exe"
[[ -f "$SETUP" ]] || { echo "ERROR: the installer was not produced" >&2; exit 1; }

# --- the zip, installer plus the loose files --------------------------------
# Named files, never a glob. Google Drive leaves sync conflict copies in bin and
# a wildcard ships them; that has happened here before.
STAGE="$ROOT/dist/stage"
rm -rf "$STAGE"
mkdir -p "$STAGE"

cp "$SETUP" "$STAGE/"
cp "$PLUGIN" "$TERMS" "$EDITOR" "$STAGE/"

ZIP="$ROOT/dist/Supervertaler-for-memoQ-$VERSION.zip"
rm -f "$ZIP"
powershell.exe -NoProfile -Command \
    "Compress-Archive -Path '$(cygpath -w "$STAGE")\*' -DestinationPath '$(cygpath -w "$ZIP")' -Force"

rm -rf "$STAGE"

echo
echo "OK  $(cygpath -w "$SETUP")"
echo "OK  $(cygpath -w "$ZIP")"
echo
echo "The zip carries the installer and the loose add-in files, as Lara's does,"
echo "so a central deployment or a failed install can place them by hand."
echo
echo "NOT SIGNED YET. Until memoQ compiles the public key into a maintenance"
echo "release there is no .kgsign to ship beside the DLLs, and memoQ may warn"
echo "once on first load."
