#!/usr/bin/env python3
"""Build the Supervertaler for memoQ .mcpb bundle (Claude Desktop extension).

Same server exe as the Trados extension — Supervertaler.McpServer, which
lives in the Supervertaler-for-Trados repo — packed with a manifest that
sets SUPERVERTALER_HOST=memoq. That one variable makes the exe look for the
memoQ plugin's handshake (<Supervertaler data folder>\\memoq\\runtime\\bridge.json),
use a separate tool cache, and drop the Trados-only instance tools. Nothing
else differs, so there is one server to maintain and two thin bundles.

The server project is found in a sibling checkout by default
(../Supervertaler-for-Trados); pass --server-project to point elsewhere.

Usage:
    python tools/build_mcpb.py [--version 0.1.0] [--skip-publish]
"""
import argparse
import json
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEFAULT_SERVER_PROJECT = REPO.parent / "Supervertaler-for-Trados" / "src" / "Supervertaler.McpServer"
DIST = REPO / "dist"
ICON = REPO / "sv-icon-512.png"
BUNDLE = "Supervertaler-for-memoQ-MCP-Server.mcpb"


def manifest(version: str) -> dict:
    return {
        "manifest_version": "0.3",
        "name": "supervertaler-memoq",
        "display_name": "Supervertaler for memoQ – MCP Server",
        "version": version,
        "description": "Connect your AI assistant to the project open in memoQ via Supervertaler for memoQ.",
        "long_description": (
            "Gives AI assistants live access to the document open in memoQ through the Supervertaler "
            "for memoQ plugin: the document with its target text, the segment the cursor is on, "
            "confirmed translations, glossary lookups, QA checks, and translations staged for the next "
            "Pre-translate. Requires memoQ with Supervertaler for memoQ installed "
            "(docs.supervertaler.com/memoq). Everything stays on your machine: the connection is "
            "loopback-only and token-authenticated."
        ),
        "author": {"name": "Supervertaler", "url": "https://supervertaler.com"},
        "homepage": "https://supervertaler.com",
        "documentation": "https://docs.supervertaler.com/memoq/mcp-server/",
        "icon": "icon.png",
        "server": {
            "type": "binary",
            "entry_point": "server/SupervertalerMcpServer.exe",
            "mcp_config": {
                "command": "${__dirname}/server/SupervertalerMcpServer.exe",
                "args": [],
                "env": {"SUPERVERTALER_HOST": "memoq"},
            },
        },
        "compatibility": {"platforms": ["win32"]},
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="0.1.0")
    ap.add_argument("--server-project", default=str(DEFAULT_SERVER_PROJECT))
    ap.add_argument("--skip-publish", action="store_true", help="reuse the existing publish output")
    args = ap.parse_args()

    project = Path(args.server_project)
    if not (project / "Supervertaler.McpServer.csproj").exists():
        print(f"ERROR: server project not found at {project}", file=sys.stderr)
        return 1
    publish_dir = project / "bin" / "Release" / "net8.0" / "win-x64" / "publish"

    if not args.skip_publish:
        print("== dotnet publish (Release, win-x64, self-contained single file) ==")
        subprocess.run(
            ["dotnet", "publish", "-c", "Release", "-r", "win-x64", "--self-contained", "true",
             "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
             f"-p:Version={args.version}"],
            cwd=project, check=True)

    exe = publish_dir / "SupervertalerMcpServer.exe"
    if not exe.exists():
        print(f"ERROR: publish output not found at {exe}", file=sys.stderr)
        return 1

    DIST.mkdir(exist_ok=True)
    out = DIST / BUNDLE
    if out.exists():
        out.unlink()

    print(f"== packing {out.name} ==")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest(args.version), indent=2))
        z.write(exe, "server/SupervertalerMcpServer.exe")
        if ICON.exists():
            z.write(ICON, "icon.png")
        else:
            print(f"  (icon not found at {ICON} - skipped)")

    print(f"Done: {out}  ({out.stat().st_size / (1024 * 1024):.1f} MB)")
    print("Install: Claude Desktop > Settings > Extensions > Advanced settings > Install extension…")
    return 0


if __name__ == "__main__":
    sys.exit(main())
