# CodeProject fixture

A Unity-shaped project for `code-index` tests (`specs/SPEC-code-index.md`, Testing). Unity never
opens it. The `.csproj` files imitate the legacy projects Unity generates. `UnityEngine` is the
stub in `Library/Stubs/` instead of the editor's DLLs, so no Unity install is needed; it's
compiled so symbols bind, never indexed. `Library/ScriptAssemblies/Missing.dll` and
`Library/Bcl/mscorlib.dll` don't exist on purpose: the loader reports the first and falls back
to the running runtime's assemblies for the second.

Tests copy this folder to a temp directory before indexing it.
