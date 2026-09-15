# Handoff: structure context (memoQ #7 ← Trados #109)

**From:** the Supervertaler for Trados session, 2026-09-07
**Core:** `git -C core pull --ff-only origin main` → `27f3420`
**Design of record:** Trados #109 (the issue and its comments carry every decision, including the four memoQ constraints from #7). Do not re-litigate the sentinel, the rule text, or the strip.

## What is in core now

Three files, no memoQ in them:

| File | What |
|---|---|
| `DocxStructure.cs` | `DocxStructure.ReadParagraphs(Stream)` – every paragraph of a .docx in document order, with `StartOffset`/`EndOffset` (character offsets of `<w:p` in `document.xml`), `Text` (stripped, entities decoded, tabs → `\t`, breaks → `\n`, text-box paragraphs excluded from their host's text and listed on their own), `StyleId`, `NumId`, `Level`, `Marker` (exactly what Word renders, or null), `IsBullet`. `DocxNumbering` does the resolving and counting; `Apply` is fed the whole document, always. The stream may be the .docx or a zip wrapping it (Studio's embedding shape); `OpenPackage` unwraps one level and returns null for anything else – never throws for a non-zip. |
| `StructureContext.cs` | `StructureContextMode { Off, Markers, Unavailable }`; `Sentinel(marker)` → `[#e)]`; `Prefix(marker, text)`; `Strip(text, out fired)` using `StripPattern = ^\[#[^\]]*\]\s*` (exposed as a const, verbatim as agreed; a leading space before an echoed sentinel is tolerated); `PreambleRule` and `FallbackRule` (the §5 strings); `RuleFor(mode)`. |
| `TranslationPrompt.cs` | `BuildSystemPrompt(…, StructureContextMode structureContext = Off)` – a `# DOCUMENT STRUCTURE` layer right after the base prompt and **before** the custom prompt, so it reaches prompts the plugin did not write. `Off` is byte-identical to the previous output (checked). |

`Supervertaler.Core.props` now references `System.IO.Compression`; your build picks that up from the props, nothing to add.

## Verified

- Synthetic document, 59 checks in `tools/structure_context_test.ps1` (Trados repo): decimal lists, a list whose abstract definition starts at 11, a `startOverride`, letters continuing across an interruption by another list, bullets (Symbol-font PUA glyph → `•`, a real `-` kept), style-based numbering from `styles.xml`, an empty numbered paragraph (counts, as in Word), two-level `%1.%2`, `numId 0` (none), old numbering inside `w:pPrChange` (ignored), entities, a text box, lowerRoman with a start, letters past z (`aa.`), the wrapper zip, a non-package zip, a document with no `numbering.xml`.
- The real patent (source .docx as imported): marker sequence `1. • • • 2. 3. 4. 5. 6. 7. • • • • 8. 9. a) b) c) d) e) f) 10. g) h) i) j) k) l) m) 11. 12. 13. 14.` – claim 10 is on its own list starting at 10, claims 11–14 on one starting at 11, and claim 10's steps continue claim 9's letters because the document never restarts them. That is what both previews show. A per-claim restart would be wrong here; follow the list ids.

## Your side (memoQ #7)

1. `SourceDocument.ImportPath` → `File.OpenRead` → `DocxStructure.ReadParagraphs`. Once per document; cache by path + write time if you like (Trados keeps one map).
2. Match preview parts to `DocxParagraph.Text` (stripped text, order as tiebreak). Empty paragraphs are in the list (Text `""`), so skip them when matching and they still count.
3. Marker on the paragraph's first segment only; `BatchSegmentInput.SourceText = StructureContext.Prefix(marker, text)` when mode is `Markers`.
4. Pass the mode to `BuildSystemPrompt`. `Markers` when the setting is on and the document yielded at least one marker; `Unavailable` when the setting is on and there is no path / not a .docx / no lists; `Off` otherwise.
5. `StructureContext.Strip` on every reply before it reaches the document, when the mode was `Markers`; log the segment index when `fired`. Trados logs to the batch log and the diagnostic log.
6. Setting name `StructureContext`, bool, **default off**. Trados: `AiSettings.StructureContext`, first shipping in 18.20.188. Docs describe one switch: "Send list numbering to the AI as structure context".

## Trados specifics, for the record

Studio publishes each paragraph unit's `StartsAt` (the `<w:p` offset in `document.xml`) as context metadata, and the original .docx sits base64 in the sdlxliff header, so Trados joins by offset and does no text matching. The count still runs over the whole document from `document.xml`, never over the batch.
