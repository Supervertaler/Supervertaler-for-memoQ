# Handoff: the Images panel for memoQ (Trados #84 → memoQ)

**From:** the Supervertaler for Trados session, 2026-09-07
**Asked by:** Michael – "is there any way to implement this entire thing in a similar way in Supervertaler for memoQ?"
**Status:** design and a proposed split; nothing built on the memoQ side yet. Two questions at the end need a memoQ answer before the core move starts.

## What it is, from the user's chair

The AI sees the text of a document, not the pictures in it. The Images panel gives it a description of every image in two steps, and every string in it was rewritten today with a new user in mind (the general case, never "patents"):

1. **Your documents** – a summary line ("1 image in 1 of 3 documents; the rest have none.") and a scrollable list of every source document, the ones with images first: "X.docx: 3 images, 3 with a figure label, paired by position and checked".
2. **Step 1 – Extract images to a folder…** Asks for a folder the first time (an empty one is fine, remembered per project), then copies the images out as `Figure 01.png`… A "Change…" link for someone who already keeps the images in a folder; "Open folder".
3. **Step 2 – Describe images with AI** (cost stated: "N AI requests to Gemini (Google), one per image") or **Describe from the text only** (free; the alternative, not a third step). Both write `figures.md` to the active memory bank and ask before replacing one that exists.
4. **Result** – "Descriptions saved 2026-09-07 22:34 · 1 figure · with what the AI saw · figures.md in memory bank “x”, read by the AI with every request."

Rules Michael set today, all of which apply to memoQ too:
- Images come **only from the project's source documents**, never from a folder sweep.
- `figures.md` **never goes to the shared bank** (every project reads it). If the active bank is the shared one, Step 2 is greyed and Result offers one link: *Create memory bank “<project-name>” for this project and switch to it*.
- Buttons that cannot run yet say why in their note; nothing is silently grey.
- Docs: https://docs.supervertaler.com/trados/batch-operations/#images-from-v1820189 (three screenshots; the Word/figures.md pair is the explanation).

## What exists on the Trados side, and where it can live

| Piece | Lines | Depends on | Proposal |
|---|---|---|---|
| `Core/DocxImageExtractor.cs` – images in document order, figure labels (ordinal / proximity / refused), descriptions from the text, extraction to files | 750 | **DocumentFormat.OpenXml** (Trados ships it for Import/Export) | move to core **if** memoQ can take the OpenXml package; otherwise port to `System.IO.Compression` + XML the way `DocxStructure` was written (real work, ~a day) |
| `Core/FigureAnalyzer.cs` – the vision request per image, the parse of what the model saw and the signs it read | ? | `LlmClient` (core) only | move to core as is |
| `Core/NumeralInventory.cs` – reference numbers cited in the text, for the "in the figure but not in the text" diff | 335 | nothing | move to core as is |
| `Core/ReferenceImages.cs` – list/suggest an images folder, parse `Figure 01` names | 217 | nothing | move to core as is |
| `WriteFiguresWithVision` + `DrawingsOnlySigns` + `WriteFiguresFile` (the figures.md writers) | ~320 | `Path`, the bank folder | move the Markdown rendering to core (`FiguresFile.Write…`), host passes the bank dir |
| `ProjectSourceDocx`, `BuildImagesState`, `ExtractImagesToFolder`, `OnAnalyseFiguresRequested`, `ShowDocumentImagesReport` | ~650 | Studio project API, `ProjectSettings` (per-project folder), bank switching | host-specific; the memoQ twin uses the list below |
| `Controls/ImagesDialog.cs` – the panel | ~300 | WinForms only | copy, or move to core if core takes WinForms (it does not today) |

## memoQ specifics I can see from here

- **Source document:** `SourceDocument.ImportPath` from the Preview SDK (already used for structure context) – the original .docx on disk, no embedding to unwrap. Only when the preview tool is connected; the capture-only path has no document, so the panel says so.
- **Memory banks:** memoQ has them and `memory-bank-projects.txt` maps a project to a bank, so "create a bank named after the project and switch to it" has a home. Shared-bank rule identical.
- **Per-project images folder:** Trados keeps it in `ProjectSettings.ReferenceImagesFolder`; memoQ needs an equivalent key per project (shared.txt has per-project keys already?).
- **Vision:** `LlmClient` in core already sends images for every provider, so the analyse step is the same call.
- **Where the panel lives:** memoQ has no Batch Operations tab; the companion window is the natural host, as a button/link near AutoPrompt.

## Proposed order

1. **memoQ answers two questions** (below).
2. **Trados moves the shareable files into core** in one mechanical commit (extractor, analyzer, inventory, reference images, figures.md writer), with the Trados plugin using them from there. No behaviour change on either side; harness unchanged.
3. **memoQ builds its panel** on those, mirroring the Trados dialog's strings (copy `ImagesDialog.cs` – it is plain WinForms – and swap the actions).
4. Docs: one page each, same screenshots where they apply.

## Two questions for the memoQ session

1. **OpenXml:** can the memoQ plugin take a `DocumentFormat.OpenXml` dependency (it is a 6 MB DLL in the plugin folder; Trados ships it already)? If not, say so and the extractor gets ported to `System.IO.Compression` before it moves – which also removes the dependency for Trados.
2. **Surface:** where should the Images button live in memoQ, and is there a per-project settings store for the images folder?

Reply in the session; the core move starts on the answers.
