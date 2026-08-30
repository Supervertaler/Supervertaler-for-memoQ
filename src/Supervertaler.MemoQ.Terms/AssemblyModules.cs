using MemoQ.Addins.Common.Framework;

// Registers the terminology provider with memoQ.
//
// This lives in its own assembly rather than alongside the MT director because
// memoQ loads exactly ONE module per DLL: ModuleManager.tryGetModuleAttribute
// returns a single ModuleAttribute, so a second entry in the same assembly is
// silently ignored — no error, no warning, and the provider simply never appears
// under Options > Terminology plugins. (ModuleAttribute is declared
// AllowMultiple, which is misleading; the loader does not honour it.)
//
// ClassName must match the director's full type name exactly; nothing checks it
// at compile time.
[assembly: Module(
    ModuleName = "Supervertaler terms",
    ClassName = "Supervertaler.MemoQ.SupervertalerTBPluginDirector")]
