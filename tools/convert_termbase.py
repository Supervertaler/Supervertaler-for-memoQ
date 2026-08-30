#!/usr/bin/env python3
"""
Convert a Supervertaler Workbench termbase export into the glossary format the
memoQ plugin reads.

    python tools/convert_termbase.py <input.tsv> <output.txt>

Input (Workbench export):
    Term UUID <TAB> Source <TAB> Target <TAB> Domain <TAB> Notes <TAB> Project
              <TAB> Client <TAB> Forbidden

Output (plugin glossary):
    source <TAB> target [<TAB> forbidden]

Three things about the input make a naive line-by-line split wrong, and all three
occur in a real export:

  * Fields are CSV-quoted inside a TSV file, so a term containing a quote comes
    through as \"\"\"approximately\"\" or \"\"around\"\"\".
  * A quoted field may contain a newline, so "one line = one entry" is false —
    9,618 physical lines in the sample resolve to far fewer records.
  * A pipe separates variants, on either side: 'kortdurend zorgverlof|(kortdurend)
    zorgverlof' is two ways of writing one target.

Hence csv.reader rather than str.split.
"""

import csv
import sys
from pathlib import Path


def variants(field):
    """Split a pipe-separated field, keeping order and dropping blanks."""
    return [v.strip() for v in field.split("|") if v.strip()]


def convert(src_path, out_path):
    rows_in = 0
    written = 0
    skipped_identical = 0
    skipped_short = 0
    skipped_empty = 0
    forbidden_count = 0
    seen = set()
    out_lines = []

    # utf-8-sig: the export carries a BOM.
    with open(src_path, "r", encoding="utf-8-sig", newline="") as fh:
        reader = csv.reader(fh, delimiter="\t", quotechar='"')

        header = next(reader, None)
        if not header or "Source" not in header:
            sys.exit(f"Unexpected header: {header!r}")

        col = {name: i for i, name in enumerate(header)}
        i_src, i_trg = col["Source"], col["Target"]
        i_forb = col.get("Forbidden")

        for row in reader:
            rows_in += 1
            if len(row) <= max(i_src, i_trg):
                skipped_empty += 1
                continue

            sources = variants(row[i_src])
            targets = variants(row[i_trg])

            if not sources or not targets:
                skipped_empty += 1
                continue

            forbidden = (
                i_forb is not None
                and len(row) > i_forb
                and row[i_forb].strip().upper() == "TRUE"
            )

            # The first target is the preferred rendering; the rest are noted as
            # accepted variants but not offered as the answer, so the model is not
            # given a menu to choose from.
            target = targets[0]

            for source in sources:
                # A term that translates to itself teaches the model nothing and
                # costs prompt space — unless it is marked forbidden, where the
                # point is precisely "do not leave this untranslated".
                if source.casefold() == target.casefold() and not forbidden:
                    skipped_identical += 1
                    continue

                # Tabs and newlines are the record separators; a term containing
                # one would corrupt the file.
                s = " ".join(source.split())
                t = " ".join(target.split())
                if not s or not t:
                    skipped_empty += 1
                    continue

                # Very short all-lowercase entries are noise once matching is
                # case-insensitive: a real termbase contains things like
                # "to -> ad", which then fires on every segment and takes prompt
                # space from terms that matter. Anything with a capital or a digit
                # survives, so IT, pH, H2, 3D and the like are kept.
                if len(s) < 3 and s.islower() and s.isalpha():
                    skipped_short += 1
                    continue

                key = (s.casefold(), t.casefold(), forbidden)
                if key in seen:
                    continue
                seen.add(key)

                out_lines.append(f"{s}\t{t}\tforbidden" if forbidden else f"{s}\t{t}")
                written += 1
                if forbidden:
                    forbidden_count += 1

    # Longest source first: the plugin re-sorts anyway, but a pre-sorted file is
    # easier to eyeball and makes the multi-word entries obvious.
    out_lines.sort(key=lambda line: (-len(line.split("\t")[0]), line.casefold()))

    header_text = (
        "# Supervertaler glossary for memoQ\n"
        f"# Converted from {Path(src_path).name}\n"
        "#\n"
        "# Format:  source <TAB> target <TAB> [forbidden]\n"
        "# Lines starting with # are ignored. The plugin re-reads this file\n"
        "# automatically whenever you save it.\n"
        "\n"
    )

    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(header_text)
        fh.write("\n".join(out_lines))
        fh.write("\n")

    print(f"records read      {rows_in}")
    print(f"terms written     {written}   ({forbidden_count} forbidden)")
    print(f"skipped identical {skipped_identical}")
    print(f"skipped too short {skipped_short}")
    print(f"skipped empty     {skipped_empty}")
    print(f"output            {out_path}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    convert(sys.argv[1], sys.argv[2])
