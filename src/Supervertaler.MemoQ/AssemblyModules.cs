using System.Runtime.CompilerServices;
using MemoQ.Addins.Common.Framework;

// THE registration that makes memoQ load this assembly at all.
//
// memoQ's loader (MemoQ.Common.Framework.Modules.ModuleManager.loadAssemblyModules)
// calls tryGetModuleAttribute(assembly) FIRST. No attribute, no load — it never
// looks at the types, never checks the signature, and shows nothing: no error,
// no "unsigned plugin" warning, and no entry in MT settings. An assembly that
// implements every interface correctly is still invisible without this line.
//
// ClassName must be the assembly-qualified-free full name of the director type.
// Rename or move SupervertalerMTPluginDirector and this string must move with it;
// nothing checks it at compile time.
//
// ONE MODULE PER ASSEMBLY. ModuleAttribute is declared AllowMultiple, but
// ModuleManager.tryGetModuleAttribute returns a single attribute and
// loadAssemblyModules loads exactly one module — so a second entry here is
// silently ignored, with the same no-error-no-warning failure as having none.
// The terminology provider therefore ships as its own DLL: see
// src/Supervertaler.MemoQ.Terms.
[assembly: Module(
    ModuleName = "Supervertaler",
    ClassName = "Supervertaler.MemoQ.SupervertalerMTPluginDirector")]


// The terminology provider lives in its own assembly (memoQ's one-module-per-DLL
// rule) but is the same product, and needs the shared index, settings and log.
// Not strong-named, so a simple name is enough here.
[assembly: InternalsVisibleTo("Supervertaler.MemoQ.Terms")]
