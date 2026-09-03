# memoQ docs: what 2026-09-03/04 left behind

Docs live in the `Supervertaler-Help` repo under `memoq/`. Two entries below are
**wrong**, not merely absent — those first.

## Wrong now

**`mcp-server.md`, line 115** — the capability table says:

> `| SuperMemory tools | ✗ | Not yet wired for memoQ |`

Three tools shipped: `list_supermemory_banks`, `get_supermemory_context`,
`search_supermemory`, over `GET /v1/supermemory-banks|-context|-search`. Worth
saying in the same breath that **no bank is selected automatically** — the
caller names one, an unknown name is an error rather than a fall back, and the
default budget is 6,000 tokens with anything dropped reported in `trimmed`. The
user's own `_shared` bank is large enough that the default drops two of its four
files, so this is not a footnote.

**`prompt-editor.md`, line 57** — explains `[[TC: …]]` as memoQ's difference
from "Trados's `⟦ ⟧`". Both products now use `[[TC:]]`; the paragraph should
describe the marker without the contrast. Note also that nothing extracts these
markers programmatically — the translator reads them in the grid and converts
the ones worth keeping into real CAT-tool comments by hand. Any doc implying an
extractor is describing something that does not exist.

## Missing

- **Activity window** — memoQ menu → Activity…, or Ctrl+L. A separate window,
  optional keep-on-top so it can sit over memoQ's modal Pre-translate dialog,
  which otherwise says only "Processing" for the length of a run. Shows engine
  and model, glossary load, direction warnings, batches with glossary hits, and
  errors. "Show everything" un-hides per-request diagnostics.
- **Preview context…** in the AutoPrompt dialog — shows the exact meta-prompt
  before the call, costs nothing, needs no API key, and the briefing box stays
  editable so the loop is look, add, look again, generate.
- **Product markers.** A prompt whose `app:` is `memoq`/`trados`/`workbench`
  gets ` [memoQ]` / ` [Trados]` / ` [Workbench]` on its filename; prompts
  available to both get nothing, so the absence of a marker is itself readable.
  The marker is derived from the field on every write and stripped on every
  read, so editing it by hand in Explorer does not change a prompt's product —
  the **Available in** dropdown does. Also shown in the editor's tree and in the
  prompt chooser.
- **Model dropdown** in Settings — populated from the provider's own models
  endpoint, cached for a day, still typeable for gateways and local models.
- **Ctrl+L** should join whatever shortcut list the docs keep.

## Deliberately not documented

The prompt-filename markers are the visible half of something with no UI: ten
built-in QuickLauncher prompts now declare `app: "trados"` because they use
placeholders memoQ cannot fill (`{{SELECTION}}`, `{{PROJECT}}`,
`{{TM_MATCHES}}`). Worth a sentence only if a user asks why those prompts are
greyed out or marked in the chooser.
