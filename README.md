# Supervertaler for memoQ

AI translation for memoQ — an LLM machine-translation engine that learns from the
segments you confirm, a terminology provider that puts your own glossary into
memoQ's Translation results *and* into the AI's prompt, and an MCP bridge that lets
Claude Desktop translate your live project.

Companion to [Supervertaler for Trados](https://github.com/Supervertaler/Supervertaler-for-Trados),
sharing its code through [Supervertaler-Plugin-Core](https://github.com/Supervertaler/Supervertaler-Plugin-Core).

> **Status: working, pre-release.** Translates, batches, learns, serves terminology,
> and connects to Claude Desktop. Distributed as unsigned DLLs for now; a signed
> build and an installer are the remaining steps to a release.

Documentation: [docs.supervertaler.com/memoq](https://docs.supervertaler.com/memoq/)

## What it does

**AI translation engine.** Anthropic, OpenAI or Google, with your own API key and
your own instructions. Segment-by-segment while you work, or batched through
Pre-translate. Inline tags survive the round trip via memoQ's own segment
serialiser.

**Learns from your confirmations.** Every segment you confirm is captured and the
most relevant ones are shown to the model when it translates later segments in the
same document. Confirm *electric module* once and the rest of the document follows
— no configuration, no retraining, just your own approved choices fed forward.
Persisted to disk, so it survives closing memoQ.

**Terminology.** A tab-separated glossary appears as a memoQ terminology provider
— matched terms highlighted in the source, entries rendered in the Translation
results pane — and the same terms are sent to the model as preferred or forbidden
terminology. Forbidden terms are enforced rather than merely displayed.

**Translate with Claude Desktop.** The plugin hosts a bridge for the
[Supervertaler MCP Server](https://docs.supervertaler.com/trados/mcp-server/), so
Claude (or any local-MCP client) can read the document you are translating, your
confirmed segments and your glossary, and *stage* translations that flow into the
grid when you press Pre-translate. Tokens go on your Claude subscription, not an
API key; every write into your document goes through your own hands. See
[MCP Server](https://docs.supervertaler.com/memoq/mcp-server/) — including the
honest table of which Trados tools do and do not exist for memoQ.

**A live document link.** memoQ's Preview SDK — the interface its own PDF
preview uses — is the one channel that shows a tool the target text, the row
the cursor is on and the document's real name. `Supervertaler.MemoQ.Preview.exe`
registers as a preview tool and forwards that stream to the plugin, so Claude
sees the document as it is, can tell which row you are on, and can ask memoQ
to jump to a segment. It draws nothing; it is a link, not a preview.

**A prompt library, shared with Trados.** Instructions come from the same library
the Trados plugin uses, chosen from a dropdown and edited in a small companion
editor (`Supervertaler.PromptEditor.exe`) launched from the settings dialog. Claude
can draft prompts into it too.

## How it fits memoQ

memoQ gives an add-in no window of its own and no API into the project, the editor
or its TMs and term bases. A plugin is only ever *called*: asked for a translation,
asked for terminology hits, handed a segment the user confirmed. Everything above
is built on those three calls — which is why the AI's knowledge of your document
is what has passed through the plugin's hands, why translations from Claude are
staged rather than written, and why the glossary is a file rather than a memoQ
term base. [`CLAUDE.md`](CLAUDE.md) records what the SDK does and does not allow,
including a number of things that fail silently.

## Installing

memoQ has no plugin marketplace. Copy the DLLs (and the editor) into memoQ's
`Addins` folder — inside the memoQ program directory, so this needs administrator
rights:

```
Supervertaler.MemoQ.dll
Supervertaler.MemoQ.Terms.dll
Supervertaler.PromptEditor.exe
```

memoQ may warn once that the plugin is unsigned.

Then:

- **MT engine** — Resource console → MT settings → edit → Services → enable
  *Supervertaler* → **Configure plugin** for provider, model, API key and prompt.
  To have it learn from confirmations, also set it under
  Settings → **Self-learning MT**.
- **Terminology** — Options → Terminology plugins → tick *Perform terminology
  plugin lookups while working in the translation grid* → **Supervertaler terms**
  → Options → choose a glossary → **Enable plugin**.
- **Claude Desktop** — add one server entry pointing the Supervertaler MCP Server
  at `<Supervertaler data folder>\memoq\runtime\bridge.json` via the
  `SUPERVERTALER_BRIDGE_FILE` environment variable. Steps in the
  [docs](https://docs.supervertaler.com/memoq/mcp-server/#setting-it-up).
- **Live document link** — run `Supervertaler.MemoQ.Preview.exe` once (the deploy
  puts it under `%LocalAppData%\Supervertaler.memoQ\preview\`) and accept memoQ's
  *Preview tool connection request*, leaving *Auto-start with memoQ* ticked. memoQ
  starts it itself from then on.

## Glossary format

Tab-separated, one term per line. See [`examples/glossary-example.txt`](examples/glossary-example.txt).

```
elektrische module	electric module
elektrische module	electrical module	forbidden
```

A third column containing `forbidden` marks a target that must not be used. Lines
starting with `#` are ignored. The file is re-read whenever you save it, so you can
edit it with memoQ open.

## Building

Requires the .NET SDK, an installed memoQ (the build references memoQ's own
assemblies; nothing is redistributed), and the `core/` submodule
(`git submodule update --init`).

```bash
bash build.sh              # build, verify, deploy to the Addins folder
bash build.sh --no-deploy  # build and verify only
```

`build.sh` refuses to run while memoQ is open — it locks the DLLs — and runs
`tools/smoketest.ps1`, which loads the build through memoQ's *own* add-in loader
before deploying. That check exists because memoQ's failure mode is silent: a
plugin it cannot load simply never appears, with no error anywhere.

## Layout

| | |
|---|---|
| `src/Supervertaler.MemoQ` | MT engine, options dialog, MCP bridge, capture and staging stores |
| `src/Supervertaler.MemoQ.Terms` | Terminology provider (its own DLL: memoQ loads one module per assembly) |
| `src/Supervertaler.PromptEditor` | Standalone prompt library editor |
| `src/Supervertaler.MemoQ.Preview` | Preview-SDK tool: the live document link (deploys under the user profile, never into `Addins`) |
| `core/` | Shared Supervertaler code (submodule) |
| `tools/` | Smoke test, deploy script, glossary converter |

## Licence

Copyright © 2026 Michael Beijer. All rights reserved.

The source is published so users and reviewers can see what the plugin does. It is
not open source: see [LICENSE](LICENSE).

---

[supervertaler.com](https://supervertaler.com) · [docs.supervertaler.com](https://docs.supervertaler.com)
