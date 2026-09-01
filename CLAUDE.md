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
3. Batch several segments per LLM call (`BatchSize` is already in settings).
4. Extract `Supervertaler.Core` from the Trados plugin — 71 of its 83 `Core/` files
   (~32,500 lines) have zero `Sdl.` references, as do 40 of 45 `Controls/` files.
5. TB plugin for TermLens (`TerminologyResult` supports `Color`, `Confidence`,
   `PrettyPrintHtml`, and source-span highlighting).
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
7. Bridge + MCP server. `Supervertaler.McpServer` is a standalone net8.0 exe that
   talks HTTP to an `HttpListener` bridge inside the plugin and fetches its tool
   registry from it — it does not know Trados exists, so it works here unchanged.
   The tool *set* shrinks: anything that drives the UI (`go_to_segment`,
   `update_segments`, `insert_into_active_segment`) has no memoQ equivalent.

## Confidentiality

Same rule as the Trados repo: **never use real client names.** `Acme` for a client,
`PROJ-001` for a case reference. This work is patent and legal translation under
confidentiality. With a project open, real names reach `plugin.log` — substitute
before pasting output into docs, changelogs, issues or commits.
