# Supervertaler for memoQ

AI translation for memoQ — an LLM machine-translation engine that learns from the
segments you confirm, plus a terminology provider that puts your own glossary into
memoQ's Translation results *and* into the AI's prompt.

Companion to [Supervertaler for Trados](https://github.com/Supervertaler/Supervertaler-for-Trados).

> **Status: early development.** It loads, translates, and learns. It is not yet a
> release.

## What it does

**AI translation engine.** Anthropic, OpenAI or Google, with your own API key and
your own instructions. Segment-by-segment while you work, or batch via
Pre-translate. Inline tags survive the round trip via memoQ's own segment
serialiser.

**Learns from your confirmations.** Every segment you confirm is captured and the
most relevant ones are shown to the model when it translates later segments in the
same document. Confirm *electric module* once and the rest of the document follows
— no configuration, no retraining, just your own approved choices fed forward.
Persisted to disk, so it survives closing memoQ.

**Terminology.** A tab-separated glossary appears as a memoQ terminology provider
— matched terms highlighted in the source, entries rendered in the Translation
results pane — and the same terms are sent to the model as required or forbidden
terminology. Forbidden terms are enforced rather than merely displayed.

## Installing

memoQ has no plugin marketplace. Copy both DLLs into memoQ's `Addins` folder
(inside the memoQ program directory — this needs administrator rights):

```
Supervertaler.MemoQ.dll
Supervertaler.MemoQ.Terms.dll
```

memoQ warns once that the plugin is unsigned; the default button is **No**.

Then:

- **MT engine** — Resource console → MT settings → edit → Services → enable
  *Supervertaler* → **Configure plugin** for provider, model and API key.
  To have it learn from confirmations, also set it under
  Settings → **Self-learning MT**.
- **Terminology** — Options → Terminology plugins → tick *Perform terminology
  plugin lookups while working in the translation grid* → **Supervertaler terms**
  → Options → choose a glossary → **Enable plugin**.

## Glossary format

Tab-separated, one term per line. See [`examples/glossary-example.txt`](examples/glossary-example.txt).

```
elektrische module	electric module
elektrische module	electrical module	forbidden
```

A third column containing `forbidden` marks a target that must not be used. Lines
starting with `#` are ignored. The file is re-read whenever you save it, so you can
edit it with memoQ open.

`tools/convert_termbase.py` converts a Supervertaler Workbench termbase export into
this format.

## Building

Requires the .NET SDK and an installed memoQ (the build references memoQ's own
assemblies; nothing is redistributed).

```bash
bash build.sh              # build, verify, deploy to the Addins folder
bash build.sh --no-deploy  # build and verify only
```

`build.sh` refuses to run while memoQ is open — it locks the DLLs — and runs
`tools/smoketest.ps1`, which loads the build through memoQ's *own* add-in loader
before deploying. That check exists because memoQ's failure mode is silent: a
plugin it cannot load simply never appears, with no error anywhere.

## Notes

Two assemblies, because memoQ loads exactly one module per DLL. They share a
process, so the glossary the terminology pane shows is the same one that reaches
the prompt.

[`CLAUDE.md`](CLAUDE.md) documents the memoQ SDK behaviour discovered while
building this — including several things that are not in the SDK documentation and
fail silently.

## Licence

Copyright © 2026 Michael Beijer. All rights reserved.

The source is published so users and reviewers can see what the plugin does. It is
not open source: see [LICENSE](LICENSE).

---

[supervertaler.com](https://supervertaler.com) · [docs.supervertaler.com](https://docs.supervertaler.com)
