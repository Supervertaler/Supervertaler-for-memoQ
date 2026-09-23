#!/usr/bin/env python3
"""Prepare a release of Supervertaler for memoQ.

    python tools/release.py            # check everything, build, write the notes

Refuses rather than warns. Each check below exists because the thing it guards
against has happened in one of the Supervertaler repositories:

- every project carries the same version, so an installer never ships a mix;
- the tree is clean and pushed, because a GitHub release is cut from the
  remote's branch and not from local HEAD (a Trados release once described
  changes its tag did not contain);
- the tag is new;
- the changelog has something to say;
- the build and the installer are fresh, built here by the scripts that
  already refuse on a stale extension bundle or a failed compile;
- the artefacts are plausible sizes, because a build step that fails can
  still leave an old or partial file behind looking like the answer;
- the notes name no client, because a real job reference once reached a
  public page through exactly this kind of text.

PUBLISHING IS NOT HERE YET, deliberately. Where the installer is hosted is
still Michael's decision, and it changes what publishing means - so this
stops at a verified installer and the notes, and says so.
"""
import io
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CHANGELOG = REPO / "CHANGELOG.md"
DIST = REPO / "dist"

# A job reference in the shape real clients use (the Trados repo's rule): three
# to five capitals, three digits, then two-letter groups. Placeholders such as
# PROJ-001 pass, because they carry no letter groups.
CLIENT_REFERENCE = re.compile(r"\b[A-Z]{3,5}-\d{3}-[A-Z]{2}(?:-[A-Z]{2})?\b")


def fail(message):
    print("\nREFUSED: " + message, file=sys.stderr)
    sys.exit(1)


def run(args, **kw):
    return subprocess.run(args, cwd=REPO, capture_output=True, text=True, **kw)


def versions():
    found = {}
    for proj in sorted((REPO / "src").glob("*/*.csproj")):
        m = re.search(r"<Version>([^<]+)</Version>", proj.read_text(encoding="utf-8-sig"))
        if m:
            found[proj.parent.name] = m.group(1).strip()
    return found


def unreleased_section(text):
    m = re.search(r"^## \[Unreleased\][^\n]*\n(.*?)(?=^## |\Z)", text, re.S | re.M)
    return m.group(1).strip() if m else ""


def highlights(section):
    """The bold headline of every starred bullet - the Trados convention, so a
    reader coming from nothing sees the flagship features before the detail."""
    return [m.group(1) for m in re.finditer(r"^- ★ \*\*(.+?)\*\*", section, re.M)]


def main():
    print("== versions")
    v = versions()
    if not v:
        fail("no <Version> found in any project")
    distinct = sorted(set(v.values()))
    if len(distinct) != 1:
        fail("the projects disagree about the version: " + ", ".join(f"{k} {x}" for k, x in v.items()))
    version = distinct[0]
    tag = "v" + version
    print(f"   {version} in all {len(v)} projects")

    print("== repository")
    if run(["git", "status", "--porcelain", "--untracked-files=no"]).stdout.strip():
        fail("there are uncommitted changes to tracked files")
    head = run(["git", "rev-parse", "HEAD"]).stdout.strip()
    upstream = run(["git", "rev-parse", "@{u}"]).stdout.strip()
    if head != upstream:
        fail("HEAD is not what is pushed. Push first: a release is cut from the remote, not from here")
    if run(["git", "tag", "-l", tag]).stdout.strip() or run(["git", "ls-remote", "--tags", "origin", tag]).stdout.strip():
        fail(f"{tag} already exists")
    print(f"   clean, pushed at {head[:7]}, {tag} is new")

    print("== changelog")
    section = unreleased_section(CHANGELOG.read_text(encoding="utf-8-sig"))
    if not section:
        fail("the [Unreleased] section of CHANGELOG.md is empty or missing")
    stars = highlights(section)
    print(f"   {len(section.splitlines())} lines, {len(stars)} highlights")

    print("== build (no deploy)")
    b = run(["bash", "build.sh", "--no-deploy"])
    if b.returncode != 0:
        fail("build.sh failed:\n" + (b.stdout + b.stderr)[-1500:])

    # The Claude Desktop extension is rebuilt for every release rather than
    # reused: it packs the MCP server from the Trados checkout, and a release
    # should carry the server as it stands, not whatever was packed last week.
    # From committed code only - a release that quietly includes somebody's
    # half-finished change in the other repository cannot be reproduced.
    print("== Claude Desktop extension")
    server = REPO.parent / "Supervertaler-for-Trados" / "src" / "Supervertaler.McpServer"
    if not server.exists():
        fail(f"the MCP server project is not where the bundle expects it: {server}")
    dirty = subprocess.run(["git", "status", "--porcelain", "--", "."], cwd=server,
                           capture_output=True, text=True).stdout.strip()
    if dirty:
        fail("the MCP server in the Trados checkout has uncommitted changes; a release must not "
             "pack them:\n" + dirty)
    m = run([sys.executable, "tools/build_mcpb.py", "--version", version])
    if m.returncode != 0:
        fail("build_mcpb.py failed:\n" + (m.stdout + m.stderr)[-1500:])

    print("== installer")
    i = run(["bash", "tools/build-installer.sh"])
    if i.returncode != 0:
        fail("build-installer.sh failed:\n" + (i.stdout + i.stderr)[-1500:])

    four = version + ".0" if version.count(".") == 2 else version
    setup = DIST / f"Supervertaler-for-memoQ-{four}.exe"
    zipped = DIST / f"Supervertaler-for-memoQ-{four}.zip"
    for f in (setup, zipped):
        if not f.exists():
            fail(f"missing: {f}")
    # The installer carries the Claude Desktop extension, about 30 MB of it; one
    # much smaller than that is not the installer we meant to build.
    mb = setup.stat().st_size / 1048576
    if mb < 20:
        fail(f"{setup.name} is {mb:.1f} MB - too small to contain the Claude Desktop extension")
    print(f"   {setup.name}  {mb:.1f} MB")
    print(f"   {zipped.name}  {zipped.stat().st_size / 1048576:.1f} MB")

    print("== notes")
    body = io.StringIO()
    body.write(f"# Supervertaler for memoQ {version}\n\n")
    body.write("AI translation inside memoQ: an MT engine, a terminology provider, and a companion "
               "window for everything memoQ gives a plugin no room for. Requires memoQ 12. One "
               "Supervertaler licence covers Supervertaler for Trados and Supervertaler for memoQ.\n\n")
    if stars:
        body.write("## Highlights\n\n")
        for s in stars:
            body.write(f"- **{s}**\n")
        body.write("\n")
    body.write("## Installing\n\n"
               "Close memoQ, run the installer, and follow its last page, which says how to switch "
               "Supervertaler on inside memoQ. The free trial starts by itself.\n\n")
    body.write("## Everything in this release\n\n" + section.replace("★ ", "") + "\n")
    notes = body.getvalue()

    leaks = [m.group(0) for m in CLIENT_REFERENCE.finditer(notes)]
    if leaks:
        fail("the notes contain what looks like a real job reference: " + ", ".join(sorted(set(leaks))))

    out = DIST / f"release-notes-{tag}.md"
    out.write_text(notes, encoding="utf-8")
    print(f"   {out.name}")

    print(f"\nReady: {tag}. Nothing has been published.")
    print("Publishing waits on where the installer is hosted, which is not decided yet.")


if __name__ == "__main__":
    main()
