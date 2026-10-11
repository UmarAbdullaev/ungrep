# CodeProject fixture

A Unity-shaped project for `code-index` tests (`specs/SPEC-code-index.md`, Testing). Unity never
opens it. The `.csproj` files imitate the legacy projects Unity generates. `UnityEngine` is the
stub in `Library/Stubs/` instead of the editor's DLLs, so no Unity install is needed; it's
compiled so symbols bind, never indexed. `Library/ScriptAssemblies/Missing.dll` and
`Library/Bcl/mscorlib.dll` don't exist on purpose: the loader reports the first and falls back
to the running runtime's assemblies for the second.

Tests copy this folder to a temp directory before indexing it.

The Unity YAML under `Assets/Prefabs`, `Assets/Scenes`, `Assets/Materials`, `Assets/Data` and
`Assets/AddressableAssetsData` is hand-written for `asset-index` tests
(`specs/SPEC-asset-index.md`, Testing): a prefab and its variant, a scene with nested prefab
instances, overrides and UnityEvent calls, a material, ScriptableObjects and an Addressables
group. GUIDs are readable patterns (`b1000…01`). `Data/Baked.asset` is binary on purpose,
`Scenes/New.unity` has no `.meta` on purpose, and `Textures/Grid.png` is a text placeholder.
`ILockable` extends `IOpenable` and `Door` implements it, so `IOpenable` has an implementer
through an extending interface (B1c).
