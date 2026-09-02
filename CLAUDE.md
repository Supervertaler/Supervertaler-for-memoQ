# Supervertaler for memoQ – Claude Context

## What this project is

A memoQ add-in that brings Supervertaler's AI translation into memoQ. It is **not**
a port of Supervertaler for Trados — memoQ's plugin model is far narrower, and the
feature set has to be re-shaped around it rather than carried across.

Status: **vertical slice**. One MT engine, segment-by-segment and batch, with an
options dialog. Everything else is still ahead.

## The constraint that shapes everything

memoQ gives an add-in **no UI surface**. No view part, no dockable panel, no ribbon
button, no menu item, no keyboard shortcut. The only UI an add-in owns is the
options dialog reached from *Resources > Settings > MT*.

An add-in is also **only ever called** — it can never reach out. There is no project
object model, no segment grid access, no way to read or write the active segment.
Everything the plugin knows arrives as arguments to a translation request.

Consequence: anything in Supervertaler that *is* a panel (prompt library, AI
assistant, SuperSearch, terminology editor) cannot live inside memoQ. It goes in a
companion application. That is not a workaround; it is the architecture.

## The five memoQ SDKs

All ship as interface assemblies in the memoQ install directory. Only the first is
used so far.

| Assembly | Contract | Use |
|---|---|---|
| `MemoQ.MTInterfaces` | `IPluginDirector2` → `IEngine2` → `ISession` / `IRichSession2` | **in use** — translation |
| `MemoQ.TBInterfaces` | `IPluginDirector` → `IEngine` → `ISession.Lookup(Segment)` | TermLens lands here |
| `MemoQ.TMInterfaces` | `ISession.Lookup` + `Concordance` | Supervertaler TM provider |
| `MemoQ.QAInterfaces` | `IBatchQAChecker` / `ISegmentLevelQAChecker` | QA over mqxliff streams |
| `MemoQ.GCInterfaces` | `ISession.CheckGrammar` | inline LLM proofreading |

There is also a **Preview SDK**, distributed separately rather than as an assembly
in the install directory — and it is more interesting than the others.

Confirmed in the UI at **Options > External preview tools**:

- a checkbox, **"Allow external preview tools to connect to memoQ"** (on by default);
- a list of installed external preview tools;
- **Advanced options** exposing the connection endpoints:
  - REST API base: `http://localhost:8088/MQPreviewService`
  - REST API full: `http://localhost:8088/MQPreviewService/1`
  - Named pipe base: `MQ_PREVIEW_PIPE`
  - Named pipe full: `MQ_PREVIEW_PIPE_1`

So a running memoQ exposes a **local REST API and a named pipe** that an external
process may connect to, and memoQ pushes it live document-preview events. This is
the only known route by which something outside memoQ can learn where the
translator is. It is what makes a position-aware companion app plausible at all —
worth pursuing before assuming the companion has to be a detached side window.

Not yet verified: whether those events carry segment identity, and what the payload
looks like. Ask memoQ for the Preview SDK package.

## Preview SDK — the contract, recovered 2026-09-02

memoQ's separately distributed **memoQ PDF Preview** tool ships
`MemoQ.PreviewInterfaces.dll` (53 KB), which IS the Preview SDK contract.
Installed at `C:\Program Files\memoQ\memoQ PDF Preview\`; downloaded from
https://docs.memoq.com/current/en/memoQ-PDF-preview-tool/memoq-pdf-preview-tool.html
(a public download — the assembly is not under NDA, though redistribution
terms are still unstated). Reflected in full;
the shape is:

**Transport.** Two protocols, same messages: a named pipe (`MQ_PREVIEW_PIPE`)
or REST (`http://localhost:8088/MQPreviewService`, with the tool hosting a
callback controller via `System.Web.Http.SelfHost`). memoQ's side is
configured in `C:\ProgramData\MemoQ\{Rest,NamedPipe}ProtocolWrapperSettings.xml`.
`PreviewServiceProxy(IPreviewToolCallback, baseAddress, protocol)` wraps it.

**Registration.** `Register(RegistrationRequest)`: a tool GUID, name,
description, an `AutoStartupCommand` (memoQ launches the tool itself —
the PDF tool has "Auto-start with memoQ"), a `PreviewPartIdRegex`, a
`ContentComplexityLevel` (`Minimal` | `PlainWithInterpretedFormatting`), and
`RequiredProperties` from {WebPreviewBaseUrl, Wpm, Cps, LineLengthLimit,
WordCount, CharCount}. Then `Connect(guid)`. memoQ shows registered tools
under Options > External preview tools and asks the user once
("Preview tool connection request", with "Can change focus").

