# Supervertaler for memoQ

AI translation for memoQ – an LLM machine-translation engine that learns from the
segments you confirm, drafts its own project prompt from the document, carries a
per-client memory bank with every request, puts your glossary into memoQ's
Translation results *and* into the AI's prompt, and hosts an MCP bridge that lets
Claude Desktop translate your live project.

Companion to [Supervertaler for Trados](https://github.com/Supervertaler/Supervertaler-for-Trados),
sharing its code through [Supervertaler-Plugin-Core](https://github.com/Supervertaler/Supervertaler-Plugin-Core).

> **Status: working, pre-release.** Translates, batches, learns, drafts prompts,
> serves terminology and memory banks, and connects to Claude Desktop. Distributed
> as unsigned DLLs for now; a signed build and an installer are the remaining steps
> to a release. What exists is listed in [`CHANGELOG.md`](CHANGELOG.md).

Documentation: [docs.supervertaler.com/memoq](https://docs.supervertaler.com/memoq/)

## What it does

**AI translation engine.** Anthropic, OpenAI or Google, with your own API key and
your own instructions. Segment-by-segment while you work, or batched through
Pre-translate. Inline tags survive the round trip via memoQ's own segment
serialiser. The model list is a short one – the few worth recommending, with a
verdict each – and the provider's full catalogue is one tick away. The stable part
of every batch request is cached at the provider, and the Activity window shows
the token counts that prove it.

**Learns from your confirmations.** Every segment you confirm is captured and the
most relevant ones are shown to the model when it translates later segments in the
same document. Confirm *electric module* once and the rest of the document follows
– no configuration, no retraining, just your own approved choices fed forward.
Persisted to disk, so it survives closing memoQ.

**AutoPrompt.** Reads the open document, classifies it, and drafts a
project-specific translation prompt with a locked-terms table – confirming the
domain with you first and showing exactly what it is about to send. A drafted
prompt is the one authority on terminology while it is selected; forbidden
glossary terms still travel.

**SuperMemory.** A memory bank of Markdown articles – brief, terminology, style
– per client or per project, sent with every request and in full to AutoPrompt.
Remembered per memoQ project; the `_shared` bank travels whatever else is chosen.

**Terminology.** A tab-separated glossary appears as a memoQ terminology provider
– matched terms highlighted in the source, entries rendered in the Translation
results pane – and the same terms are sent to the model as preferred or forbidden
terminology. Forbidden terms are enforced rather than merely displayed. A drafted
prompt's locked terms can be exported as a glossary in one click.

**Translate with Claude Desktop.** The plugin hosts a bridge for the
[Supervertaler MCP Server](https://docs.supervertaler.com/trados/mcp-server/), so
Claude (or any local-MCP client) can read the document you are translating, your
confirmed segments and your glossary, and *stage* translations that flow into the
grid when you press Pre-translate. Tokens go on your Claude subscription, not an
API key; every write into your document goes through your own hands. See
[MCP Server](https://docs.supervertaler.com/memoq/mcp-server/) – including the
honest table of which Trados tools do and do not exist for memoQ.

**A live document link.** memoQ's Preview SDK – the interface its own PDF
preview uses – is the one channel that shows a tool the target text, the row
the cursor is on and the document's real name. `Supervertaler.MemoQ.Preview.exe`
registers as a preview tool and forwards that stream to the plugin, so Claude
sees the document as it is, can tell which row you are on, and can ask memoQ
to jump to a segment. It draws nothing; it is a link, not a preview.

**A companion window.** memoQ gives an add-in no window of its own, so
everything richer than a settings dialog lives in `Supervertaler.PromptEditor.exe`
– launched from memoQ's dialog or pinned to the taskbar. A panel at the top says
what memoQ will use for the next segment (project, model, prompt, glossary, memory
bank), each changeable there; beneath it, the prompt library, every memory bank
with its articles, and every glossary, any of which can be made active with a
right-click and opened for editing. The library is the same one the Trados plugin
uses, and Claude can draft prompts into it too. **Activity** (Ctrl+L) shows what
the plugin is doing while memoQ's own dialog says only *Processing*.

## How it fits memoQ

memoQ gives an add-in no window of its own and no API into the project, the editor
or its TMs and term bases. A plugin is only ever *called*: asked for a translation,
asked for terminology hits, handed a segment the user confirmed. Everything above
is built on those three calls – which is why the AI's knowledge of your document
is what has passed through the plugin's hands, why translations from Claude are
staged rather than written, and why the glossary is a file rather than a memoQ
term base. [`CLAUDE.md`](CLAUDE.md) records what the SDK does and does not allow,
including a number of things that fail silently.

## Installing

memoQ has no plugin marketplace. Copy the DLLs (and the editor) into memoQ's
`Addins` folder – inside the memoQ program directory, so this needs administrator
rights:

```
Supervertaler.MemoQ.dll
Supervertaler.MemoQ.Terms.dll
Supervertaler.PromptEditor.exe
```

memoQ may warn once that the plugin is unsigned.

Then:

- **MT engine** – Resource console → MT settings → edit → Services → enable
  *Supervertaler* → **Configure plugin** for provider, model, API key and prompt.
  To have it learn from confirmations, also set it under
  Settings → **Self-learning MT**. API keys live in
  `C:\Users\<you>\Supervertaler\settings\api-keys.json`, shared with
  Supervertaler for Trados and Sidekick, so a key set in any of them works here.
- **Terminology** – Options → Terminology plugins → tick *Perform terminology
  plugin lookups while working in the translation grid* → **Supervertaler terms**
  → Options → choose a glossary → **Enable plugin**.
- **Claude Desktop** – install `Supervertaler-for-memoQ-MCP-Server.mcpb`
  (Settings → Extensions → Advanced settings → Install extension…). It is the
  same MCP server exe as the Trados extension with `SUPERVERTALER_HOST=memoq`
  set; build it with `python tools/build_mcpb.py`. Other MCP clients: run the
  exe with that variable set. Steps in the
  [docs](https://docs.supervertaler.com/memoq/mcp-server/#setting-it-up).
- **Live document link** – run `Supervertaler.MemoQ.Preview.exe` once (the deploy
  puts it in your Supervertaler data folder, `C:\Users\<you>\Supervertaler\memoq\preview\`)
  and accept memoQ's *Preview tool connection request*, leaving *Auto-start with
  memoQ* ticked. memoQ starts it itself from then on.

## Glossary format

Tab-separated, one term per line. See [`examples/glossary-example.txt`](examples/glossary-example.txt).

```
elektrische module	electric module
elektrische module	electrical module	forbidden
```

A third column containing `forbidden` marks a target that must not be used. Lines
starting with `#` are ignored; an optional first line `#! source=dut target=eng`
declares the language pair, and a glossary facing the wrong way for the project is
reported. The file is re-read whenever you save it, so you can edit it with memoQ
open – in any text editor, or as a grid in the companion window, which keeps the
header and comments where they were.

## Building

Requires the .NET SDK, an installed memoQ (the build references memoQ's own
assemblies; nothing is redistributed), and the `core/` submodule
(`git submodule update --init`).

```bash
bash build.sh              # build, verify, deploy to the Addins folder
bash build.sh --no-deploy  # build and verify only
```

`build.sh` refuses to run while memoQ or the prompt editor is open – each locks
its own file – and runs `tools/smoketest.ps1`, which loads the build through
memoQ's *own* add-in loader before deploying. That check exists because memoQ's
failure mode is silent: a plugin it cannot load simply never appears, with no
error anywhere. After deploying it verifies that what reached `Addins` is what was
built, and says so by name when it is not.

The behaviour harnesses in `tools/*-test.ps1` run through `tools/run-harness.ps1`,
which snapshots and restores the shared settings and never lets a test reach a real
API key. They must not run while memoQ is open.

## Layout

| | |
|---|---|
| `src/Supervertaler.MemoQ` | MT engine, options dialog, MCP bridge, capture and staging stores |
| `src/Supervertaler.MemoQ.Terms` | Terminology provider (its own DLL: memoQ loads one module per assembly) |
| `src/Supervertaler.PromptEditor` | Standalone prompt library editor |
| `src/Supervertaler.MemoQ.Preview` | Preview-SDK tool: the live document link (deploys into the Supervertaler data folder, never into `Addins`) |
| `core/` | Shared Supervertaler code (submodule) |
| `tools/` | Smoke test, deploy script, the harness suite and its runner, glossary converter, `.mcpb` builder |

## Licence

Copyright © 2026 Michael Beijer. All rights reserved.

The source is published so users and reviewers can see what the plugin does. It is
not open source: see [LICENSE](LICENSE).

---

[supervertaler.com](https://supervertaler.com) · [docs.supervertaler.com](https://docs.supervertaler.com)