**What memoQ pushes** (`IPreviewToolCallback`):
- `HandleContentUpdateRequest(PreviewPart[])` — each part: `PreviewPartId`,
  `SourceDocument {DocumentGuid, DocumentName, ImportPath}`, source/target
  language codes, and **`SourceContent` AND `TargetContent`** as text. Fires on
  every edit: one real log shows ~4,900 of these over a few months.
- `HandleChangeHighlightRequest(ActivePreviewParts[] with source/target
  FocusedRange {StartIndex, Length})` — **where the cursor is**, on every
  move (~4,500 in the same log).
- `HandlePreviewPartIdUpdateRequest`, `HandleDisconnect`.

**What the tool may send:** `RequestContentUpdate(previewPartIds,
targetLangCodes)` — pull any parts, and `RequestHighlightChange(previewPartId,
langs, sourceContent, targetContent, focused ranges)` — which the PDF tool
uses to make a click in the PDF select the matching memoQ segment ("Select
only one segment in memoQ then click the Align mode button…"). That is, in
all likelihood, **`go_to_segment`**.

**Why this matters.** Every ✗ in the MCP tool table exists because the MT/TB
SDKs never show a plugin the target text, the active row, or the document
name. The Preview SDK shows all three, live. A Supervertaler preview tool
registration — in the plugin process or the editor exe — would give the
bridge `get_active_segment`, target text for QA (`check_numbers`,
`find_inconsistencies`…), real document names without the disk hack, and
probably cursor navigation. `stage_translations` + Pre-translate stays the
only write channel; the Preview SDK has no "set target text" call.

**Measured with the spike (`src/Supervertaler.MemoQ.Preview`), 2026-09-02:**
- Accepting the "Preview tool connection request" dialog in memoQ *is* the
  connection: `Register` returns accepted and a subsequent `Connect` throws
  `PreviewToolAlreadyConnectedException`. `Connect` is for a tool that is
  already registered and starts later (the auto-start path).
- Part ids look like `mQ-default-<view-guid>-<n>`. The view guid is constant
  for a document but is NOT the document guid (that arrives separately in
  `SourceDocument.DocumentGuid`, alongside `DocumentName` and `ImportPath`).
  `<n>` is a stable per-row integer whose order did not obviously match the
  grid's row numbers on first sight — treat it as an opaque key and sort
  numerically only as a guess until the full id list has been compared.
- A highlight change delivers the active part in full: source AND target
  content, language codes, `WordCount`/`CharCount`, and the focused ranges
  (0..length of each side when a whole row is selected).
- A content update fires for exactly the part that changed, after an edit or
  confirm, with the new target text.
- `PlainWithInterpretedFormatting` renders inline formatting as `<b>…</b>`;
  tags come through as HTML-ish markup, not memoQ tag objects.
- The tool's own process, not the plugin, must host this: memoQ launches it
  from `AutoStartupCommand` and shows it under Options > External preview
  tools with "Auto-start with memoQ".

**Blocker:** `MemoQ.PreviewInterfaces.dll` ships with preview tools, not with
memoQ, and its redistribution terms are unknown. Adam has been asked for the
SDK package; that is now a concrete, specific request: the assembly plus
permission to reference it. Do not copy the DLL out of the PDF tool's folder
into our repo.

**Privacy note:** the PDF tool's `%APPDATA%\MemoQ.PDFPreview\logs.txt`
names every document it ever saw. Never paste from it.

## What memoQ hands us (and Trados does not)

`TranslationBundle` carries context the Trados plugin has to assemble itself:

- `PlainTextContext` — `List<PlainTextContextItem>` (`Kind`, `Text1`, `Text2`, `NumericValue`)
- `SegmentContext` — `List<SegmentContextItem>` (`Kind`, `SourceSegment`, `TargetSegment`, `NumericValue`)

`ContextKinds` values: `Terminology`, `ForbiddenTerm`, `MetaInfo`, `TextFlowContext`,
`TranslationPair`.

**Which list a given kind lands in is not documented.** `PromptBuilder` therefore
looks for every kind in *both* lists, and `SupervertalerRichSession.LogContextShape`
logs what actually arrived on the first bundle of each batch. Read the log before
assuming.

**`IRichSession2` is unreachable for a third-party MT plugin — design around it.**
Measured 2026-08-30: memoQ calls `CreateLookupSession` for everything, Pre-translate
included (it uses `ISession`'s array overload). `CreateRichLookupSession` has never
been called once. A term marked forbidden in the project term base came straight
back in the output, because the plugin was never told it existed.

This is not a mistake on our side. Reflecting the two shipped comparators settles it:

| Plugin | Session interfaces |
|---|---|
| `MemoQ.ModernMT.dll` (bundled, signed) | `ISessionWithMetadata`, `ISession`, `ISessionForStoringTranslations` |
| `MemoQ.IntentoMT.dll` (bundled, signed) | `ISession`, `ISessionForStoringTranslations` |
| `LaraPlugin.dll` (current ModernMT/Lara, signed) | `ISessionWithMetadata`, `ISession`, `ISessionForStoringTranslations` |

**None of them implements `IRichSession`.** The most context-hungry adaptive MT
engines on the market do not use it, which means `TranslationBundle` — and with it
`ContextKinds.Terminology` and `ForbiddenTerm` — is effectively AGT-only.

**Do not claim `Capabilities.AGT` to try to unlock it.** Tested and rejected:
returning true from `HasCapability("AGT")` removed Supervertaler from the
*Pre-translation* dropdown in *Edit machine translation settings > Settings* — it
read "No plugins selected" and the engine could not be chosen at all.

### What to use instead

The three interfaces the shipped plugins use, all of which we now implement:

- **`ISession`** — the actual translate path, single and array overloads.
- **`ISessionWithMetadata`** — same, plus `MTRequestMetadata`: `ProjectGuid`,
  `DocumentID`, `Client`, `Domain`, `Subject`, and per-segment `SegmentID` /
  `SegmentIndex` / `SegmentStatus`. The only channel by which an MT plugin learns
  where it is. `Client` / `Domain` / `Subject` go into the prompt; `DocumentID`
  keys `DocumentMemory`.
- **`ISessionForStoringTranslations`** — every segment the translator confirms, as
  they confirm it. Requires `StoringTranslationSupported => true` on the director,
  and is what puts an engine in the *Self-learning MT* dropdown.

`Core/DocumentMemory.cs` joins them: confirmed pairs are recorded per document and
the most lexically similar ones are fed back into later prompts. It is the
substitute for the neighbouring-segment context we cannot have — and arguably
better, since every example is human-approved rather than merely adjacent.

Terminology proper still has no route through the MT SDK. If memoQ confirms that,
TermLens belongs on the **TB SDK** (`MemoQ.TBInterfaces`) instead.


## Reading memoQ's own data off disk

Surveyed 2026-08-31, because AutoPrompt needs project context the MT SDK does not
give us. Findings, from most to least usable:

**Resource registries — usable.** `C:\ProgramData\MemoQ\Termbases.xml` and
`TranslationMemories.xml` are plain XML, one entry per resource, carrying `<Name>`,
`<Directory>`, `<ClientID>`, `<DefaultDomain>`, `<DefaultSubject>`. A project's
`project.mprx` (also plain XML, under `Documents\My memoQ Projects\<name>\`) lists
the `<TBGuid>` of every attached term base plus `<SourceLangCode>`, `<Subject>`,
`<Domain>`. So *which* resources are attached, and what they are called, is
readable. Note the registry may point outside `ProgramData` — entries here are
symlinks to `D:\Google Drive\Software\memoQ\`, and stale `<Directory>` values
survive in the file, so resolve and existence-check rather than trusting the path.

**Translation memories — effectively closed.** A TM folder is a pile of `.sst`
files: RocksDB/LevelDB SSTables. Reading one means a native RocksDB binding *and*
memoQ's undocumented key schema, re-verified every release. Not worth it — see
below for what to use instead.

**Documents — recoverable but fragile.** The per-document store at
`Documents/<docGuid>/ver1/majorVersionStore.dat` (backslashes on disk) is an
undocumented length-prefixed binary format, but the
segment text sits in it in the clear, keyed `trans-units / trans-unit#N`. Verified
against the example patent: all 21 source segments extracted. Usable as a fallback,
never as the primary route — the format has no compatibility promise.

**The conclusion that matters:** do not build on any of this if the SDK already
carries it. `MTRequestMetadata` gives Client/Domain/Subject; `DocumentMemory` gives
real confirmed translation pairs for the document in hand; the glossary gives terms;
and every source segment passes through `TranslateMany` anyway. Disk reading is for
the one thing none of those provide — *which* term bases and TMs the project has
attached, by name.

## Tech stack

- **C# / .NET Framework 4.8, x64, WinForms** — must match memoQ.exe exactly; the
  add-in loads into its AppDomain.
- memoQ 12 is `12.4.53`. `MemoQ.MTInterfaces` is assembly version `3.0.0.0`,
  `MemoQ.TBInterfaces` / `MemoQ.TMInterfaces` are `2.0.0.0`.
- Build: `bash build.sh` (build → smoke test → deploy).
- No NuGet packages, deliberately — see "Dependencies" below.

## Where settings live (moved out of memoQ, 2026-09-02)

memoQ persists an MT plugin's settings inside an **MT settings resource**, and
the only way to that dialog is Project home, Settings, MT settings, right-click
the provider, Edit, find Supervertaler, Options. The user works with a single
resource and wanted the settings reachable without opening memoQ at all, so
they now live in two files the plugin and the prompt editor both read:

- `C:\Users\<you>\AppData\Local\Supervertaler.memoQ\shared.txt` — one
  `key=value` per line: glossary, bridgemode, provider, model, endpoint,
  parallel, batchsize, useterminology, usedocumentcontext, promptpath.
- `...\instructions.txt` — the inline instructions, in their own file because
  they are the one setting that spans lines.

Three rules hold this together:

1. **`SharedSettings` knows nothing about the settings model.** The prompt
   editor compiles the very same source file (a `<Compile Include>` link in its
   csproj, alongside `LlmProviders.cs`), so it must not reference memoQ SDK
   types. It reports errors through an `ErrorSink` the plugin points at its log
   and the editor leaves silent.
2. **`EngineContext.General` is the single overlay point.** It returns the
   stored resource values with anything the shared file carries laid over the
   top, resolved on every access so a change in the editor lands on the next
   segment. All eight consumers read settings through it and none of them know
   two stores exist.
3. **Every accessor has an `...Or(fallback)` form**, answering with the
   resource value until the file has ever carried that key. That is the whole
   migration: nothing is copied on upgrade and no flag records whether a move
   happened.

`EngineContext`'s constructor calls `SharedSettings.SeedIfUnset(...)`, filling
gaps from the resource memoQ just handed us, so the editor shows what is
actually in force rather than its own defaults.

### API keys

Resolved in `Core/ApiKeys.cs`, in order: an explicit `apikey` in the shared
file, then the key Supervertaler for Trados keeps for that provider, then the
copy of memoQ's own key seeded into `apikey.memoq`. Trados stores its keys in
plain JSON at `D:\Supervertaler	rados\settings\settings.json` under
`aiSettings.apiKeys` (`claude`, `openai`, `gemini`), so a translator running
both products rotates a key once. Clear text on disk is a deliberate choice and
matches what Trados has always done.

Two things bit while writing that reader. The file carries a **UTF-8 byte order
mark**, which the JSON reader rejects as an unexpected character. And
`DataContractJsonSerializer` walks members in contract order and returned
**every key empty** against a file that plainly had them; it is the wrong tool
for a document another product owns. `JsonReaderWriterFactory` into an
`XDocument` is order-independent and indifferent to the fifty other settings.

### The harness trap this creates

**Always run a harness through `tools/run-harness.ps1`.** A harness builds an
engine from a bare defaults object, and two things follow from that:

- Seeding writes those defaults into `shared.txt`, and because seeding only
  fills gaps, memoQ then finds the keys present and never seeds the real ones.
  One unguarded run silently blanks the user's selected prompt.
- Key resolution finds the user's real key, so a test written around "no key
  configured" instead makes a **billable call**. This happened: a run reached
  AutoPrompt and drafted a prompt against the user's Anthropic account.

Both are off when `SUPERVERTALER_HARNESS` is set, which the wrapper and
`build.sh` (whose smoke test also builds an engine) both do. The wrapper still
snapshots and restores both files as well. Check `shared.txt` after any run that
skipped it.

## Gotchas that have already bitten

**memoQ will not load an assembly without `[assembly: Module(...)]`.** This is the
single most important fact about the plugin model, and it is invisible:
`ModuleManager.loadAssemblyModules` calls `tryGetModuleAttribute(assembly)` FIRST
and returns immediately when it is absent. No types are examined, no signature is
checked, and nothing is reported — no error dialog, no "unsigned plugin" warning,
no entry in MT settings, nothing in memoQ's own log. An assembly that implements
every interface perfectly is simply invisible. See `AssemblyModules.cs`.

The corollary: **do not hand-roll discovery checks.** The first version of
`tools/smoketest.ps1` verified "a type implements IPluginDirector2 and IModule",
passed, and the plugin was still invisible. The smoke test now calls memoQ's own
`tryGetModuleAttribute` / `IsMTAddin` / `IsAddinSigned` through reflection, which
is the only check worth trusting.

**`TranslationResult.Confidence` / `.ConfidenceProviderName` are AIQE fields, not
a match rate.** AIQE is memoQ's AI Quality Estimation feature (its own tab in the
MT settings dialog, with COMET and other providers behind an API key). Setting
those two makes memoQ render `AIQE: <name>   Score: (n%)` against every segment and
treats the plugin as a quality-estimation provider. Setting them to a constant —
which the first build did, at 0.75 — presents a fabricated number as a per-segment
quality judgement. Leave both unset unless Supervertaler genuinely scores its own
output. `Info` is the free-text field for "where did this come from", and it shows
under the hit in Translation results.

**`Supervertaler.MemoQ` shadows memoQ's own `MemoQ` root namespace.** Inside our
code, `MemoQ.MTInterfaces.MTException` resolves against `Supervertaler.MemoQ` and
fails to compile. Always `using MemoQ.MTInterfaces;` and reference types unqualified
— never inline-qualify with a leading `MemoQ.`.

**`PluginSettingsObject<TG,TS>.GeneralSettings` / `.SecureSettings` are readonly
fields**, not properties. They can only be set through the
`(TGeneralSettings, TSecureSettings)` constructor. Use
`SupervertalerSettings.Create(...)`, never an object initialiser.

**Git Bash mangles MSBuild switches.** Use `-p:` not `/p:` (a leading slash becomes
a Windows path, so the property arrives as a second project argument), and set
`MSYS2_ARG_CONV_EXCL="-p:"` — which then means `$PROJECT` must be passed through
`cygpath -w` itself.

**memoQ fails silently.** If the plugin type cannot be discovered, constructed or
initialised, the engine just does not appear in the MT list. Nothing is logged and
no error is shown. `tools/smoketest.ps1` exists to tell that apart from "I picked
the wrong menu" — it loads the built DLL exactly as memoQ's scanner does and drives
it through construction, `Initialize`, `CreateEngine` and both session factories.

## Dependencies: ship nothing

Add-ins are probed out of memoQ's own install directory
(`memoQ.exe.config`: `privatePath="DocConverters;Addins;CefSharp;x64"`), so anything
we drop into `Addins` competes with memoQ's own copy of the same library.

memoQ already ships `System.Data.SQLite.dll` + `SQLite.Interop.dll` — the exact DLL
recorded in the Trados plugin's `CLAUDE.md` as the cause of
`EntryPointNotFoundException` there. We would now be loading *into that process*.

So: every memoQ reference is `Private=false`, and the slice has zero NuGet packages.
JSON parsing uses memoQ's own `MemoQ.Addins.Common.Utils.JSON`. When
`Supervertaler.Core` arrives with `Microsoft.Data.Sqlite`, the Trados plugin's
`AppInitializer` trick (pre-load `e_sqlite3.dll` by full path, handle
`AssemblyResolve` for every shipped managed DLL) becomes mandatory, not optional.

## Distribution

memoQ has no App Store. Three tiers, per memoQ support:

1. **Unsigned private** — build and distribute it yourself; users drop the DLL into
   the `Addins` folder and dismiss a one-time "unsigned plugin" warning. No review.
   **This is the starting point.**
2. **Signed private** — send memoQ the compiled DLL plus `PublicKey.xml` and a
   `.KGSIGN` file; the public key goes into the next maintenance release. Removes
   the warning and earns a listing on memoQ's website linking out to our download.
3. **Public** — full code review, QA and bundling into memoQ. Reserved for proven
   demand.

The `.kgsign` files next to each shipped add-in are base64 RSA signatures (1024-bit)
verified against a key baked into memoQ. Unsigned genuinely works: memoQ's own
`MemoQ.MSWordGC.dll` ships without a `.kgsign`, and our unsigned build loaded with
**no warning shown at all**. A `MemoQ.Common.Framework.Modules.IUnsignedPluginsApprove`
interface exists, so an approval prompt does happen in some configurations — it
simply did not fire here. Do not treat the absence of a warning as a sign that
something is wrong.

**Installation needs administrator rights** — the `Addins` folder is under Program
Files and there is no per-user equivalent. The path is version-stamped
(`memoQ-12`, `memoQ-13`, …), so a memoQ upgrade means a re-deploy. An installer that
locates the current memoQ directory is a real requirement, not a nicety.

## Roadmap

1. ~~MT plugin vertical slice~~ — done; loads in memoQ 12.4, appears in MT settings,
   options dialog opens, error paths report correctly.
2. ~~Get memoQ to call `CreateRichLookupSession`~~ — established that it never
   will; designed around it via `ISessionWithMetadata` + `ISessionForStoringTranslations`
   + `DocumentMemory`. **Verified working end to end 2026-08-30**: confirming a
   segment with a chosen term causes later segments to adopt it. Awaiting memoQ's
   answer on whether terminology is reachable at all.

   **`StoringTranslationSupported => true` only makes the engine _eligible_.** memoQ
   does not call `CreateStoreTranslationSession` until the plugin is selected under
   *Edit machine translation settings > Settings > Self-learning MT*, and the engine
   is rebuilt (restart memoQ after changing it). Until then every request logs
   `held=0` and nothing is captured — with no indication why.
3. ~~Batch several segments per LLM call~~ — shipped. `Core/BatchTranslator.cs`,
   using Core's `TranslationPrompt` batch format so library prompts written for
   numbered batches work here. memoQ hands the array overload about 10 segments
   at a time, so `BatchSize` above 10 has no effect.
4. ~~Extract `Supervertaler.Core` from the Trados plugin~~ — in progress as the
   `core/` submodule (Supervertaler-Plugin-Core): LlmClient, prompt library,
   TranslationPrompt, PromptGenerator, DocumentAnalyzer and friends. Both plugins
   compile the same sources in.
5. ~~TB plugin for TermLens~~ — shipped as `Supervertaler.MemoQ.Terms.dll`.
   Span arithmetic: map the LAST character's position and add one; mapping the
   exclusive end and adding one put spans one past the segment, which memoQ's
   tracked-changes converter turns into an ArgumentOutOfRangeException dialog.
6. Companion app for everything memoQ will not host. First piece shipped:
   `src/Supervertaler.PromptEditor`, a standalone WinForms exe launched from the
   options dialog's **Edit…** button beside the prompt picker. It deploys into
   `Addins` next to the DLLs — not because memoQ loads it (it never does) but
   because that is where the dialog looks for it.

   Written in C# against Core's `PromptLibrary` rather than in the companion's
   own language, deliberately. The format has 17 frontmatter keys in live use,
   ordered, with legacy aliases; a second implementation of it is exactly how
   nine of those keys came to be silently deleted on save. One parser, one
   writer, three consumers.

   It knows one thing the plugins do not: **which placeholders each host fills.**
   `ApplyVariables` substitutes an empty string for anything the caller had no
   value for, so `{{SOURCE_SEGMENT}}` in a memoQ prompt does not survive as
   visible text — it silently becomes nothing. The editor colours unknown
   placeholders red and warns when a prompt targeting memoQ uses one memoQ will
   not fill.
7. ~~Bridge + MCP server~~ — shipped 2026-09-01. `Core/MemoQBridge.cs` is an
   `HttpListener` inside the plugin speaking the same protocol as the Trados
   bridge; the unmodified `SupervertalerMcpServer.exe` drives it when pinned via
   `SUPERVERTALER_BRIDGE_FILE` to `<root>/memoq/runtime/bridge.json` (backslashes on disk). Verified
   end to end: initialize, tools/list (12 memoQ tools), get_project,
   stage_translations, all through the real exe.

   The tool set is the honest subset — no `go_to_segment`, no `update_segments`:
   memoQ gives a plugin no UI access. Instead the write channel is
   `stage_translations` + `Core/StagedTranslations.cs`: Claude stages pairs
   keyed on source text, and they reach the grid when the user runs
   Pre-translate or lands on the segment (checked before cache and LLM in
   SessionRunner and BatchTranslator, so a fully staged document costs zero
   LLM calls and needs no API key). `Core/CaptureStore.cs` records every
   source segment the plugin sees, which after one Pre-translate pass is the
   whole document — that is what get_project/get_segments serve. The bridge
   also exposes the glossary (lookup/add) and the shared prompt library
   (list/get/save), so Claude can draft a project prompt and the user selects
   it in the options dialog.

   **Bridge mode** (`BridgeMode` setting, user-facing label "Pre-translate only
   captures and delivers staged translations") is scoped to the batch path only.
   Pre-translate then costs nothing — capture on the first pass, delivery of
   staged translations on the second — while single-segment lookups keep calling
   the model, so a chat-driven job still gets live suggestions for rows Claude
   has not staged. It never needs toggling mid-job.

   **Second capture channel:** the TB plugin's `Lookup` records every visited row
   into `CaptureStore` under a `visited_<pair>` bucket, regardless of MT provider.
   Its own key prefix, not the MT path's `nodoc_`: sharing a bucket made the
   origin reported over the bridge depend on which channel created it first.

   **Handshake ownership:** the bridge writes `bridge.json` only when the file is
   missing, ours, or owned by a dead PID. A test harness that loaded the DLL once
   overwrote memoQ's live handshake and exited, leaving memoQ listening on a port
   nothing could find. The harnesses now refuse to run while memoQ is open.

   **Language pair for staging** comes from the most recently active capture
   bucket, not the latest engine: memoQ builds one engine per target language in
   an order of its own, and the "latest" was German on a Dutch job.

8. ~~AutoPrompt~~ — shipped 2026-09-02. Button in the prompt editor; the work
   happens in the plugin over the bridge (`POST /v1/autoprompt`, not an MCP
   tool), because the captured document, confirmed pairs, glossary and API key
   are all inside memoQ's process. Same pipeline as Trados — keyword analysis,
   classification call, `PromptGenerator.BuildMetaPrompt` — plus a
   `HostConstraints` block (Core) describing how memoQ consumes a prompt:
   single unnumbered segments as well as batches, tags reproduced exactly,
   request-time confirmed pairs outrank the prompt's glossary, 1500-3000 words
   because the prompt is re-sent every ~10 segments. Inline translator
   comments are kept as in Trados — they land in the target cell and the
   translator processes them during review; the first draft of this block
   forbade them, and the user wants them. **Delimiter differs:** memoQ prompts
   use `[[TC: ...]]`, not `⟦TC: ...⟧` — U+27E6/U+27E7 are missing from Tahoma,
   Verdana and Calibri, the fonts memoQ's grid uses, and render as boxes.

   Document labels: memoQ gives the plugin only a document GUID, so
   `Core/DocumentNames.cs` resolves it to the project folder name and the
   file name from `Documents\<guid>er1\majorVersionStore.info` (first
   printable string). Labels only, never keys.

9. ~~Live document link (Preview SDK)~~ — shipped 2026-09-02, the same day the
   SDK assembly was found inside memoQ's PDF preview tool.
   `src/Supervertaler.MemoQ.Preview` registers as a preview tool, forwards every
   content update and highlight change to the bridge (`Core/PreviewStore.cs`),
   and long-polls the bridge for `goto` commands, executed through
   `RequestHighlightChange`. **Verified live:** memoQ moved the cursor. Two
   facts that shaped it: memoQ does not echo a selection it was asked to make,
   so the tool reports the new active row itself after an accepted goto; and
   **A preview part is a PARAGRAPH, not a segment.** Measured: 11 parts for a
   21-row document; part 5 is 602 characters / three sentences = grid rows
   5-7. The "11 of 21" was never about loading. Consequences: `get_segments`
   in live mode returns paragraphs (documented as such in the tool
   description); the focused range in a highlight change identifies the
   sentence — the grid row — within the paragraph, and `get_active_segment`
   cuts it out as `activeSource`/`activeTarget`; `go_to_segment` accepts
   `sourceStart`/`sourceLength` to aim at a sentence. memoQ's id list also
   arrives in STRING order (1, 10, 11, 2, ...), so rows are ordered by the
   numeric tail of the id, never by the list. **Verified live 2026-09-02:** a jump
   with a range aimed at the third sentence of paragraph 5 put memoQ's cursor
   on grid row 7. Sentence-precise, not paragraph-precise. Bridge tools added: `get_active_segment`, `go_to_segment`;
   `get_segments` serves the live view (order, targets, isActive, real name)
   when the tool is connected. The exe deploys to `<Supervertaler data folder>\memoq\preview\`
   (`D:\Supervertaler\memoq\preview\` here) — never `Addins`, because it
   carries its own Newtonsoft.Json and memoQ probes Addins for its own copy;
   and never under `%LocalAppData%`, because of the next section.

10. ~~QA over the live document~~ — shipped 2026-09-02. `Core/QaChecks.cs`
    runs the Trados plugin's checks (numbers, tags, nbsp, terminology,
    inconsistencies) over PreviewStore rows: same semantics, paragraph units,
    tag markers compared by name as a multiset since memoQ delivers text
    markers rather than tag ids. `run_verification` stays impossible — memoQ's
    own QA is not callable by a plugin. Bridge: GET /v1/qa-check?type=… and
    GET /v1/inconsistencies; five MCP tools.

11. ~~memoQ `.mcpb`~~ — shipped 2026-09-02. `tools/build_mcpb.py` packs the
    Trados repo's `Supervertaler.McpServer` exe with a manifest whose only
    difference is `env: SUPERVERTALER_HOST=memoq`. The server (Trados repo)
    understands that variable: handshake at `<root>/memoq/runtime/bridge.json`,
    a separate tool cache, memoQ wording in the not-found message, no
    `list/select_trados_instance` tools, and no Trados fallback registry.
    Verified over stdio: 19 memoQ tools, no instance tools, get_project
    answered. One server, two thin bundles. `dist/` is gitignored (29 MB).

12. ~~Export glossary from a prompt~~ — shipped 2026-09-02. Core's
    `PromptGlossaryExtractor` reads every source/target table in a prompt
    (AutoPrompt's locked-terms table, or any laid out the same way), turns
    `never "X"` notes into forbidden rows, and writes the TB plugin's
    tab-separated format. Editor button **Export glossary…** writes to
    `<data folder>/memoq/glossaries/<prompt>.txt` and activates it over the
    bridge (`POST /v1/glossary/activate`, which sets `SharedSettings.GlossaryPath`).
    Tested on the real draft: 70 entries, `application → applicatie`, one
    forbidden (`apparaat`). Motivation: `check_terminology` against the general
    patent glossary flagged 9 of 11 paragraphs, every one a false positive.

## This shell cannot write to AppData (MSIX virtualisation)

Claude Code here runs inside the packaged Claude desktop app
(`Claude_pzs8sxrjxfjjc`). Windows virtualises the file system for packaged
apps: every write from this shell to `C:\Users\<user>\AppData\Local\...` or
`...\Roaming\...` — bash, PowerShell, python, child processes, even a
UAC-elevated child — lands in
`C:\Users\<user>\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...`.
Reads see a merged view, so memoQ's `plugin.log` reads fine and my own writes
look like they landed. Nothing outside the package sees them.

Found 2026-09-02 after an hour: seven files in every listing this shell could
produce, an empty folder in Explorer, Run and cmd. Everything (the search
tool) showed the real location.

Rules: deploy runtime files to the Supervertaler data folder (`D:`) or, via
the elevated `deploy.ps1`, to Program Files — both real. Never start the
preview tool from this shell: it inherits the sandbox, its log goes to the
package cache, and memoQ's auto-started copy is the one that matters. If a
listing here disagrees with what the user sees, the user is right.

**Orphaned preview tool:** when memoQ is killed rather than closed, the
preview tool it started lives on, and it will connect to ANY bridge whose
handshake appears — including a harness's — and consume its `goto` commands.
Two harness checks failed that way on 2026-09-02. The harnesses now refuse to
run while `Supervertaler.MemoQ.Preview.exe` is running; memoQ restarts the
tool itself, so killing an orphan costs nothing.

**The reverse trap:** once a harness run here has written
`%LocalAppData%\Supervertaler.memoQ\plugin.log` (or `preview-tool.log`), the
package copy SHADOWS memoQ's real one in this shell's merged view — reading
"the log" after that shows the harness's log, not memoQ's. Seen 2026-09-02
while looking into a memoQ hang: the tail was my own harness. Until the
plugin logs to the data folder instead, ask the user to copy the real file,
or read it through a path this shell does not virtualise.

## Confidentiality

Same rule as the Trados repo: **never use real client names.** `Acme` for a client,
`PROJ-001` for a case reference. This work is patent and legal translation under
confidentiality. With a project open, real names reach `plugin.log` — substitute
before pasting output into docs, changelogs, issues or commits.
